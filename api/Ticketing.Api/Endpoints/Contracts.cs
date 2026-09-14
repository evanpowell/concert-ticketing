namespace Ticketing.Api.Endpoints;

public record ShowSummary(int ShowId, string Title, string Artist, DateTimeOffset StartsAt, string VenueName);

public record SeatView(int ShowSeatId, string Section, string RowLabel, int SeatNumber, string Status, int PriceCents);

public record CreateHoldRequest(int[] SeatIds, string Email);

public record HoldView(int HoldId, DateTimeOffset ExpiresAt, int[] ShowSeatIds);

public record OrderView(int OrderId, string Email, int TotalCents, DateTimeOffset CreatedAt, TicketView[] Tickets);

public record TicketView(int TicketId, int ShowSeatId, string Section, string RowLabel, int SeatNumber, int PriceCents);
