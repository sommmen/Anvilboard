using Anvilboard.Infrastructure.Persistence;
using Anvilboard.Infrastructure.Persistence.Backup;
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

        // Backup/restore (`docs/plans/backup-and-restore.md` §8.4) needs a fresh, independently
        // creatable AnvilboardDbContext post-swap, since the scoped instance above cannot be
        // reused after the live database file has been replaced out from under it.
        // AddDbContextFactory re-registers AnvilboardDbContext itself with the identical
        // configuration, so the existing scoped AddDbContext usage above keeps working unchanged.
        // The factory is registered Scoped rather than with its default Singleton lifetime because
        // AddDbContext above already registers DbContextOptions<AnvilboardDbContext> as Scoped; a
        // singleton factory consuming those scoped options fails container validation under
        // ValidateScopes/ValidateOnBuild. Scoped is also the correct lifetime here: every consumer
        // resolves the factory from a freshly created scope (see BackupService's post-swap
        // migration and audit paths), never from the root provider.
        services.AddDbContextFactory<AnvilboardDbContext>((sp, options) =>
        {
            var dbOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AnvilboardDbOptions>>().Value;
            options.UseSqlite($"Data Source={dbOptions.DatabasePath}");
        }, lifetime: ServiceLifetime.Scoped);

        services.AddScoped<IBackupArchiveStore, FileSystemBackupArchiveStore>();
        services.AddScoped<ISnapshotArchiver, SqliteBackupArchiver>();

        services.AddDataProtection();
        services.AddScoped<ISecretStore>(serviceProvider =>
            new DataProtectionSecretStore(
                serviceProvider
                    .GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("Anvilboard.Integrations.Secrets")));
        services.AddScoped<PluginConfigStateStore>();
        services.AddScoped<IPluginConfigStore>(serviceProvider => serviceProvider.GetRequiredService<PluginConfigStateStore>());
        services.AddScoped<IPluginStateStore>(serviceProvider => serviceProvider.GetRequiredService<PluginConfigStateStore>());
        services.AddSingleton<IPluginRegistry, PluginRegistry>();

        return services;
    }
}
