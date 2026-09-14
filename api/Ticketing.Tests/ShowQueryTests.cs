using System.Net;
using System.Net.Http.Json;

namespace Ticketing.Tests;

[Collection(nameof(OracleCollection))]
public class ShowQueryTests(OracleFixture oracle)
{
    [Fact]
    public async Task Lists_all_seeded_shows()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();

        var shows = await client.GetFromJsonAsync<List<ShowSummaryDto>>("/api/shows");

        Assert.NotNull(shows);
        Assert.Equal(3, shows!.Count);
        Assert.Contains(shows, s => s.Artist == "Lankum");
        // A show time belongs to the venue, not the viewer. The client needs the
        // venue's IANA zone to render "8:00 PM" the same way everywhere on earth.
        Assert.All(shows, s => Assert.Equal("America/Los_Angeles", s.VenueTimeZone));
    }

    [Fact]
    public async Task Seat_map_returns_one_hundred_seats()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();

        var shows = await client.GetFromJsonAsync<List<ShowSummaryDto>>("/api/shows");
        var seats = await client.GetFromJsonAsync<List<SeatDto>>($"/api/shows/{shows![0].ShowId}/seats");

        Assert.NotNull(seats);
        Assert.Equal(100, seats!.Count);
        Assert.Contains(seats, s => s.Section == "A" && s.PriceCents == 8500);
        Assert.Contains(seats, s => s.Section == "B" && s.PriceCents == 6000);
        Assert.All(seats, s => Assert.Contains(s.Status, new[] { "AVAILABLE", "HELD", "SOLD" }));
        // The hold endpoint takes seatIds, so the seat map must expose them or no
        // client can construct a hold request.
        Assert.All(seats, s => Assert.True(s.SeatId > 0));
    }

    [Fact]
    public async Task Show_detail_returns_the_requested_show()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();

        var shows = await client.GetFromJsonAsync<List<ShowSummaryDto>>("/api/shows");
        var show = await client.GetFromJsonAsync<ShowSummaryDto>($"/api/shows/{shows![0].ShowId}");

        Assert.NotNull(show);
        Assert.Equal(shows[0].ShowId, show!.ShowId);
        Assert.Equal(shows[0].Artist, show.Artist);
    }

    [Fact]
    public async Task Unknown_show_returns_404()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/shows/999999/seats");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public record ShowSummaryDto(int ShowId, string Title, string Artist, DateTimeOffset StartsAt, string VenueName, string VenueTimeZone);
    public record SeatDto(int ShowSeatId, int SeatId, string Section, string RowLabel, int SeatNumber, string Status, int PriceCents);
}
