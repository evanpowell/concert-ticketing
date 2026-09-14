using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;

namespace Ticketing.Tests;

[Collection(nameof(OracleCollection))]
public class HoldLifecycleTests(OracleFixture oracle)
{
    [Fact]
    public async Task Confirming_a_hold_creates_an_order_and_marks_seats_sold()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();
        var hold = await CreateHoldAsync(client);

        var response = await client.PostAsync($"/api/holds/{hold.HoldId}/confirm", null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var db = TestData.NewContext(oracle.ConnectionString);
        var seat = await db.ShowSeats.SingleAsync(ss => ss.ShowSeatId == hold.ShowSeatIds[0]);
        Assert.Equal("SOLD", seat.Status);
        Assert.Null(seat.HoldId);
        Assert.Null(seat.ExpiresAt);
        Assert.Equal(1, await db.Tickets.CountAsync(t => t.ShowSeatId == seat.ShowSeatId));
    }

    [Fact]
    public async Task A_hold_cannot_be_confirmed_twice()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();
        var hold = await CreateHoldAsync(client);

        var first = await client.PostAsync($"/api/holds/{hold.HoldId}/confirm", null);
        var second = await client.PostAsync($"/api/holds/{hold.HoldId}/confirm", null);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, second.StatusCode);
    }

    [Fact]
    public async Task An_expired_hold_cannot_be_confirmed()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();
        var hold = await CreateHoldAsync(client);

        await TestData.ExpireHoldAsync(oracle.ConnectionString, hold.HoldId);

        var response = await client.PostAsync($"/api/holds/{hold.HoldId}/confirm", null);

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }

    [Fact]
    public async Task Seats_of_an_expired_hold_can_be_claimed_by_someone_else()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();
        var hold = await CreateHoldAsync(client);
        var (showId, seatId) = hold.Origin;

        await TestData.ExpireHoldAsync(oracle.ConnectionString, hold.HoldId);

        var response = await client.PostAsJsonAsync(
            $"/api/shows/{showId}/holds",
            new { seatIds = new[] { seatId }, email = "second@example.com" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private sealed record HoldUnderTest(int HoldId, int[] ShowSeatIds, (int ShowId, int SeatId) Origin);

    private async Task<HoldUnderTest> CreateHoldAsync(HttpClient client)
    {
        var (showId, seatId) = await TestData.PickAvailableSeatAsync(oracle.ConnectionString);

        var response = await client.PostAsJsonAsync(
            $"/api/shows/{showId}/holds",
            new { seatIds = new[] { seatId }, email = "buyer@example.com" });

        response.EnsureSuccessStatusCode();
        var view = await response.Content.ReadFromJsonAsync<HoldViewDto>();

        return new HoldUnderTest(view!.HoldId, view.ShowSeatIds, (showId, seatId));
    }

    private record HoldViewDto(int HoldId, DateTimeOffset ExpiresAt, int[] ShowSeatIds);
}
