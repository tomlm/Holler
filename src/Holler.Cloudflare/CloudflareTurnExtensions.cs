using Holler.Cloudflare;
using Holler.Server;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class CloudflareTurnExtensions
{
    /// <summary>
    /// Uses Cloudflare Realtime TURN for ICE servers. Settings bind from the <c>Holler:CloudflareTurn</c>
    /// configuration section (<c>KeyId</c>, <c>ApiToken</c>, <c>CredentialLifetime</c>); <paramref name="configure"/>
    /// runs after binding. Call alongside <c>AddHoller()</c>, in either order.
    /// </summary>
    public static IServiceCollection AddHollerCloudflareTurn(this IServiceCollection services, Action<CloudflareTurnOptions>? configure = null)
    {
        var options = services.AddOptions<CloudflareTurnOptions>().BindConfiguration("Holler:CloudflareTurn");
        if (configure is not null)
            options.Configure(configure);
        services.AddHttpClient(CloudflareTurnProvider.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
        services.TryAddSingleton(TimeProvider.System);
        // Replace whatever AddHoller registered (or will register: it only adds a provider if none exists).
        services.Replace(ServiceDescriptor.Singleton<IIceServerProvider, CloudflareTurnProvider>());
        return services;
    }
}
