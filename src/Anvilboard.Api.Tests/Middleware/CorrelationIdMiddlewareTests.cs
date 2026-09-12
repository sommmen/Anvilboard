using System.Net;
using Anvilboard.Api.Tests.Testing;

namespace Anvilboard.Api.Tests.Middleware;

public sealed class CorrelationIdMiddlewareTests
{
    private const string HeaderName = "X-Correlation-Id";

    [Fact]
    public async Task Response_EchoesClientSuppliedCorrelationId()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderName, "correlation-from-client");

        var response = await client.GetAsync("/api/issues", CancellationToken.None);

        Assert.Equal("correlation-from-client", response.Headers.GetValues(HeaderName).Single());
    }

    [Fact]
    public async Task Response_GeneratesCorrelationIdWhenClientSuppliesNone()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/issues", CancellationToken.None);

        var correlationId = response.Headers.GetValues(HeaderName).Single();
        Assert.True(Guid.TryParse(correlationId, out _));
    }

    [Fact]
    public async Task Response_EchoesCorrelationIdOnAuthenticationDenial()
    {
        // A denial is exactly the response a caller most needs to correlate against server logs,
        // so the header must survive the enforcement point rejecting the request.
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderName, "denied-request");

        var response = await client.GetAsync("/api/issues", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("denied-request", response.Headers.GetValues(HeaderName).Single());
    }
}
