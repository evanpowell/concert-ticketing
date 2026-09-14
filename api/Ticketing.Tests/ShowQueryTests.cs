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

    public record ShowSummaryDto(int ShowId, string Title, string Artist, DateTimeOffset StartsAt, string VenueName);
    public record SeatDto(int ShowSeatId, string Section, string RowLabel, int SeatNumber, string Status, int PriceCents);
}
