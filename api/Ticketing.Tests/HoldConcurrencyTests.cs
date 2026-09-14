using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Oracle.ManagedDataAccess.Client;

namespace Ticketing.Tests;

[Collection(nameof(OracleCollection))]
public class HoldConcurrencyTests(OracleFixture oracle)
{
    private const int Attempts = 10;

    [Fact]
    public async Task Exactly_one_of_many_concurrent_holds_on_the_same_seat_wins()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);

        var (showId, seatId) = await TestData.PickAvailableSeatAsync(oracle.ConnectionString);

        // Release all racers at the same instant so they genuinely collide.
        var gate = new TaskCompletionSource();

        var racers = Enumerable.Range(0, Attempts).Select(async i =>
        {
            var client = factory.CreateClient();
            await gate.Task;
            return await client.PostAsJsonAsync(
                $"/api/shows/{showId}/holds",
                new { seatIds = new[] { seatId }, email = $"racer{i}@example.com" });
        }).ToArray();

        gate.SetResult();
        var responses = await Task.WhenAll(racers);

        var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var conflicted = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        Assert.Equal(1, created);
        Assert.Equal(Attempts - 1, conflicted);
    }

    /// <summary>
    /// The racing test above would pass even if the requests never truly collided:
    /// a loser that arrives after the winner commits sees the seat already HELD and
    /// is rejected without the lock mattering. This test removes that ambiguity by
    /// holding a row lock from an outside transaction, so the ONLY way to get a 409
    /// is ORA-00054 from FOR UPDATE NOWAIT. The elapsed-time assertion proves NOWAIT
    /// specifically: without it, the request would block behind the lock instead.
    /// </summary>
    [Fact]
    public async Task A_seat_locked_by_another_transaction_is_rejected_immediately()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();
        var (showId, seatId) = await TestData.PickAvailableSeatAsync(oracle.ConnectionString);

        await using var blocker = new OracleConnection(oracle.ConnectionString);
        await blocker.OpenAsync();
        using var blockingTx = blocker.BeginTransaction();

        await using (var lockCommand = blocker.CreateCommand())
        {
            lockCommand.Transaction = blockingTx;
            lockCommand.CommandText =
                "SELECT show_seat_id FROM show_seat WHERE show_id = :showId AND seat_id = :seatId FOR UPDATE";
            lockCommand.Parameters.Add(new OracleParameter("showId", showId));
            lockCommand.Parameters.Add(new OracleParameter("seatId", seatId));
            await lockCommand.ExecuteScalarAsync();
        }

        var elapsed = Stopwatch.StartNew();
        var response = await client.PostAsJsonAsync(
            $"/api/shows/{showId}/holds",
            new { seatIds = new[] { seatId }, email = "blocked@example.com" });
        elapsed.Stop();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(5),
            $"Expected an immediate rejection from NOWAIT, but it took {elapsed.Elapsed}.");

        blockingTx.Rollback();
    }
}
