namespace Anvilboard.Application.Automation;

/// <summary>
/// A channel-agnostic representation of a §7.7 catalog error, produced by
/// <see cref="ErrorCatalogTranslator"/>. <c>Anvilboard.Application</c> has no dependency on
/// ASP.NET Core, so this is a plain record rather than
/// <c>Microsoft.AspNetCore.Http.ProblemDetails</c>; REST/CLI/MCP adapters map it onto their own
/// response envelope.
/// </summary>
public sealed record ProblemDetailsResult(int Status, string ErrorCode, string CorrelationId, string? Detail = null);
