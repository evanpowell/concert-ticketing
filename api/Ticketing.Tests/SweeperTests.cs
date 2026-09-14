using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Domain;

namespace Ticketing.Tests;

[Collection(nameof(OracleCollection))]
public class SweeperTests(OracleFixture oracle)
{
    [Fact]
    public async Task Sweep_releases_expired_holds_and_leaves_active_ones_alone()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();

        var expired = await CreateHoldAsync(client);
        var active = await CreateHoldAsync(client);
        await TestData.ExpireHoldAsync(oracle.ConnectionString, expired.HoldId);

        await using var db = TestData.NewContext(oracle.ConnectionString);
        var swept = await ExpiredHoldSweeper.SweepAsync(db, CancellationToken.None);

        Assert.True(swept >= 1);

        var expiredSeat = await db.ShowSeats.AsNoTracking()
            .SingleAsync(ss => ss.ShowSeatId == expired.ShowSeatIds[0]);
        Assert.Equal("AVAILABLE", expiredSeat.Status);
        Assert.Null(expiredSeat.HoldId);

        var activeSeat = await db.ShowSeats.AsNoTracking()
            .SingleAsync(ss => ss.ShowSeatId == active.ShowSeatIds[0]);
        Assert.Equal("HELD", activeSeat.Status);
        Assert.NotNull(activeSeat.HoldId);

        var expiredHold = await db.SeatHolds.AsNoTracking().SingleAsync(h => h.HoldId == expired.HoldId);
        Assert.Equal("EXPIRED", expiredHold.Status);
    }

    private async Task<HoldViewDto> CreateHoldAsync(HttpClient client)
    {
        var (showId, seatId) = await TestData.PickAvailableSeatAsync(oracle.ConnectionString);
        var response = await client.PostAsJsonAsync(
            $"/api/shows/{showId}/holds", new { seatIds = new[] { seatId }, email = "buyer@example.com" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<HoldViewDto>())!;
    }

    public record HoldViewDto(int HoldId, DateTimeOffset ExpiresAt, int[] ShowSeatIds);
}
