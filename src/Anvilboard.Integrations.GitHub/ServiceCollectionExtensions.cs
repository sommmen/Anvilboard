using Anvilboard.Plugins.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Anvilboard.Integrations.GitHub;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the first-class GitHub integration (polling <see cref="IIngestionSource"/> +
    /// <see cref="IWebhookReceiver"/> for the <c>issues</c> event) bound from the
    /// <c>Plugins:github</c> configuration section. Both are registered as themselves so
    /// <c>Anvilboard.Infrastructure</c>'s <c>PluginRegistry</c> discovers them alongside any
    /// reflection-loaded third-party plugin, with no special-casing for "first-class" providers.
    /// </summary>
    public static IServiceCollection AddGitHubIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<GitHubOptions>(configuration.GetSection("Plugins:github"));
        services.AddHttpClient<GitHubIngestionSource>(client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
        });

        services.AddSingleton<GitHubWebhookReceiver>();

        // Registered under both their concrete/specific-interface type (so the host can resolve
        // them directly, e.g. for the HttpClient factory above) and IAnvilboardPlugin (so
        // PluginRegistry's IEnumerable<IAnvilboardPlugin> picks up every instance — see its OfType
        // filtering for how IIngestionSource/IWebhookReceiver are recovered from that flat list).
        services.AddSingleton<IIngestionSource>(sp => sp.GetRequiredService<GitHubIngestionSource>());
        services.AddSingleton<IWebhookReceiver>(sp => sp.GetRequiredService<GitHubWebhookReceiver>());

        // Resolve the *concrete* types here, not IIngestionSource/IWebhookReceiver: multiple
        // integrations register those same interfaces, and GetRequiredService<T>() only ever
        // returns the last-registered implementation, which would make every plugin collapse to
        // whichever integration was registered last.
        services.AddSingleton<IAnvilboardPlugin>(sp => sp.GetRequiredService<GitHubIngestionSource>());
        services.AddSingleton<IAnvilboardPlugin>(sp => sp.GetRequiredService<GitHubWebhookReceiver>());

        return services;
    }
}
