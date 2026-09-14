using System.Net.Http.Json;

namespace Ticketing.Tests;

[Collection(nameof(OracleCollection))]
public class OrderTests(OracleFixture oracle)
{
    [Fact]
    public async Task Order_lookup_returns_tickets_with_seat_detail()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();

        var (showId, seatId) = await TestData.PickAvailableSeatAsync(oracle.ConnectionString);
        var created = await client.PostAsJsonAsync(
            $"/api/shows/{showId}/holds", new { seatIds = new[] { seatId }, email = "buyer@example.com" });
        var hold = await created.Content.ReadFromJsonAsync<HoldViewDto>();

        var confirmed = await client.PostAsync($"/api/holds/{hold!.HoldId}/confirm", null);
        var order = await confirmed.Content.ReadFromJsonAsync<OrderIdDto>();

        var view = await client.GetFromJsonAsync<OrderViewDto>($"/api/orders/{order!.OrderId}");

        Assert.NotNull(view);
        Assert.Equal("buyer@example.com", view!.Email);
        Assert.Single(view.Tickets);
        Assert.True(view.TotalCents > 0);
        Assert.Equal(view.TotalCents, view.Tickets.Sum(t => t.PriceCents));
    }

    [Fact]
    public async Task Health_reports_database_connectivity()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();

        var health = await client.GetFromJsonAsync<HealthDto>("/health");

        Assert.Equal("ok", health!.Status);
        Assert.True(health.Database);
    }

    private record HoldViewDto(int HoldId, DateTimeOffset ExpiresAt, int[] ShowSeatIds);
    private record OrderIdDto(int OrderId);
    private record OrderViewDto(int OrderId, string Email, int TotalCents, DateTimeOffset CreatedAt, TicketDto[] Tickets);
    private record TicketDto(int TicketId, int ShowSeatId, string Section, string RowLabel, int SeatNumber, int PriceCents);
    private record HealthDto(string Status, bool Database);
}
