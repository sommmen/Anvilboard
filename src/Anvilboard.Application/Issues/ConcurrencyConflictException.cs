namespace Anvilboard.Application.Issues;

/// <summary>
/// Signals that a conditional mutation was rejected because the caller's <c>expectedVersion</c>
/// did not match the persisted <see cref="Anvilboard.Domain.Issue.Version"/> — the caller acted on
/// a stale read. Carries <see cref="CurrentVersion"/> so HTTP/CLI/MCP adapters can hand the caller
/// the value it needs to refetch and retry without a second round trip.
/// </summary>
public sealed class ConcurrencyConflictException(int expectedVersion, int currentVersion)
    : InvalidOperationException(
        $"Expected version {expectedVersion} but the issue is at version {currentVersion}. " +
        "Refetch the issue and re-apply the change.")
{
    public string ErrorCode { get; } = "CONCURRENCY_CONFLICT";

    public int ExpectedVersion { get; } = expectedVersion;

    public int CurrentVersion { get; } = currentVersion;
}
