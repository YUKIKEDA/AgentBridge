using System.Windows.Threading;
using AgentBridge.Core;

namespace AgentBridge.Wpf;

/// <summary>
/// WPF の <see cref="Dispatcher"/> へ処理を載せる <see cref="IUiThreadMarshaller"/>
/// </summary>
public sealed class DispatcherMarshaller : IUiThreadMarshaller
{
    private readonly Dispatcher dispatcher;

    /// <summary>
    /// Initializes a new instance of the <see cref="DispatcherMarshaller"/> class
    /// </summary>
    /// <param name="dispatcher">投入先の Dispatcher</param>
    /// <exception cref="ArgumentNullException"><paramref name="dispatcher"/> が <see langword="null"/></exception>
    public DispatcherMarshaller(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        this.dispatcher = dispatcher;
    }

    /// <inheritdoc />
    public bool IsOnUiThread => this.dispatcher.CheckAccess();

    /// <inheritdoc />
    public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();

        if (this.dispatcher.CheckAccess())
        {
            try
            {
                return Task.FromResult(action());
            }
            catch (Exception exception)
            {
                return Task.FromException<T>(exception);
            }
        }

        return this.dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task;
    }

    /// <inheritdoc />
    public async Task<T> InvokeAsync<T>(Func<Task<T>> asyncAction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asyncAction);
        cancellationToken.ThrowIfCancellationRequested();

        if (this.dispatcher.CheckAccess())
        {
            return await asyncAction().ConfigureAwait(false);
        }

        Task<T> inner = await this.dispatcher
            .InvokeAsync(asyncAction, DispatcherPriority.Normal, cancellationToken)
            .Task
            .ConfigureAwait(false);
        return await inner.ConfigureAwait(false);
    }

    /// <inheritdoc />
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
