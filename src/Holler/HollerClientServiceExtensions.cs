using Holler;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class HollerClientServiceExtensions
{
    /// <summary>
    /// Registers Holler for a WebAssembly client: <see cref="HollerClient"/> plus hub-based signaling.
    /// </summary>
    public static IServiceCollection AddHollerClient(this IServiceCollection services, Action<HollerClientOptions>? configure = null)
    {
        var options = new HollerClientOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddScoped<ISignalingChannel, HubSignalingChannel>();
        services.TryAddScoped<HollerClient>();
        return services;
    }
}
