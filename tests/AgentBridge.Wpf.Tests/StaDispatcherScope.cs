using System.Windows.Threading;

namespace AgentBridge.Wpf.Tests;

internal sealed class StaDispatcherScope : IDisposable
{
    private readonly Thread thread;

    private StaDispatcherScope(Dispatcher dispatcher, Thread thread)
    {
        this.Dispatcher = dispatcher;
        this.thread = thread;
    }

    public Dispatcher Dispatcher { get; }

    public static StaDispatcherScope Start()
    {
        TaskCompletionSource<Dispatcher> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                ready.SetResult(dispatcher);
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                ready.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "AgentBridge.Wpf.Tests.STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return new StaDispatcherScope(ready.Task.GetAwaiter().GetResult(), thread);
    }

    public void Dispose()
    {
        if (!this.Dispatcher.HasShutdownStarted)
        {
            this.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        }

        if (!this.thread.Join(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("STA Dispatcher スレッドが終了しませんでした。");
        }
    }
}
