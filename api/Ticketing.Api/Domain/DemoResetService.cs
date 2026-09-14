using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Data;

namespace Ticketing.Api.Domain;

/// <summary>
/// The public demo has no authentication, so anyone can buy every seat. Without a
/// periodic reset the deployed link would permanently show a sold-out venue and
/// demonstrate nothing. Deletes transactional data only; venues, seats and shows
/// are reference data and are never touched.
/// </summary>
public sealed class DemoResetService(IServiceProvider services, ILogger<DemoResetService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    public static async Task ResetAsync(TicketingDbContext db, CancellationToken ct)
    {
        // Order matters: TICKET references CUSTOMER_ORDER and SHOW_SEAT, and both
        // CUSTOMER_ORDER and SHOW_SEAT reference SEAT_HOLD. show_seat must be
        // cleared of hold_id before seat_hold rows can be deleted.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM ticket", ct);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM customer_order", ct);
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE show_seat SET status = 'AVAILABLE', hold_id = NULL, expires_at = NULL", ct);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM seat_hold", ct);
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
                await ResetAsync(db, stoppingToken);
                logger.LogInformation("Demo data reset");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Demo reset failed; will retry next interval");
            }
        }
    }
}
