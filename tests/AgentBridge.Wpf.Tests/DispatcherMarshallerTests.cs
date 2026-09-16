using System.Windows.Threading;
using AgentBridge.Core;
using Microsoft.Extensions.AI;

namespace AgentBridge.Wpf.Tests;

public sealed class DispatcherMarshallerTests
{
    [Fact]
    public async Task InvokeAsync_バックグラウンドから呼ぶとDispatcherスレッドで実行すること()
    {
        using StaDispatcherScope scope = StaDispatcherScope.Start();
        DispatcherMarshaller marshaller = new(scope.Dispatcher);
        int uiThreadId = await scope.Dispatcher.InvokeAsync(() => Environment.CurrentManagedThreadId);

        int observed = await marshaller.InvokeAsync(() => Environment.CurrentManagedThreadId);

        Assert.Equal(uiThreadId, observed);
        Assert.False(marshaller.IsOnUiThread);
    }

    [Fact]
    public async Task InvokeAsync_既にUIスレッド上ならマーシャリングをバイパスすること()
    {
        using StaDispatcherScope scope = StaDispatcherScope.Start();
        DispatcherMarshaller marshaller = new(scope.Dispatcher);

        (bool isOnUi, int uiThreadId, int observed) = await scope.Dispatcher
            .InvokeAsync(async () =>
            {
                bool onUi = marshaller.IsOnUiThread;
                int ui = Environment.CurrentManagedThreadId;
                int seen = await marshaller.InvokeAsync(() => Environment.CurrentManagedThreadId);
                return (onUi, ui, seen);
            })
            .Task
            .Unwrap();

        Assert.True(isOnUi);
        Assert.Equal(uiThreadId, observed);
    }

    [Fact]
    public async Task InvokeAsync_FuncTaskをアンラップして戻り値を返すこと()
    {
        using StaDispatcherScope scope = StaDispatcherScope.Start();
        DispatcherMarshaller marshaller = new(scope.Dispatcher);

        int result = await marshaller.InvokeAsync(async () =>
        {
            await Task.Delay(10).ConfigureAwait(false);
            return 42;
        });

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task InvokeAsync_内側の例外を呼び出し元へ再スローすること()
    {
        using StaDispatcherScope scope = StaDispatcherScope.Start();
        DispatcherMarshaller marshaller = new(scope.Dispatcher);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => marshaller.InvokeAsync(static int () => throw new InvalidOperationException("boom")));
        Assert.Equal("boom", ex.Message);

        InvalidOperationException asyncEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => marshaller.InvokeAsync(async static () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("async-boom");
#pragma warning disable CS0162
                return 0;
#pragma warning restore CS0162
            }));
        Assert.Equal("async-boom", asyncEx.Message);
    }

    [Fact]
    public async Task InvokeAsync_投入前にキャンセルされていると投入しないこと()
    {
        using StaDispatcherScope scope = StaDispatcherScope.Start();
        DispatcherMarshaller marshaller = new(scope.Dispatcher);
        bool invoked = false;
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => marshaller.InvokeAsync(
                () =>
                {
                    invoked = true;
                    return 1;
                },
                cts.Token));
        Assert.False(invoked);
    }

    [Fact]
    public async Task InvokeAsync_実行開始後はキャンセルしても強制Abortしないこと()
    {
        using StaDispatcherScope scope = StaDispatcherScope.Start();
        DispatcherMarshaller marshaller = new(scope.Dispatcher);
        using CancellationTokenSource cts = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> invoke = marshaller.InvokeAsync(
            () =>
            {
                started.SetResult();
                Thread.Sleep(150);
                return 7;
            },
            cts.Token);

        await started.Task;
        await cts.CancelAsync();

        Assert.Equal(7, await invoke);
    }

    [Fact]
    public async Task Bind_WPFマーシャラ経由だとツール本体がUIスレッドで走ること()
    {
        using StaDispatcherScope scope = StaDispatcherScope.Start();
        DispatcherMarshaller marshaller = new(scope.Dispatcher);
        int uiThreadId = await scope.Dispatcher.InvokeAsync(() => Environment.CurrentManagedThreadId);
        int toolThreadId = 0;
        AIFunction bound = UiThreadFunctions.Bind(
            AIFunctionFactory.Create(
                () =>
                {
                    toolThreadId = Environment.CurrentManagedThreadId;
                    return "ok";
                },
                name: "touch_doc"),
            marshaller);

        object? result = await bound.InvokeAsync();

        Assert.Equal("ok", result?.ToString());
        Assert.Equal(uiThreadId, toolThreadId);
    }
}
