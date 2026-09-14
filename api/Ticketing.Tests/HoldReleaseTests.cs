using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;

namespace Ticketing.Tests;

[Collection(nameof(OracleCollection))]
public class HoldReleaseTests(OracleFixture oracle)
{
    [Fact]
    public async Task Releasing_a_hold_makes_its_seats_available_again()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();
        var (showId, seatId) = await TestData.PickAvailableSeatAsync(oracle.ConnectionString);

        var created = await client.PostAsJsonAsync(
            $"/api/shows/{showId}/holds", new { seatIds = new[] { seatId }, email = "buyer@example.com" });
        var hold = await created.Content.ReadFromJsonAsync<HoldViewDto>();

        var response = await client.DeleteAsync($"/api/holds/{hold!.HoldId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var db = TestData.NewContext(oracle.ConnectionString);
        var seat = await db.ShowSeats.SingleAsync(ss => ss.ShowSeatId == hold.ShowSeatIds[0]);
        Assert.Equal("AVAILABLE", seat.Status);
        Assert.Null(seat.HoldId);
        Assert.Null(seat.ExpiresAt);
    }

    [Fact]
    public async Task Releasing_a_confirmed_hold_returns_410()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();
        var (showId, seatId) = await TestData.PickAvailableSeatAsync(oracle.ConnectionString);

        var created = await client.PostAsJsonAsync(
            $"/api/shows/{showId}/holds", new { seatIds = new[] { seatId }, email = "buyer@example.com" });
        var hold = await created.Content.ReadFromJsonAsync<HoldViewDto>();
        await client.PostAsync($"/api/holds/{hold!.HoldId}/confirm", null);

        var response = await client.DeleteAsync($"/api/holds/{hold.HoldId}");

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }

    private record HoldViewDto(int HoldId, DateTimeOffset ExpiresAt, int[] ShowSeatIds);
}
