using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Data;

namespace Ticketing.Api.Domain;

/// <summary>
/// Cosmetic cleanup only. Booking correctness does not depend on this running --
/// HoldService re-checks expiry inside its own transaction. This exists so the
/// seat map and any reporting do not show stale HELD rows for up to a minute.
/// </summary>
public sealed class ExpiredHoldSweeper(IServiceProvider services, ILogger<ExpiredHoldSweeper> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    public static async Task<int> SweepAsync(TicketingDbContext db, CancellationToken ct)
    {
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>(
            """SELECT SYSTIMESTAMP AS "Value" FROM dual""").SingleAsync(ct);

        var staleSeats = await db.ShowSeats
            .Where(ss => ss.Status == SeatStatus.Held && ss.ExpiresAt != null && ss.ExpiresAt <= now)
            .ToListAsync(ct);

        if (staleSeats.Count == 0)
            return 0;

        var holdIds = staleSeats.Where(s => s.HoldId != null).Select(s => s.HoldId!.Value).Distinct().ToList();

        foreach (var seat in staleSeats)
        {
            seat.Status = SeatStatus.Available;
            seat.HoldId = null;
            seat.ExpiresAt = null;
        }

        var holds = await db.SeatHolds
            .Where(h => holdIds.Contains(h.HoldId) && h.Status == HoldStatus.Active)
            .ToListAsync(ct);

        foreach (var hold in holds)
            hold.Status = HoldStatus.Expired;

        await db.SaveChangesAsync(ct);
        return staleSeats.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
                var swept = await SweepAsync(db, stoppingToken);

                if (swept > 0)
                    logger.LogInformation("Swept {Count} expired seat holds", swept);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed sweep is not fatal: correctness lives in HoldService.
                logger.LogError(ex, "Expired-hold sweep failed; will retry next interval");
            }
        }
    }
}
