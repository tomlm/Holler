using Microsoft.AspNetCore.Components;

namespace Holler;

/// <summary>
/// Base for Holler components: starts the <see cref="HollerClient"/> after the first interactive render
/// (never during prerender) and re-renders whenever its state changes.
/// </summary>
public abstract class HollerComponentBase : ComponentBase, IAsyncDisposable
{
    [Inject] protected HollerClient Holler { get; set; } = default!;

    /// <summary>The last error from starting the client or a user action (e.g. not signed in, hub unreachable).</summary>
    protected string? Error { get; private set; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
            return;
        Holler.Changed += OnHollerChanged;
        try
        {
            await Holler.StartAsync();
            await OnHollerStartedAsync();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        StateHasChanged();
    }

    /// <summary>Called once the client is connected.</summary>
    protected virtual Task OnHollerStartedAsync() => Task.CompletedTask;

    /// <summary>Called (on the renderer) whenever client state changes, before re-rendering.</summary>
    protected virtual void OnHollerStateChanged()
    {
    }

    private void OnHollerChanged() => _ = InvokeAsync(() =>
    {
        OnHollerStateChanged();
        StateHasChanged();
    });

    protected async Task Run(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    public virtual ValueTask DisposeAsync()
    {
        Holler.Changed -= OnHollerChanged;
        return ValueTask.CompletedTask;
    }
}
