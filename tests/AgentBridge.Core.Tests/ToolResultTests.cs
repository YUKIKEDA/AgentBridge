using AgentBridge.Core;

namespace AgentBridge.Core.Tests;

public sealed class ToolResultTests
{
    [Fact]
    public void Success_LlmContentを指定すると成功状態のToolResultが生成されること()
    {
        ToolResult result = ToolResult.Success("call_1", "done");

        Assert.Equal("call_1", result.ToolUseId);
        Assert.Equal(ToolExecutionStatus.Success, result.Status);
        Assert.Equal("done", result.LlmContent);
        Assert.Null(result.ErrorCode);
        Assert.Null(result.DiagnosticDetails);
    }

    [Fact]
    public void Failed_ErrorCodeとLlmContentを指定すると失敗状態のToolResultが生成されること()
    {
        ToolResult result = ToolResult.Failed("call_1", "NOT_FOUND", "file not found", "stack trace...");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal("NOT_FOUND", result.ErrorCode);
        Assert.Equal("file not found", result.LlmContent);
        Assert.Equal("stack trace...", result.DiagnosticDetails);
    }

    [Fact]
    public void Cancelled_引数を省略すると既定のErrorCodeとLlmContentが設定されること()
    {
        ToolResult result = ToolResult.Cancelled("call_1");

        Assert.Equal(ToolExecutionStatus.Cancelled, result.Status);
        Assert.Equal("CANCELLED", result.ErrorCode);
        Assert.Equal("cancelled", result.LlmContent);
    }

    [Fact]
    public void TimedOut_引数を省略すると既定のErrorCodeとLlmContentが設定されること()
    {
        ToolResult result = ToolResult.TimedOut("call_1");

        Assert.Equal(ToolExecutionStatus.TimedOut, result.Status);
        Assert.Equal("TIMED_OUT", result.ErrorCode);
        Assert.Equal("timed out", result.LlmContent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Failed_ErrorCodeが未指定だと例外になること(string? errorCode)
    {
        Assert.ThrowsAny<ArgumentException>(() => ToolResult.Failed("call_1", errorCode!, "reason"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Failed_LlmContentが未指定だと例外になること(string? llmContent)
    {
        Assert.ThrowsAny<ArgumentException>(() => ToolResult.Failed("call_1", "SOME_ERROR", llmContent!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Success_ToolUseIdが未指定だと例外になること(string? toolUseId)
    {
        Assert.ThrowsAny<ArgumentException>(() => ToolResult.Success(toolUseId!, "done"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Success_LlmContentが未指定だと例外になること(string? llmContent)
    {
        Assert.ThrowsAny<ArgumentException>(() => ToolResult.Success("call_1", llmContent!));
    }

    [Fact]
    public void コンストラクタ_SuccessにErrorCodeを指定すると例外になること()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new ToolResult("call_1", ToolExecutionStatus.Success, "done", ErrorCode: "X"));
    }

    [Theory]
    [InlineData(ToolExecutionStatus.Failed)]
    [InlineData(ToolExecutionStatus.Cancelled)]
    [InlineData(ToolExecutionStatus.TimedOut)]
    public void コンストラクタ_失敗系ステータスでErrorCodeが未指定だと例外になること(ToolExecutionStatus status)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ToolResult("call_1", status, "reason"));
    }
}
