using System.Net;
using System.Text.Json;
using Anvilboard.Api.Tests.Testing;

namespace Anvilboard.Api.Tests.Issues;

/// <summary>
/// Exercises the reference lists the board's filter controls need. Without them a client can only
/// offer a free-text box for values that are really identifiers (MAJ-010).
/// </summary>
public sealed class TaxonomyEndpointTests
{
    [Fact]
    public async Task Projects_NewWorkspace_ReturnsEmptyArray()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/api/projects", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Empty(payload.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task Labels_NewWorkspace_ReturnsEmptyArray()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/api/labels", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Empty(payload.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task LinkTypes_ReturnsSuggestedVocabulary()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/api/issue-link-types", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        var types = payload.RootElement.EnumerateArray().Select(element => element.GetString()).ToList();
        Assert.Contains("RELATED", types);
        Assert.Contains("BLOCKS", types);
        Assert.Contains("DUPLICATES", types);
    }

    [Theory]
    [InlineData("/api/projects")]
    [InlineData("/api/labels")]
    [InlineData("/api/issue-link-types")]
    public async Task Taxonomy_Unauthenticated_ReturnsUnauthorized(string route)
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(route, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(ApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", await ApiFactory.BootstrapAndGetSessionCookieAsync(client));
        return client;
    }
}
