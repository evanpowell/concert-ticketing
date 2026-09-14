using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Domain;

namespace Ticketing.Tests;

[Collection(nameof(OracleCollection))]
public class DemoResetTests(OracleFixture oracle)
{
    [Fact]
    public async Task Reset_clears_transactional_data_but_keeps_reference_data()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();

        var (showId, seatId) = await TestData.PickAvailableSeatAsync(oracle.ConnectionString);
        var created = await client.PostAsJsonAsync(
            $"/api/shows/{showId}/holds", new { seatIds = new[] { seatId }, email = "buyer@example.com" });
        var hold = await created.Content.ReadFromJsonAsync<HoldViewDto>();
        await client.PostAsync($"/api/holds/{hold!.HoldId}/confirm", null);

        await using var db = TestData.NewContext(oracle.ConnectionString);
        await DemoResetService.ResetAsync(db, CancellationToken.None);

        // Transactional data is gone.
        Assert.Equal(0, await db.Tickets.CountAsync());
        Assert.Equal(0, await db.Orders.CountAsync());
        Assert.Equal(0, await db.SeatHolds.CountAsync());

        // Every seat is available again.
        Assert.Equal(0, await db.ShowSeats.CountAsync(ss => ss.Status != "AVAILABLE"));
        Assert.Equal(0, await db.ShowSeats.CountAsync(ss => ss.HoldId != null));

        // Reference data is untouched.
        Assert.Equal(3, await db.Shows.CountAsync());
        Assert.Equal(100, await db.Seats.CountAsync());
        Assert.Equal(300, await db.ShowSeats.CountAsync());
    }

    private record HoldViewDto(int HoldId, DateTimeOffset ExpiresAt, int[] ShowSeatIds);
}
