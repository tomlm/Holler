using Holler.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

public static class HollerServerExtensions
{
    /// <summary>
    /// Adds the Holler signaling hub and router, plus <c>HollerClient</c> for components rendered with
    /// interactive Server render mode. WebAssembly clients also call <c>AddHollerClient()</c> in their own Program.cs.
    /// </summary>
    public static IServiceCollection AddHoller(this IServiceCollection services, Action<HollerOptions>? configure = null)
    {
        services.AddSignalR();
        var options = services.AddOptions<HollerOptions>();
        if (configure is not null)
            options.Configure(configure);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<HollerSignalRouter>();
        services.TryAddSingleton<IHollerAuthorizer, AllowAllHollerAuthorizer>();
        services.TryAddSingleton<IHollerUserIdProvider, ClaimsHollerUserIdProvider>();
        services.TryAddSingleton<IIceServerProvider, OptionsIceServerProvider>();
        services.TryAddScoped<Holler.ISignalingChannel, InProcessSignalingChannel>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<CircuitHandler, HollerCircuitHandler>());
        services.TryAddScoped<Holler.HollerClient>();
        return services;
    }

    /// <summary>Maps the Holler hub at <see cref="HollerOptions.HubPath"/>, requiring authorization unless disabled.</summary>
    public static HubEndpointConventionBuilder MapHollerHub(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<HollerOptions>>().Value;
        var builder = endpoints.MapHub<HollerHub>(options.HubPath);
        if (options.RequireAuthorization)
            builder.RequireAuthorization();
        return builder;
    }
}
