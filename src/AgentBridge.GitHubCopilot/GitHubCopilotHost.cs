using AgentBridge.Core;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Agents.AI.GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace AgentBridge.GitHubCopilot;

/// <summary>
/// GitHub Copilot SDK から、アプリのツールだけを使う <see cref="GitHubCopilotAgent"/> を組み立てる
/// </summary>
public static class GitHubCopilotHost
{
    /// <summary>
    /// CLI 組み込みの機能を既定で出さない <see cref="CopilotClientOptions"/> を返す
    /// </summary>
    /// <param name="baseDirectory">セッション状態や設定を置くディレクトリ</param>
    /// <returns><see cref="CopilotClientMode.Empty"/> の <see cref="CopilotClientOptions"/></returns>
    /// <remarks>
    /// 認証（<c>GitHubToken</c> など）は返り値へアプリが設定する
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="baseDirectory"/> が空または空白</exception>
    /// <exception cref="ArgumentNullException"><paramref name="baseDirectory"/> が <see langword="null"/></exception>
    public static CopilotClientOptions CreateClientOptions(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        return new CopilotClientOptions
        {
            Mode = CopilotClientMode.Empty,
            BaseDirectory = baseDirectory,
        };
    }

    /// <summary>
    /// アプリのツールだけを公開し、それ以外のツール実行を拒否する <see cref="GitHubCopilotAgent"/> を返す
    /// </summary>
    /// <param name="client">アプリが用意した Copilot クライアント</param>
    /// <param name="tools">エージェントへ渡すツール。すべて <see cref="AIFunction"/> であること</param>
    /// <param name="marshaller">指定時は <see cref="AIFunction"/> を UI スレッドへ載せる</param>
    /// <param name="options">名前や指示などのホストオプション</param>
    /// <returns>ツールループを Copilot CLI に任せたエージェント</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> または列内の要素が <see langword="null"/></exception>
    /// <exception cref="ArgumentException">ツールが <see cref="AIFunction"/> でない、承認付き、または名前が重複している</exception>
    public static GitHubCopilotAgent Create(
        CopilotClient client,
        IEnumerable<AITool>? tools = null,
        IUiThreadMarshaller? marshaller = null,
        GitHubCopilotHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        options ??= new GitHubCopilotHostOptions();

        return new GitHubCopilotAgent(
            client,
            CreateSessionConfig(tools, marshaller, options),
            ownsClient: options.OwnsClient,
            name: options.Name,
            description: options.Description);
    }

    /// <summary>
    /// <see cref="Create"/> が使うセッション設定を返す
    /// </summary>
    /// <param name="tools">エージェントへ渡すツール。すべて <see cref="AIFunction"/> であること</param>
    /// <param name="marshaller">指定時は <see cref="AIFunction"/> を UI スレッドへ載せる</param>
    /// <param name="options">指示やモデルなどのホストオプション</param>
    /// <returns>アプリのツールだけを許可するセッション設定</returns>
    /// <exception cref="ArgumentNullException">列内の要素が <see langword="null"/></exception>
    /// <exception cref="ArgumentException">ツールが <see cref="AIFunction"/> でない、承認付き、または名前が重複している</exception>
    public static SessionConfig CreateSessionConfig(
        IEnumerable<AITool>? tools = null,
        IUiThreadMarshaller? marshaller = null,
        GitHubCopilotHostOptions? options = null)
    {
        options ??= new GitHubCopilotHostOptions();

        List<AIFunction> functions = ValidateTools(tools);
        HashSet<string> allowedNames = new(functions.Select(f => f.Name), StringComparer.Ordinal);

        ToolSet availableTools = [];
        List<AIFunctionDeclaration> declarations = [];
        foreach (AIFunction function in functions)
        {
            availableTools.AddCustom(function.Name);
            declarations.Add(marshaller is null ? function : UiThreadFunctions.Bind(function, marshaller));
        }

        return new SessionConfig
        {
            Model = options.Model,
            Streaming = true,
            Tools = declarations,
            AvailableTools = availableTools,
            OnPermissionRequest = (request, _) => Task.FromResult(Decide(request, allowedNames)),
            SystemMessage = options.Instructions is null
                ? null
                : new SystemMessageConfig
                {
                    Mode = SystemMessageMode.Append,
                    Content = options.Instructions,
                },
        };
    }

    // OnPermissionRequest の戻り値型が評価段階の API のため、ここだけ GHCP001 を抑止する
#pragma warning disable GHCP001
    private static PermissionDecision Decide(PermissionRequest request, HashSet<string> allowedNames)
    {
        return request is PermissionRequestCustomTool customTool && allowedNames.Contains(customTool.ToolName)
            ? PermissionDecision.ApproveOnce()
            : PermissionDecision.Reject("このアプリでは許可されていないツールです。");
    }
#pragma warning restore GHCP001

    private static List<AIFunction> ValidateTools(IEnumerable<AITool>? tools)
    {
        List<AIFunction> functions = [];
        if (tools is null)
        {
            return functions;
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (AITool tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);

            if (tool is not AIFunction function)
            {
                throw new ArgumentException($"ツール '{tool.Name}' は AIFunction ではありません。", nameof(tools));
            }

            // 承認 UI が無いため、許可の判定で素通しにならないよう受け付けない
            if (function.GetService<ApprovalRequiredAIFunction>() is not null)
            {
                throw new ArgumentException($"ツール '{function.Name}' は承認付きです。承認付きツールは未対応です。", nameof(tools));
            }

            if (!names.Add(function.Name))
            {
                throw new ArgumentException($"ツール名 '{function.Name}' が重複しています。", nameof(tools));
            }

            functions.Add(function);
        }

        return functions;
    }
}
