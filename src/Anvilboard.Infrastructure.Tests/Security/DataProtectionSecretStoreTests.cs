using Anvilboard.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;

namespace Anvilboard.Infrastructure.Tests.Security;

public sealed class DataProtectionSecretStoreTests
{
    [Fact]
    public async Task StoreRetrieveAndRemove_RoundTripsProtectedCredentialValues()
    {
        var provider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        var store = new DataProtectionSecretStore(provider.CreateProtector("test"));

        var protectedPayload = store.Protect(new Dictionary<string, string> { ["token"] = "secret-value" });

        Assert.DoesNotContain("secret-value", protectedPayload);
        var stored = store.Unprotect(protectedPayload);
        Assert.Equal("secret-value", stored["token"]);
    }
}
