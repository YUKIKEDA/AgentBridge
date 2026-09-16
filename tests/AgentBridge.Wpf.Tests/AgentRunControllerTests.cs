using AgentBridge.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentBridge.Wpf.Tests;

public sealed class AgentRunControllerTests
{
    [Fact]
    public async Task RunStreamingAsync_実行中はIsBusyになり完了後に下がること()
    {
        ChatClientAgent agent = AgentBridgeHost.Create(new ScriptedChatClient(_ => Text("ok")));
        AgentSession session = await agent.CreateSessionAsync();
        using AgentRunController controller = new(agent, session);

        Assert.False(controller.IsBusy);
        List<string> chunks = [];
        await foreach (AgentResponseUpdate update in controller.RunStreamingAsync("hello"))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                chunks.Add(update.Text);
            }
        }

        Assert.Contains("ok", string.Concat(chunks), StringComparison.Ordinal);
        Assert.False(controller.IsBusy);
    }

    [Fact]
    public async Task RunStreamingAsync_実行中の再開始は拒否すること()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedChatClient client = new(async (_, _, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return Text("never");
        });
        ChatClientAgent agent = AgentBridgeHost.Create(client);
        AgentSession session = await agent.CreateSessionAsync();
        using AgentRunController controller = new(agent, session);

        IAsyncEnumerable<AgentResponseUpdate> first = controller.RunStreamingAsync("first");
        IAsyncEnumerator<AgentResponseUpdate> enumerator = first.GetAsyncEnumerator();
        Task<bool> move = enumerator.MoveNextAsync().AsTask();
        await started.Task;
        Assert.True(controller.IsBusy);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () =>
            {
                await foreach (AgentResponseUpdate update in controller.RunStreamingAsync("second"))
                {
                    _ = update;
                }
            });
        Assert.Contains("同時", ex.Message, StringComparison.Ordinal);

        controller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        await enumerator.DisposeAsync();
        Assert.False(controller.IsBusy);
    }

    [Fact]
    public async Task Cancel_実行中のストリームを中断すること()
    {
        ScriptedChatClient client = new(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return Text("never");
        });
        ChatClientAgent agent = AgentBridgeHost.Create(client);
        AgentSession session = await agent.CreateSessionAsync();
        using AgentRunController controller = new(agent, session);

        IAsyncEnumerable<AgentResponseUpdate> stream = controller.RunStreamingAsync("run");
        IAsyncEnumerator<AgentResponseUpdate> enumerator = stream.GetAsyncEnumerator();
        Task<bool> move = enumerator.MoveNextAsync().AsTask();
        await WaitUntilBusyAsync(controller);
        controller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        await enumerator.DisposeAsync();
        Assert.False(controller.IsBusy);
    }

    [Fact]
    public async Task RunStreamingAsync_完了後なら再実行できること()
    {
        int calls = 0;
        ScriptedChatClient client = new(_ =>
        {
            calls++;
            return Text("ok");
        });
        ChatClientAgent agent = AgentBridgeHost.Create(client);
        AgentSession session = await agent.CreateSessionAsync();
        using AgentRunController controller = new(agent, session);

        await DrainAsync(controller.RunStreamingAsync("one"));
        await DrainAsync(controller.RunStreamingAsync("two"));

        Assert.Equal(2, calls);
        Assert.False(controller.IsBusy);
    }

    private static async Task DrainAsync(IAsyncEnumerable<AgentResponseUpdate> stream)
    {
        await foreach (AgentResponseUpdate update in stream)
        {
            _ = update;
        }
    }

    private static async Task WaitUntilBusyAsync(AgentRunController controller)
    {
        for (int i = 0; i < 50; i++)
        {
            if (controller.IsBusy)
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("IsBusy になりませんでした。");
    }

    private static ChatResponse Text(string text)
    {
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            FinishReason = ChatFinishReason.Stop,
        };
    }
}
