using Ticketing.Api.Domain;

namespace Ticketing.Api.Endpoints;

public static class HoldEndpoints
{
    public static void MapHoldEndpoints(this WebApplication app)
    {
        app.MapPost("/api/shows/{showId:int}/holds", async (
            int showId, CreateHoldRequest request, HoldService holds, CancellationToken ct) =>
        {
            var outcome = await holds.CreateHoldAsync(showId, request.SeatIds, request.Email, ct);

            return outcome switch
            {
                HoldOutcome.Created c =>
                    Results.Created($"/api/holds/{c.HoldId}", new HoldView(c.HoldId, c.ExpiresAt, c.ShowSeatIds)),
                HoldOutcome.ShowOrSeatNotFound =>
                    Results.Problem(statusCode: 404, title: "Show or seat not found"),
                _ =>
                    Results.Problem(statusCode: 409, title: "One or more seats are no longer available"),
            };
        });

        app.MapPost("/api/holds/{holdId:int}/confirm", async (
            int holdId, HoldService holds, CancellationToken ct) =>
        {
            var outcome = await holds.ConfirmHoldAsync(holdId, ct);

            return outcome switch
            {
                ConfirmOutcome.Confirmed c =>
                    Results.Created($"/api/orders/{c.OrderId}", new { orderId = c.OrderId }),
                ConfirmOutcome.HoldNotFound =>
                    Results.Problem(statusCode: 404, title: "Hold not found"),
                _ =>
                    Results.Problem(statusCode: 410, title: "This hold has expired or was already used"),
            };
        });
    }
}
