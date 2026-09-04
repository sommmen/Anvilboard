using Anvilboard.Plugins.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Anvilboard.Integrations.Linear;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the first-class remote-issue-tracker integration (polling
    /// <see cref="IIngestionSource"/> + <see cref="IWebhookReceiver"/>) bound from the
    /// <c>Plugins:linear</c> configuration section.
    /// </summary>
    public static IServiceCollection AddLinearIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LinearOptions>(configuration.GetSection("Plugins:linear"));
        services.AddHttpClient<LinearIngestionSource>(client =>
        {
            client.BaseAddress = new Uri("https://api.linear.app/");
        });

        services.AddSingleton<LinearWebhookReceiver>();

        services.AddSingleton<IIngestionSource>(sp => sp.GetRequiredService<LinearIngestionSource>());
        services.AddSingleton<IWebhookReceiver>(sp => sp.GetRequiredService<LinearWebhookReceiver>());

        // Resolve the *concrete* types here, not IIngestionSource/IWebhookReceiver: multiple
        // integrations register those same interfaces, and GetRequiredService<T>() only ever
        // returns the last-registered implementation, which would make every plugin collapse to
        // whichever integration was registered last.
        services.AddSingleton<IAnvilboardPlugin>(sp => sp.GetRequiredService<LinearIngestionSource>());
        services.AddSingleton<IAnvilboardPlugin>(sp => sp.GetRequiredService<LinearWebhookReceiver>());

        return services;
    }
}
