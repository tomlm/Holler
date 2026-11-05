using Holler;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Holler.Server;

/// <summary>Keeps a Blazor Server circuit's Holler session in step with the browser connection.</summary>
internal sealed class HollerCircuitHandler(ISignalingChannel channel) : CircuitHandler
{
    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken) =>
        channel is InProcessSignalingChannel inProcess ? inProcess.SuspendAsync() : Task.CompletedTask;

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken) =>
        channel is InProcessSignalingChannel inProcess ? inProcess.ResumeAsync() : Task.CompletedTask;
}
