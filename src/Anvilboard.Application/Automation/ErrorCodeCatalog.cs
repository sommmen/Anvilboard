namespace Anvilboard.Application.Automation;

/// <summary>
/// The stable anticipated-failure codes and their HTTP status, mirroring tech-design.md §7.7
/// "Error Catalog &amp; Traceability" verbatim. This is the single source of truth for
/// code-to-status mapping shared by <see cref="ErrorCatalogTranslator"/> and any channel that
/// needs to translate a catalog code without an exception instance (e.g. a
/// <c>AuthenticationResult</c>/<c>AuthorizationResult</c>/<c>TransitionValidationResult</c> value
/// carrying only an <c>ErrorCode</c> string). <c>INTERNAL_ERROR</c> is deliberately absent from
/// this table: it is reserved for unanticipated faults and is never a documented contract value.
/// </summary>
public static class ErrorCodeCatalog
{
    public const string InternalError = "INTERNAL_ERROR";

    private static readonly IReadOnlyDictionary<string, int> HttpStatusByCode = new Dictionary<string, int>
    {
        ["AUTHENTICATION_REQUIRED"] = 401,
        ["CREDENTIAL_INVALID_OR_EXPIRED"] = 401,
        ["WORKSPACE_ACCESS_DENIED"] = 403,
        ["VALIDATION_FAILED"] = 400,
        ["REFERENCED_ENTITY_NOT_FOUND"] = 404,
        ["INVALID_WORKFLOW_TRANSITION"] = 409,
        ["RESOURCE_ALREADY_EXISTS"] = 409,
        ["CONCURRENCY_CONFLICT"] = 409,
        ["IDEMPOTENCY_KEY_REUSED"] = 409,
        ["RATE_LIMITED"] = 429,
        ["PROVIDER_UNAVAILABLE"] = 502,
        ["INTEGRATION_PAUSED"] = 409,
        ["BACKUP_INTEGRITY_INVALID"] = 422,
        ["SYNC_CONFLICT"] = 409,
        ["ARTIFACT_STORE_UNAVAILABLE"] = 502,
    };

    /// <summary>Returns the documented HTTP status for <paramref name="errorCode"/>, or 500 (with
    /// <paramref name="errorCode"/> treated as <see cref="InternalError"/>) for any code outside
    /// the §7.7 catalog.</summary>
    public static int HttpStatusFor(string errorCode) =>
        HttpStatusByCode.GetValueOrDefault(errorCode, 500);
}
