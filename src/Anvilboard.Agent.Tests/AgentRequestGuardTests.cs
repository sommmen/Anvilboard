using Anvilboard.Agent.Automation;

namespace Anvilboard.Agent.Tests;

public sealed class AgentRequestGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RequireIdempotencyKey_RejectsAbsentKeys(string? key)
    {
        var exception = Assert.Throws<AgentRequestException>(() => AgentRequestGuard.RequireIdempotencyKey(key));

        Assert.Equal("VALIDATION_FAILED", exception.ErrorCode);
    }

    [Fact]
    public void RequireIdempotencyKey_RejectsOverlongKeys()
    {
        var key = new string('k', AgentRequestGuard.MaxIdempotencyKeyLength + 1);

        var exception = Assert.Throws<AgentRequestException>(() => AgentRequestGuard.RequireIdempotencyKey(key));

        Assert.Equal("VALIDATION_FAILED", exception.ErrorCode);
    }

    [Fact]
    public void RequireIdempotencyKey_TrimsSurroundingWhitespace()
    {
        // Trimming keeps "  k  " and "k" from being two distinct keys, which would defeat
        // suppression for a caller that pads its keys inconsistently between attempts.
        Assert.Equal("retry-1", AgentRequestGuard.RequireIdempotencyKey("  retry-1  "));
    }

    [Fact]
    public void AgentRequestException_CarriesTheErrorCodeInItsMessage()
    {
        // OperationInvoker flattens exceptions to their message, so the code must survive there for
        // a caller to be able to branch on it.
        var exception = new AgentRequestException("IDEMPOTENCY_KEY_REUSED", "reused");

        Assert.Contains("IDEMPOTENCY_KEY_REUSED", exception.Message, StringComparison.Ordinal);
    }
}

public sealed class CanonicalRequestHashTests
{
    [Fact]
    public void Compute_IsStableForIdenticalInputs()
    {
        Assert.Equal(
            CanonicalRequestHash.Compute("create-issue", "team", "title", null),
            CanonicalRequestHash.Compute("create-issue", "team", "title", null));
    }

    [Fact]
    public void Compute_DiffersWhenAnyInputDiffers()
    {
        Assert.NotEqual(
            CanonicalRequestHash.Compute("create-issue", "team", "title"),
            CanonicalRequestHash.Compute("create-issue", "team", "other-title"));
    }

    [Fact]
    public void Compute_DiffersAcrossOperations()
    {
        // The operation name is part of the identity so the same key reused against a different
        // operation is a reuse conflict rather than a replay of an unrelated result.
        Assert.NotEqual(
            CanonicalRequestHash.Compute("create-issue", "x"),
            CanonicalRequestHash.Compute("assign-issue", "x"));
    }

    [Fact]
    public void Compute_DistinguishesNullFromTheStringNull()
    {
        Assert.NotEqual(
            CanonicalRequestHash.Compute("op", [null]),
            CanonicalRequestHash.Compute("op", "null"));
    }

    [Fact]
    public void Compute_DistinguishesArgumentBoundaries()
    {
        // Without a separator, ("ab", "c") and ("a", "bc") would hash identically, letting a caller
        // replay one request's key against a materially different request.
        Assert.NotEqual(
            CanonicalRequestHash.Compute("op", "ab", "c"),
            CanonicalRequestHash.Compute("op", "a", "bc"));
    }
}
