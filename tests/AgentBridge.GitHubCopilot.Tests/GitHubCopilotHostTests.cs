using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Agents.AI.GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace AgentBridge.GitHubCopilot.Tests;

public sealed class GitHubCopilotHostTests
{
    [Fact]
    public void CreateClientOptions_Emptyモードで保存先が設定されること()
    {
        CopilotClientOptions options = GitHubCopilotHost.CreateClientOptions("/tmp/copilot");

        Assert.Equal(CopilotClientMode.Empty, options.Mode);
        Assert.Equal("/tmp/copilot", options.BaseDirectory);
    }

    [Fact]
    public void CreateClientOptions_保存先が空なら拒否すること()
    {
        Assert.Throws<ArgumentNullException>(() => GitHubCopilotHost.CreateClientOptions(null!));
        Assert.Throws<ArgumentException>(() => GitHubCopilotHost.CreateClientOptions(" "));
    }

    [Fact]
    public void CreateSessionConfig_ツールなしでは何も公開しないこと()
    {
        SessionConfig config = GitHubCopilotHost.CreateSessionConfig();

        Assert.NotNull(config.AvailableTools);
        Assert.Empty(config.AvailableTools);
        Assert.NotNull(config.Tools);
        Assert.Empty(config.Tools);
        Assert.True(config.Streaming);
        Assert.Null(config.SystemMessage);
    }

    [Fact]
    public void CreateSessionConfig_アプリのツールだけを許可リストに載せること()
    {
        SessionConfig config = GitHubCopilotHost.CreateSessionConfig([Tool("set_bc"), Tool("submit_job")]);

        Assert.Equal(new ToolSet().AddCustom("set_bc").AddCustom("submit_job"), config.AvailableTools);
        Assert.Equal(["set_bc", "submit_job"], config.Tools!.Select(t => t.Name));
    }

    [Fact]
    public async Task CreateSessionConfig_マーシャラを渡すとツールInvokeがUIスレッド経由になること()
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

        SessionConfig config = GitHubCopilotHost.CreateSessionConfig([tool], marshaller);
        AIFunction bound = Assert.IsAssignableFrom<AIFunction>(Assert.Single(config.Tools!));
        object? result = await bound.InvokeAsync(new AIFunctionArguments());

        Assert.Equal("touch_doc", bound.Name);
        Assert.Equal("done", Assert.IsType<System.Text.Json.JsonElement>(result).GetString());
        Assert.Equal(1, toolCalls);
        Assert.Equal(1, marshaller.DispatchCount);
    }

    [Fact]
    public async Task CreateSessionConfig_登録したツールの実行だけを許可すること()
    {
        SessionConfig config = GitHubCopilotHost.CreateSessionConfig([Tool("set_bc")]);
        PermissionInvocation invocation = new();

        PermissionDecision allowed = await config.OnPermissionRequest!(
            new PermissionRequestCustomTool { ToolName = "set_bc", ToolDescription = string.Empty },
            invocation);
        PermissionDecision unknown = await config.OnPermissionRequest!(
            new PermissionRequestCustomTool { ToolName = "other", ToolDescription = string.Empty },
            invocation);
        PermissionDecision url = await config.OnPermissionRequest!(
            new PermissionRequestUrl { Intention = "fetch", Url = "https://example.com" },
            invocation);

        string approveKind = PermissionDecision.ApproveOnce().Kind;
        string rejectKind = PermissionDecision.Reject("x").Kind;
        Assert.Equal(approveKind, allowed.Kind);
        Assert.Equal(rejectKind, unknown.Kind);
        Assert.Equal(rejectKind, url.Kind);
    }

    [Fact]
    public void CreateSessionConfig_指示とモデルを設定に反映すること()
    {
        SessionConfig config = GitHubCopilotHost.CreateSessionConfig(
            options: new GitHubCopilotHostOptions { Instructions = "CAE の操作を手伝う", Model = "gpt-5" });

        Assert.Equal("gpt-5", config.Model);
        Assert.NotNull(config.SystemMessage);
        Assert.Equal(SystemMessageMode.Append, config.SystemMessage.Mode);
        Assert.Equal("CAE の操作を手伝う", config.SystemMessage.Content);
    }

    [Fact]
    public void CreateSessionConfig_AIFunctionでないツールを拒否すること()
    {
        Assert.Throws<ArgumentException>(() => GitHubCopilotHost.CreateSessionConfig([new HostedWebSearchTool()]));
    }

    [Fact]
    public void CreateSessionConfig_承認付きツールを拒否すること()
    {
        Assert.Throws<ArgumentException>(
            () => GitHubCopilotHost.CreateSessionConfig([new ApprovalRequiredAIFunction(Tool("delete_mesh"))]));
    }

    [Fact]
    public void CreateSessionConfig_ツール名の重複を拒否すること()
    {
        Assert.Throws<ArgumentException>(() => GitHubCopilotHost.CreateSessionConfig([Tool("set_bc"), Tool("set_bc")]));
    }

    [Fact]
    public void CreateSessionConfig_nullのツールを拒否すること()
    {
        Assert.Throws<ArgumentNullException>(() => GitHubCopilotHost.CreateSessionConfig([null!]));
    }

    [Fact]
    public async Task Create_名前と説明を持つGitHubCopilotAgentを返すこと()
    {
        await using CopilotClient client = new(GitHubCopilotHost.CreateClientOptions(Path.GetTempPath()));

        await using GitHubCopilotAgent agent = GitHubCopilotHost.Create(
            client,
            [Tool("set_bc")],
            options: new GitHubCopilotHostOptions { Name = "cae", Description = "CAE 補助" });

        Assert.Equal("cae", agent.Name);
        Assert.Equal("CAE 補助", agent.Description);
    }

    [Fact]
    public void Create_クライアントがnullなら拒否すること()
    {
        Assert.Throws<ArgumentNullException>(() => GitHubCopilotHost.Create(null!));
    }

    private static AIFunction Tool(string name) => AIFunctionFactory.Create(() => "ok", name: name);
}
