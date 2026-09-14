using Microsoft.EntityFrameworkCore;
using Oracle.ManagedDataAccess.Client;
using Ticketing.Api.Data;

namespace Ticketing.Api.Domain;

public sealed class HoldService(TicketingDbContext db)
{
    public static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(15);

    /// <summary>ORA-00054: resource busy and acquire with NOWAIT specified.</summary>
    private const int OracleResourceBusy = 54;

    public async Task<HoldOutcome> CreateHoldAsync(
        int showId, IReadOnlyList<int> seatIds, string email, CancellationToken ct)
    {
        if (seatIds.Count == 0)
            return new HoldOutcome.ShowOrSeatNotFound();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var now = await CurrentDatabaseTimeAsync(ct);
            var rows = await LockShowSeatsAsync(showId, seatIds, ct);

            if (rows.Count != seatIds.Count)
                return new HoldOutcome.ShowOrSeatNotFound();

            if (rows.Any(row => !IsClaimable(row, now)))
                return new HoldOutcome.SeatUnavailable();

            var hold = new SeatHold
            {
                ShowId = showId,
                Email = email,
                CreatedAt = now,
                ExpiresAt = now + HoldDuration,
                Status = HoldStatus.Active,
            };
            db.SeatHolds.Add(hold);
            await db.SaveChangesAsync(ct);   // assigns hold.HoldId

            foreach (var row in rows)
            {
                row.Status = SeatStatus.Held;
                row.HoldId = hold.HoldId;
                row.ExpiresAt = hold.ExpiresAt;
            }
            await db.SaveChangesAsync(ct);

            await tx.CommitAsync(ct);
            return new HoldOutcome.Created(hold.HoldId, hold.ExpiresAt, rows.Select(r => r.ShowSeatId).ToArray());
        }
        catch (OracleException ex) when (ex.Number == OracleResourceBusy)
        {
            // Another transaction holds the lock. NOWAIT means we find out immediately
            // instead of queueing behind it, which is exactly what we want here.
            await tx.RollbackAsync(ct);
            return new HoldOutcome.SeatUnavailable();
        }
    }

    /// <summary>
    /// A seat is claimable when it is available, or when it is held by a hold that
    /// has already expired. This is the authoritative expiry check: it happens
    /// inside the lock and does not trust the background sweeper.
    /// </summary>
    private static bool IsClaimable(ShowSeat seat, DateTimeOffset now) =>
        seat.Status == SeatStatus.Available ||
        (seat.Status == SeatStatus.Held && seat.ExpiresAt is { } expiry && expiry <= now);

    /// <summary>
    /// Takes pessimistic row locks on the requested seats. This is the only raw SQL
    /// in the application: EF Core cannot express FOR UPDATE.
    /// </summary>
    private async Task<List<ShowSeat>> LockShowSeatsAsync(
        int showId, IReadOnlyList<int> seatIds, CancellationToken ct)
    {
        // Placeholders are generated from the *count* of ids, never from user input,
        // so this string interpolation cannot carry an injection.
        var placeholders = string.Join(", ", seatIds.Select((_, i) => $":seat{i}"));

        var parameters = new List<object> { new OracleParameter("showId", showId) };
        parameters.AddRange(seatIds.Select((id, i) => new OracleParameter($"seat{i}", id)));

        var sql = $"""
            SELECT * FROM show_seat
             WHERE show_id = :showId
               AND seat_id IN ({placeholders})
             ORDER BY show_seat_id
               FOR UPDATE NOWAIT
            """;

        return await db.ShowSeats.FromSqlRaw(sql, parameters.ToArray()).ToListAsync(ct);
    }

    /// <summary>
    /// Reads the clock from the database rather than the API process, so that clock
    /// skew between application servers cannot affect expiry decisions.
    /// </summary>
    private async Task<DateTimeOffset> CurrentDatabaseTimeAsync(CancellationToken ct) =>
        await db.Database.SqlQueryRaw<DateTimeOffset>(
            """SELECT SYSTIMESTAMP AS "Value" FROM dual""").SingleAsync(ct);
}
