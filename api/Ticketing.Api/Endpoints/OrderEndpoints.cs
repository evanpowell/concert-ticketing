using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Data;

namespace Ticketing.Api.Endpoints;

public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this WebApplication app)
    {
        app.MapGet("/api/orders/{orderId:int}", async (
            int orderId, TicketingDbContext db, CancellationToken ct) =>
        {
            var order = await db.Orders.FirstOrDefaultAsync(o => o.OrderId == orderId, ct);
            if (order is null)
                return Results.Problem(statusCode: 404, title: "Order not found");

            var tickets = await (
                from ticket in db.Tickets
                join showSeat in db.ShowSeats on ticket.ShowSeatId equals showSeat.ShowSeatId
                join seat in db.Seats on showSeat.SeatId equals seat.SeatId
                where ticket.OrderId == orderId
                orderby seat.Section, seat.RowLabel, seat.SeatNumber
                select new TicketView(
                    ticket.TicketId, ticket.ShowSeatId,
                    seat.Section, seat.RowLabel, seat.SeatNumber, ticket.PriceCents))
                .ToArrayAsync(ct);

            return Results.Ok(new OrderView(order.OrderId, order.Email, order.TotalCents, order.CreatedAt, tickets));
        });
    }
}
