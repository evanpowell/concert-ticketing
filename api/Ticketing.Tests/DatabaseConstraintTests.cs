using System.Net.Http.Json;
using Oracle.ManagedDataAccess.Client;

namespace Ticketing.Tests;

/// <summary>
/// Application logic *should* prevent double-selling; the database *guarantees* it.
/// These tests bypass the service layer entirely to prove the guarantee holds even
/// if the booking code were wrong.
/// </summary>
[Collection(nameof(OracleCollection))]
public class DatabaseConstraintTests(OracleFixture oracle)
{
    [Fact]
    public async Task A_show_seat_cannot_be_ticketed_twice_even_bypassing_the_service()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();

        var (showId, seatId) = await TestData.PickAvailableSeatAsync(oracle.ConnectionString);
        var created = await client.PostAsJsonAsync(
            $"/api/shows/{showId}/holds", new { seatIds = new[] { seatId }, email = "buyer@example.com" });
        var hold = await created.Content.ReadFromJsonAsync<HoldViewDto>();
        var confirmed = await client.PostAsync($"/api/holds/{hold!.HoldId}/confirm", null);
        var order = await confirmed.Content.ReadFromJsonAsync<OrderIdDto>();

        await using var connection = new OracleConnection(oracle.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO ticket (order_id, show_seat_id, price_cents) VALUES (:orderId, :showSeatId, 8500)";
        command.Parameters.Add(new OracleParameter("orderId", order!.OrderId));
        command.Parameters.Add(new OracleParameter("showSeatId", hold.ShowSeatIds[0]));

        var ex = await Assert.ThrowsAsync<OracleException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal(1, ex.Number);   // ORA-00001: unique constraint violated
        Assert.Contains("UQ_TICKET_SHOW_SEAT", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ck_held_consistency makes "HELD with no hold" and "available with a stale
    /// hold reference" unrepresentable, so no code path can leave a seat in either state.
    /// </summary>
    [Fact]
    public async Task A_seat_cannot_be_held_without_a_hold_reference()
    {
        await using var connection = new OracleConnection(oracle.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE show_seat SET status = 'HELD', hold_id = NULL, expires_at = NULL WHERE show_seat_id = 1";

        var ex = await Assert.ThrowsAsync<OracleException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal(2290, ex.Number);   // ORA-02290: check constraint violated
        Assert.Contains("CK_HELD_CONSISTENCY", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private record HoldViewDto(int HoldId, DateTimeOffset ExpiresAt, int[] ShowSeatIds);
    private record OrderIdDto(int OrderId);
}
