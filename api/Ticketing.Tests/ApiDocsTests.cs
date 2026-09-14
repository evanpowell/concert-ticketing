using System.Net;

namespace Ticketing.Tests;

/// <summary>
/// The deployed link must show something a human can use. These assert the
/// OpenAPI document and the interactive docs UI are both served.
/// </summary>
[Collection(nameof(OracleCollection))]
public class ApiDocsTests(OracleFixture oracle)
{
    [Fact]
    public async Task Openapi_document_is_served_and_describes_the_endpoints()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadAsStringAsync();
        Assert.Contains("/api/shows", document);
        Assert.Contains("/api/holds/{holdId}/confirm", document);
    }

    [Fact]
    public async Task Docs_route_serves_the_interactive_docs_ui()
    {
        using var factory = new ApiFactory(oracle.ConnectionString);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/docs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/html", response.Content.Headers.ContentType?.MediaType ?? "");
    }
}
