using Anvilboard.Application.Automation;

namespace Anvilboard.Api.Middleware;

/// <summary>
/// Echoes the request's resolved correlation id back on the response as <c>X-Correlation-Id</c>, so
/// a REST caller can tie its request to the matching audit record, log lines, and realtime envelope
/// the same way a CLI/MCP caller reads it from the response envelope.
/// </summary>
/// <remarks>
/// Runs ahead of authentication so denials carry the header too — those are exactly the responses a
/// caller most needs to correlate against server-side logs. The header is written from
/// <see cref="HttpResponse.OnStarting(Func{Task})"/> rather than up front because a later component
/// may clear the response headers when it takes over the response.
/// </remarks>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context, CorrelationContext correlation)
    {
        context.Response.OnStarting(static state =>
        {
            var (response, correlationId) = ((HttpResponse, string))state;
            response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        }, (context.Response, correlation.CorrelationId));

        await next(context);
    }
}
