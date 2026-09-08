namespace Anvilboard.Application.Issues;

/// <summary>Stable application error returned when an issue-link request cannot be completed.</summary>
public sealed class IssueLinkException(string errorCode, string message) : InvalidOperationException(message)
{
    public string ErrorCode { get; } = errorCode;
}
