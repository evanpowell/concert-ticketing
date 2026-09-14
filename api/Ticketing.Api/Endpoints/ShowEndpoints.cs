using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Endpoints;

public static class ShowEndpoints
{
    public static void MapShowEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/shows");

        group.MapGet("/", async (TicketingDbContext db, CancellationToken ct) =>
            await db.Shows
                .OrderBy(s => s.StartsAt)
                .Select(s => new ShowSummary(s.ShowId, s.Title, s.Artist, s.StartsAt, s.Venue.Name))
                .ToListAsync(ct));

        group.MapGet("/{showId:int}", async (int showId, TicketingDbContext db, CancellationToken ct) =>
        {
            var show = await db.Shows
                .Where(s => s.ShowId == showId)
                .Select(s => new ShowSummary(s.ShowId, s.Title, s.Artist, s.StartsAt, s.Venue.Name))
                .FirstOrDefaultAsync(ct);

            return show is null
                ? Results.Problem(statusCode: 404, title: "Show not found")
                : Results.Ok(show);
        });

        group.MapGet("/{showId:int}/seats", async (int showId, TicketingDbContext db, CancellationToken ct) =>
        {
            var showExists = await db.Shows.AnyAsync(s => s.ShowId == showId, ct);
            if (!showExists)
                return Results.Problem(statusCode: 404, title: "Show not found");

            var seats = await db.ShowSeats
                .Where(ss => ss.ShowId == showId)
                .OrderBy(ss => ss.Seat.Section).ThenBy(ss => ss.Seat.RowLabel).ThenBy(ss => ss.Seat.SeatNumber)
                .Select(ss => new SeatView(
                    ss.ShowSeatId,
                    ss.SeatId,
                    ss.Seat.Section,
                    ss.Seat.RowLabel,
                    ss.Seat.SeatNumber,
                    // A hold that has expired but not yet been swept reads as available.
                    ss.Status == SeatStatus.Held && ss.ExpiresAt != null && ss.ExpiresAt <= DateTimeOffset.UtcNow
                        ? SeatStatus.Available
                        : ss.Status,
                    ss.PriceCents))
                .ToListAsync(ct);

            return Results.Ok(seats);
        });
    }
}
