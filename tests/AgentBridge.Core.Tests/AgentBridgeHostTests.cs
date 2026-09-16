using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentBridge.Core.Tests;

public sealed class AgentBridgeHostTests
{
    [Fact]
    public void Create_既定では直列実行かつ反復上限が10であること()
    {
        ChatClientAgent agent = AgentBridgeHost.Create(CreateIdleClient());
        FunctionInvokingChatClient ficc = RequireFunctionClient(agent);

        Assert.False(ficc.AllowConcurrentInvocation);
        Assert.Equal(AgentBridgeHostOptions.DefaultMaximumIterationsPerRequest, ficc.MaximumIterationsPerRequest);
        Assert.Equal(10, ficc.MaximumIterationsPerRequest);
    }

    [Fact]
    public void Create_反復上限が0以下なら拒否すること()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AgentBridgeHost.Create(
                CreateIdleClient(),
                options: new AgentBridgeHostOptions { MaximumIterationsPerRequest = 0 }));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => AgentBridgeHost.Create(
                CreateIdleClient(),
                options: new AgentBridgeHostOptions { MaximumIterationsPerRequest = -1 }));
    }

    [Fact]
    public async Task Create_マーシャラを渡すとツールInvokeがUIスレッド経由になること()
    {
        CapturingUiThreadMarshaller marshaller = new();
        int toolCalls = 0;
        AIFunction tool = AIFunctionFactory.Create(
            () =>
            {
                toolCalls++;
                return "done";
            },
            name: "touch_doc");

        ScriptedChatClient client = new(call => call == 1
            ? FunctionCalls(FunctionCall("c1", "touch_doc"))
            : Text("ok"));

        ChatClientAgent agent = AgentBridgeHost.Create(client, [tool], marshaller);
        AgentResponse response = await agent.RunAsync("run");

        Assert.Contains("ok", response.Text, StringComparison.Ordinal);
        Assert.Equal(1, toolCalls);
        Assert.Equal(1, marshaller.DispatchCount);
    }

    [Fact]
    public async Task Create_同一応答の複数ツールは直列に実行されること()
    {
        int running = 0;
        int maxConcurrent = 0;
        object gate = new();

        async Task<string> Work(string id)
        {
            lock (gate)
            {
                running++;
                if (running > maxConcurrent)
                {
                    maxConcurrent = running;
                }
            }

            await Task.Delay(80);
            lock (gate)
            {
                running--;
            }

            return id;
        }

        AIFunction alpha = AIFunctionFactory.Create(() => Work("alpha"), name: "alpha");
        AIFunction beta = AIFunctionFactory.Create(() => Work("beta"), name: "beta");

        ScriptedChatClient client = new(call => call == 1
            ? FunctionCalls(FunctionCall("c1", "alpha"), FunctionCall("c2", "beta"))
            : Text("done"));

        ChatClientAgent agent = AgentBridgeHost.Create(client, [alpha, beta]);
        AgentResponse response = await agent.RunAsync("run");

        Assert.Contains("done", response.Text, StringComparison.Ordinal);
        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task RunStreamingAsync_キャンセルすると中断すること()
    {
        ScriptedChatClient client = new(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return Text("never");
        });

        ChatClientAgent agent = AgentBridgeHost.Create(client);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (AgentResponseUpdate update in agent.RunStreamingAsync("run", cancellationToken: cts.Token))
            {
                _ = update;
            }
        });
    }

    private static ScriptedChatClient CreateIdleClient()
    {
        return new ScriptedChatClient(_ => Text("idle"));
    }

    private static FunctionInvokingChatClient RequireFunctionClient(ChatClientAgent agent)
    {
        return agent.GetService<FunctionInvokingChatClient>()
            ?? agent.ChatClient.GetService<FunctionInvokingChatClient>()
            ?? throw new InvalidOperationException("FunctionInvokingChatClient が見つかりません。");
    }

    private static FunctionCallContent FunctionCall(string callId, string name)
    {
        return new FunctionCallContent(callId, name, new Dictionary<string, object?>());
    }

    private static ChatResponse FunctionCalls(params FunctionCallContent[] calls)
    {
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, [.. calls]))
        {
            FinishReason = ChatFinishReason.ToolCalls,
        };
    }

    private static ChatResponse Text(string text)
    {
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            FinishReason = ChatFinishReason.Stop,
        };
    }
}
