namespace AgentBridge.Core.Tests;

internal sealed class CapturingUiThreadMarshaller : IUiThreadMarshaller
{
    public int DispatchCount { get; private set; }

    public bool IsOnUiThread { get; set; }

    public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();

        if (this.IsOnUiThread)
        {
            return Task.FromResult(action());
        }

        this.DispatchCount++;
        return Task.FromResult(action());
    }

    public async Task<T> InvokeAsync<T>(Func<Task<T>> asyncAction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asyncAction);
        cancellationToken.ThrowIfCancellationRequested();

        if (!this.IsOnUiThread)
        {
            this.DispatchCount++;
        }

        return await asyncAction().ConfigureAwait(false);
    }

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return this.InvokeAsync(
            () =>
            {
                action();
                return true;
            },
            cancellationToken);
    }
}
