using System.Net;
using System.Net.Http.Json;

namespace Ticketing.Tests;

[Collection(nameof(OracleCollection))]
public class ErrorContractTests(OracleFixture oracle)
{
    [Fact]
    public async Task Conflicts_are_returned_as_problem_details()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();

        var (showId, seatId) = await TestData.PickAvailableSeatAsync(oracle.ConnectionString);
        await client.PostAsJsonAsync(
            $"/api/shows/{showId}/holds", new { seatIds = new[] { seatId }, email = "first@example.com" });

        var second = await client.PostAsJsonAsync(
            $"/api/shows/{showId}/holds", new { seatIds = new[] { seatId }, email = "second@example.com" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);

        var problem = await second.Content.ReadFromJsonAsync<ProblemDto>();
        Assert.Equal(409, problem!.Status);
        Assert.False(string.IsNullOrWhiteSpace(problem.Title));
    }

    /// <summary>
    /// The public demo has no authentication, so write endpoints are rate limited
    /// per client IP. Each test gets its own ApiFactory and therefore its own
    /// limiter state, so this cannot bleed into other tests.
    /// </summary>
    [Fact]
    public async Task Write_endpoints_are_rate_limited()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();

        var (showId, seatId) = await TestData.PickAvailableSeatAsync(oracle.ConnectionString);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 40; i++)
        {
            var response = await client.PostAsJsonAsync(
                $"/api/shows/{showId}/holds",
                new { seatIds = new[] { seatId }, email = $"flood{i}@example.com" });
            statuses.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    private record ProblemDto(string? Title, int Status, string? Detail);
}
