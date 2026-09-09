using Anvilboard.Domain;

namespace Anvilboard.Application.Authorization;

/// <summary>
/// The one-time result of minting a browser session credential (§ Included: "HTTP-only secure
/// session cookie for the SPA"). <see cref="RawToken"/> is the only place the raw session value
/// ever appears — only its hash is persisted (<c>ApiToken.TokenHash</c>) — so the caller must set
/// it as the session cookie immediately and never log or return it again (AC-108).
/// </summary>
public sealed record SessionIssuedResult(
    ApiTokenId ApiTokenId,
    string RawToken,
    DateTimeOffset ExpiresAt);
