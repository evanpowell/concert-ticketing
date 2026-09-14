using Microsoft.EntityFrameworkCore;
using Oracle.ManagedDataAccess.Client;
using Ticketing.Api.Data;

namespace Ticketing.Tests;

public static class TestData
{
    /// <summary>Returns a (showId, seatId) pair that is currently AVAILABLE.</summary>
    public static async Task<(int ShowId, int SeatId)> PickAvailableSeatAsync(string connectionString)
    {
        await using var db = NewContext(connectionString);

        var row = await db.ShowSeats
            .Where(ss => ss.Status == "AVAILABLE")
            .OrderBy(ss => ss.ShowSeatId)
            .Select(ss => new { ss.ShowId, ss.SeatId })
            .FirstAsync();

        return (row.ShowId, row.SeatId);
    }

    public static TicketingDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<TicketingDbContext>().UseOracle(connectionString).Options);

    /// <summary>Backdates a hold and its seats so expiry paths can be tested without waiting.</summary>
    public static async Task ExpireHoldAsync(string connectionString, int holdId)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE seat_hold SET expires_at = SYSTIMESTAMP - INTERVAL '1' MINUTE WHERE hold_id = :holdId";
        command.Parameters.Add(new OracleParameter("holdId", holdId));
        await command.ExecuteNonQueryAsync();

        await using var seats = connection.CreateCommand();
        seats.CommandText =
            "UPDATE show_seat SET expires_at = SYSTIMESTAMP - INTERVAL '1' MINUTE WHERE hold_id = :holdId";
        seats.Parameters.Add(new OracleParameter("holdId", holdId));
        await seats.ExecuteNonQueryAsync();
    }
}
