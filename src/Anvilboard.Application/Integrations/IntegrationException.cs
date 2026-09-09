namespace Anvilboard.Application.Integrations;

public sealed class IntegrationException(string errorCode, string message) : InvalidOperationException(message)
{
    public string ErrorCode { get; } = errorCode;
}
