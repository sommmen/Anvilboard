namespace Anvilboard.Application.Automation;

/// <summary>Signals that a committed <c>Idempotency-Key</c> was reused with a different canonical
/// request hash (a different payload or actor); see AC-008. The automation surface should catch
/// this and translate it to the stable <c>IDEMPOTENCY_KEY_REUSED</c> (409) catalog code without
/// performing any mutation.</summary>
public sealed class IdempotencyKeyReusedException(string message) : InvalidOperationException(message)
{
    public const string ErrorCode = "IDEMPOTENCY_KEY_REUSED";
}
