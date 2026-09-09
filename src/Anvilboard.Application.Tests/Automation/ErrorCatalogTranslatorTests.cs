using Anvilboard.Application.Authorization;
using Anvilboard.Application.Automation;
using Anvilboard.Application.Issues;
using Anvilboard.Application.Workflows;

namespace Anvilboard.Application.Tests.Automation;

public sealed class ErrorCatalogTranslatorTests
{
    [Theory]
    [InlineData("AUTHENTICATION_REQUIRED", 401)]
    [InlineData("CREDENTIAL_INVALID_OR_EXPIRED", 401)]
    [InlineData("WORKSPACE_ACCESS_DENIED", 403)]
    [InlineData("VALIDATION_FAILED", 400)]
    [InlineData("REFERENCED_ENTITY_NOT_FOUND", 404)]
    [InlineData("INVALID_WORKFLOW_TRANSITION", 409)]
    [InlineData("RESOURCE_ALREADY_EXISTS", 409)]
    [InlineData("CONCURRENCY_CONFLICT", 409)]
    [InlineData("IDEMPOTENCY_KEY_REUSED", 409)]
    [InlineData("RATE_LIMITED", 429)]
    [InlineData("PROVIDER_UNAVAILABLE", 502)]
    [InlineData("INTEGRATION_PAUSED", 409)]
    [InlineData("BACKUP_INTEGRITY_INVALID", 422)]
    [InlineData("SYNC_CONFLICT", 409)]
    [InlineData("ARTIFACT_STORE_UNAVAILABLE", 502)]
    public void Translate_ByCode_ReturnsDocumentedHttpStatus(string errorCode, int expectedStatus)
    {
        // tech-design.md §7.7 Error Catalog & Traceability is the single source of truth for
        // every status in this table.
        var result = ErrorCatalogTranslator.Translate(errorCode, "correlation-1");

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(errorCode, result.ErrorCode);
        Assert.Equal("correlation-1", result.CorrelationId);
    }

    [Fact]
    public void Translate_UnknownCode_FallsBackTo500()
    {
        var result = ErrorCatalogTranslator.Translate("SOMETHING_UNDOCUMENTED", "correlation-1");

        Assert.Equal(500, result.Status);
    }

    [Fact]
    public void Translate_WorkspaceAuthorizationException_MapsToItsErrorCode()
    {
        var ex = new WorkspaceAuthorizationException("WORKSPACE_ACCESS_DENIED", "denied");

        var result = ErrorCatalogTranslator.Translate(ex, "correlation-1");

        Assert.Equal(403, result.Status);
        Assert.Equal("WORKSPACE_ACCESS_DENIED", result.ErrorCode);
        Assert.Equal("denied", result.Detail);
    }

    [Fact]
    public void Translate_IssueLinkException_MapsToItsErrorCode()
    {
        var ex = new IssueLinkException("RESOURCE_ALREADY_EXISTS", "already linked");

        var result = ErrorCatalogTranslator.Translate(ex, "correlation-1");

        Assert.Equal(409, result.Status);
        Assert.Equal("RESOURCE_ALREADY_EXISTS", result.ErrorCode);
    }

    [Fact]
    public void Translate_WorkflowTransitionDeniedException_MapsToItsErrorCode()
    {
        var ex = new WorkflowTransitionDeniedException("INVALID_WORKFLOW_TRANSITION", "no such transition");

        var result = ErrorCatalogTranslator.Translate(ex, "correlation-1");

        Assert.Equal(409, result.Status);
        Assert.Equal("INVALID_WORKFLOW_TRANSITION", result.ErrorCode);
    }

    [Fact]
    public void Translate_WorkflowValidationException_MapsToValidationFailed()
    {
        var ex = new WorkflowValidationException("bad config");

        var result = ErrorCatalogTranslator.Translate(ex, "correlation-1");

        Assert.Equal(400, result.Status);
        Assert.Equal("VALIDATION_FAILED", result.ErrorCode);
    }

    [Fact]
    public void Translate_IdempotencyKeyReusedException_MapsTo409()
    {
        var ex = new IdempotencyKeyReusedException("key reused with different payload");

        var result = ErrorCatalogTranslator.Translate(ex, "correlation-1");

        Assert.Equal(409, result.Status);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", result.ErrorCode);
    }

    [Fact]
    public void Translate_UnknownException_FallsBackToInternalError()
    {
        // INTERNAL_ERROR is deliberately absent from the public §7.7 contract table; it must
        // still resolve to HTTP 500 for any unanticipated fault.
        var result = ErrorCatalogTranslator.Translate(new InvalidOperationException("boom"), "correlation-1");

        Assert.Equal(500, result.Status);
        Assert.Equal(ErrorCodeCatalog.InternalError, result.ErrorCode);
    }
}
