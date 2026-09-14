namespace Ticketing.Api.Domain;

public class Venue
{
    public int VenueId { get; set; }
    public string Name { get; set; } = "";
    public string City { get; set; } = "";
}

public class Seat
{
    public int SeatId { get; set; }
    public int VenueId { get; set; }
    public string Section { get; set; } = "";
    public string RowLabel { get; set; } = "";
    public int SeatNumber { get; set; }
}

public class ShowEvent
{
    public int ShowId { get; set; }
    public int VenueId { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public DateTimeOffset StartsAt { get; set; }
    public Venue Venue { get; set; } = null!;
}

public class ShowSeat
{
    public int ShowSeatId { get; set; }
    public int ShowId { get; set; }
    public int SeatId { get; set; }
    public string Status { get; set; } = SeatStatus.Available;
    public int PriceCents { get; set; }
    public int? HoldId { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public Seat Seat { get; set; } = null!;
}

public class SeatHold
{
    public int HoldId { get; set; }
    public int ShowId { get; set; }
    public string Email { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string Status { get; set; } = HoldStatus.Active;
}

public class CustomerOrder
{
    public int OrderId { get; set; }
    public int HoldId { get; set; }
    public string Email { get; set; } = "";
    public int TotalCents { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class Ticket
{
    public int TicketId { get; set; }
    public int OrderId { get; set; }
    public int ShowSeatId { get; set; }
    public int PriceCents { get; set; }
}
