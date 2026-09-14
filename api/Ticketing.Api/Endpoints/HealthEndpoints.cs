using Ticketing.Api.Data;

namespace Ticketing.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health", async (TicketingDbContext db, CancellationToken ct) =>
        {
            var reachable = await db.Database.CanConnectAsync(ct);

            return reachable
                ? Results.Ok(new { status = "ok", database = true })
                : Results.Json(new { status = "degraded", database = false }, statusCode: 503);
        });
    }
}
