namespace AgentBridge.Core;

/// <summary>
/// ツール実行の成否・ToolUseId・LLM/診断メッセージを保持する結果
/// MVP では成功コンテンツを文字列（テキスト）とする。プロバイダ固有形式への変換は各アダプタの責務
/// </summary>
/// <param name="ToolUseId">元の tool_use ID（必須。プロバイダ変換に必要）</param>
/// <param name="Status">実行結果の状態</param>
/// <param name="LlmContent">LLM に送る短文（パス・スタック・秘密情報を含めない）</param>
/// <param name="DiagnosticDetails">UI / ログ向け詳細</param>
/// <param name="ErrorCode">失敗系の状態を識別するエラーコード</param>
public sealed record ToolResult(
    string ToolUseId,
    ToolExecutionStatus Status,
    string LlmContent,
    string? DiagnosticDetails = null,
    string? ErrorCode = null)
{
    // プライマリコンストラクタ経由でも Status 別契約を強制する
#pragma warning disable IDE0052
    private readonly bool validated = ValidateContract(ToolUseId, Status, LlmContent, ErrorCode);
#pragma warning restore IDE0052

    /// <summary>
    /// 成功結果を作成します。Status 契約上、ErrorCode は常に null になります
    /// </summary>
    /// <param name="toolUseId">元の tool_use ID</param>
    /// <param name="llmContent">LLM に送る結果テキスト</param>
    /// <param name="diagnosticDetails">UI / ログ向け詳細</param>
    /// <returns>成功状態の <see cref="ToolResult"/></returns>
    public static ToolResult Success(string toolUseId, string llmContent, string? diagnosticDetails = null)
    {
        return new ToolResult(toolUseId, ToolExecutionStatus.Success, llmContent, diagnosticDetails);
    }

    /// <summary>
    /// 失敗結果を作成します
    /// </summary>
    /// <param name="toolUseId">元の tool_use ID</param>
    /// <param name="errorCode">失敗を識別するエラーコード</param>
    /// <param name="llmContent">LLM に送る理由の要約テキスト</param>
    /// <param name="diagnosticDetails">UI / ログ向け詳細</param>
    /// <returns>失敗状態の <see cref="ToolResult"/></returns>
    public static ToolResult Failed(
        string toolUseId,
        string errorCode,
        string llmContent,
        string? diagnosticDetails = null)
    {
        return new ToolResult(toolUseId, ToolExecutionStatus.Failed, llmContent, diagnosticDetails, errorCode);
    }

    /// <summary>
    /// キャンセル結果を作成します
    /// </summary>
    /// <param name="toolUseId">元の tool_use ID</param>
    /// <param name="errorCode">エラーコード（既定 "CANCELLED"）</param>
    /// <param name="llmContent">LLM に送る理由の要約テキスト（既定 "cancelled"）</param>
    /// <returns>キャンセル状態の <see cref="ToolResult"/></returns>
    public static ToolResult Cancelled(
        string toolUseId,
        string errorCode = "CANCELLED",
        string llmContent = "cancelled")
    {
        return new ToolResult(toolUseId, ToolExecutionStatus.Cancelled, llmContent, ErrorCode: errorCode);
    }

    /// <summary>
    /// タイムアウト結果を作成します
    /// </summary>
    /// <param name="toolUseId">元の tool_use ID</param>
    /// <param name="errorCode">エラーコード（既定 "TIMED_OUT"）</param>
    /// <param name="llmContent">LLM に送る理由の要約テキスト（既定 "timed out"）</param>
    /// <returns>タイムアウト状態の <see cref="ToolResult"/></returns>
    public static ToolResult TimedOut(
        string toolUseId,
        string errorCode = "TIMED_OUT",
        string llmContent = "timed out")
    {
        return new ToolResult(toolUseId, ToolExecutionStatus.TimedOut, llmContent, ErrorCode: errorCode);
    }

    private static bool ValidateContract(
        string toolUseId,
        ToolExecutionStatus status,
        string llmContent,
        string? errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolUseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(llmContent);

        switch (status)
        {
            case ToolExecutionStatus.Success:
                if (errorCode is not null)
                {
                    throw new ArgumentException("Success の ToolResult は ErrorCode を持てません。", nameof(ErrorCode));
                }

                break;
            case ToolExecutionStatus.Failed:
            case ToolExecutionStatus.Cancelled:
            case ToolExecutionStatus.TimedOut:
                ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(Status), status, "未知の ToolExecutionStatus です。");
        }

        return true;
    }
}
