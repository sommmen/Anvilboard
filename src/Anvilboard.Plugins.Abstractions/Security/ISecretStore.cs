namespace Anvilboard.Plugins.Abstractions.Security;

/// <summary>Protects integration credential payloads for persistence.</summary>
public interface ISecretStore
{
    string Protect(IReadOnlyDictionary<string, string> secrets);
    IReadOnlyDictionary<string, string> Unprotect(string protectedPayload);
}
