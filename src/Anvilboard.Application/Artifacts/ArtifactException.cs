namespace Anvilboard.Application.Artifacts;

/// <summary>Stable application error returned when an artifact request cannot be completed.</summary>
public sealed class ArtifactException(string errorCode, string message) : InvalidOperationException(message)
{
    public string ErrorCode { get; } = errorCode;
}
