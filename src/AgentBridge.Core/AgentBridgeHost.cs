using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentBridge.Core;

/// <summary>
/// <see cref="IChatClient"/> から直列ツール実行の <see cref="ChatClientAgent"/> を組み立てる
/// </summary>
public static class AgentBridgeHost
{
    /// <summary>
    /// function invocation を有効にした <see cref="ChatClientAgent"/> を返す
    /// </summary>
    /// <param name="chatClient">アプリが用意したチャット クライアント</param>
    /// <param name="tools">エージェントへ渡すツール</param>
    /// <param name="marshaller">指定時は <see cref="AIFunction"/> を UI スレッドへ載せる</param>
    /// <param name="options">反復上限などのホストオプション</param>
    /// <returns>ツールループを Agent Framework に任せたエージェント</returns>
    /// <exception cref="ArgumentNullException"><paramref name="chatClient"/> が <see langword="null"/></exception>
    /// <exception cref="ArgumentOutOfRangeException">反復上限が 0 以下</exception>
    /// <exception cref="InvalidOperationException">function invocation を有効にできなかった</exception>
    public static ChatClientAgent Create(
        IChatClient chatClient,
        IEnumerable<AITool>? tools = null,
        IUiThreadMarshaller? marshaller = null,
        AgentBridgeHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        options ??= new AgentBridgeHostOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumIterationsPerRequest);

        IList<AITool>? toolList = BindTools(tools, marshaller);

        ChatClientAgent agent = new(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = options.Name,
                Description = options.Description,
                AllowConcurrentInvocation = false,
                ChatOptions = new ChatOptions
                {
                    Instructions = options.Instructions,
                    Tools = toolList,
                },
            });

        FunctionInvokingChatClient functionClient = agent.GetService<FunctionInvokingChatClient>()
            ?? agent.ChatClient.GetService<FunctionInvokingChatClient>()
            ?? throw new InvalidOperationException("FunctionInvokingChatClient を有効にできませんでした。");

        functionClient.AllowConcurrentInvocation = false;
        functionClient.MaximumIterationsPerRequest = options.MaximumIterationsPerRequest;
        return agent;
    }

    private static IList<AITool>? BindTools(IEnumerable<AITool>? tools, IUiThreadMarshaller? marshaller)
    {
        if (tools is null)
        {
            return null;
        }

        List<AITool> list = marshaller is null
            ? [.. tools]
            : [.. UiThreadFunctions.Bind(tools, marshaller)];

        foreach (AITool tool in list)
        {
            ArgumentNullException.ThrowIfNull(tool);
        }

        return list.Count == 0 ? null : list;
    }
}
