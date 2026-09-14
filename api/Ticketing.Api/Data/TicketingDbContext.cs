using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Data;

public class TicketingDbContext(DbContextOptions<TicketingDbContext> options) : DbContext(options)
{
    public DbSet<Venue> Venues => Set<Venue>();
    public DbSet<Seat> Seats => Set<Seat>();
    public DbSet<ShowEvent> Shows => Set<ShowEvent>();
    public DbSet<ShowSeat> ShowSeats => Set<ShowSeat>();
    public DbSet<SeatHold> SeatHolds => Set<SeatHold>();
    public DbSet<CustomerOrder> Orders => Set<CustomerOrder>();
    public DbSet<Ticket> Tickets => Set<Ticket>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Venue>(e =>
        {
            e.ToTable("VENUE");
            e.HasKey(x => x.VenueId);
            e.Property(x => x.VenueId).HasColumnName("VENUE_ID").ValueGeneratedOnAdd();
            e.Property(x => x.Name).HasColumnName("NAME");
            e.Property(x => x.City).HasColumnName("CITY");
        });

        b.Entity<Seat>(e =>
        {
            e.ToTable("SEAT");
            e.HasKey(x => x.SeatId);
            e.Property(x => x.SeatId).HasColumnName("SEAT_ID").ValueGeneratedOnAdd();
            e.Property(x => x.VenueId).HasColumnName("VENUE_ID");
            e.Property(x => x.Section).HasColumnName("SECTION");
            e.Property(x => x.RowLabel).HasColumnName("ROW_LABEL");
            e.Property(x => x.SeatNumber).HasColumnName("SEAT_NUMBER");
        });

        b.Entity<ShowEvent>(e =>
        {
            e.ToTable("SHOW_EVENT");
            e.HasKey(x => x.ShowId);
            e.Property(x => x.ShowId).HasColumnName("SHOW_ID").ValueGeneratedOnAdd();
            e.Property(x => x.VenueId).HasColumnName("VENUE_ID");
            e.Property(x => x.Title).HasColumnName("TITLE");
            e.Property(x => x.Artist).HasColumnName("ARTIST");
            e.Property(x => x.StartsAt).HasColumnName("STARTS_AT");
            e.HasOne(x => x.Venue).WithMany().HasForeignKey(x => x.VenueId);
        });

        b.Entity<ShowSeat>(e =>
        {
            e.ToTable("SHOW_SEAT");
            e.HasKey(x => x.ShowSeatId);
            e.Property(x => x.ShowSeatId).HasColumnName("SHOW_SEAT_ID").ValueGeneratedOnAdd();
            e.Property(x => x.ShowId).HasColumnName("SHOW_ID");
            e.Property(x => x.SeatId).HasColumnName("SEAT_ID");
            e.Property(x => x.Status).HasColumnName("STATUS");
            e.Property(x => x.PriceCents).HasColumnName("PRICE_CENTS");
            e.Property(x => x.HoldId).HasColumnName("HOLD_ID");
            e.Property(x => x.ExpiresAt).HasColumnName("EXPIRES_AT");
            e.HasOne(x => x.Seat).WithMany().HasForeignKey(x => x.SeatId);
        });

        b.Entity<SeatHold>(e =>
        {
            e.ToTable("SEAT_HOLD");
            e.HasKey(x => x.HoldId);
            e.Property(x => x.HoldId).HasColumnName("HOLD_ID").ValueGeneratedOnAdd();
            e.Property(x => x.ShowId).HasColumnName("SHOW_ID");
            e.Property(x => x.Email).HasColumnName("EMAIL");
            e.Property(x => x.CreatedAt).HasColumnName("CREATED_AT");
            e.Property(x => x.ExpiresAt).HasColumnName("EXPIRES_AT");
            e.Property(x => x.Status).HasColumnName("STATUS");
        });

        b.Entity<CustomerOrder>(e =>
        {
            e.ToTable("CUSTOMER_ORDER");
            e.HasKey(x => x.OrderId);
            e.Property(x => x.OrderId).HasColumnName("ORDER_ID").ValueGeneratedOnAdd();
            e.Property(x => x.HoldId).HasColumnName("HOLD_ID");
            e.Property(x => x.Email).HasColumnName("EMAIL");
            e.Property(x => x.TotalCents).HasColumnName("TOTAL_CENTS");
            e.Property(x => x.CreatedAt).HasColumnName("CREATED_AT");
        });

        b.Entity<Ticket>(e =>
        {
            e.ToTable("TICKET");
            e.HasKey(x => x.TicketId);
            e.Property(x => x.TicketId).HasColumnName("TICKET_ID").ValueGeneratedOnAdd();
            e.Property(x => x.OrderId).HasColumnName("ORDER_ID");
            e.Property(x => x.ShowSeatId).HasColumnName("SHOW_SEAT_ID");
            e.Property(x => x.PriceCents).HasColumnName("PRICE_CENTS");
        });
    }
}
