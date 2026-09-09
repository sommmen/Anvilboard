using Anvilboard.Infrastructure.Persistence;
using Anvilboard.Infrastructure.Plugins;
using Anvilboard.Infrastructure.Security;
using Anvilboard.Plugins.Abstractions;
using Anvilboard.Plugins.Abstractions.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Anvilboard.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SQLite <see cref="AnvilboardDbContext"/> and the plugin registry. Called once
    /// from every host (the ASP.NET Core API and the CLI/MCP agent) so both surfaces share
    /// identical persistence and plugin-discovery behavior.
    /// </summary>
    public static IServiceCollection AddAnvilboardInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AnvilboardDbOptions>(configuration.GetSection("Database"));
        services.Configure<PluginHostOptions>(configuration.GetSection("Plugins"));

        services.AddDbContext<AnvilboardDbContext>((sp, options) =>
        {
            var dbOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AnvilboardDbOptions>>().Value;
            options.UseSqlite($"Data Source={dbOptions.DatabasePath}");
        });

        services.AddDataProtection().SetApplicationName("Anvilboard");
        services.AddSingleton<ISecretStore>(sp => new DataProtectionSecretStore(
            sp.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>()
                .CreateProtector("Anvilboard.Integrations.Secrets.v1")));
        services.AddSingleton<IPluginRegistry, PluginRegistry>();

        return services;
    }
}
