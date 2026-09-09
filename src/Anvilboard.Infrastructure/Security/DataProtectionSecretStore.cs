using System.Text.Json;
using Anvilboard.Plugins.Abstractions.Security;
using Microsoft.AspNetCore.DataProtection;

namespace Anvilboard.Infrastructure.Security;

/// <summary>Protects credential payloads before they are persisted with their integration.</summary>
public sealed class DataProtectionSecretStore(IDataProtector protector) : ISecretStore
{
    public string Protect(IReadOnlyDictionary<string, string> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        return protector.Protect(JsonSerializer.Serialize(secrets));
    }

    public IReadOnlyDictionary<string, string> Unprotect(string protectedPayload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedPayload);
        var payload = protector.Unprotect(protectedPayload);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(payload)
            ?? throw new InvalidOperationException("The protected secret payload was empty.");
    }
}
