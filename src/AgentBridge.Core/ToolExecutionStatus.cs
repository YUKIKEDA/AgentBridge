namespace AgentBridge.Core;

/// <summary>
/// ツール実行結果の状態
/// </summary>
public enum ToolExecutionStatus
{
    /// <summary>ツールの実行に成功した</summary>
    Success,

    /// <summary>ツールの実行に失敗した</summary>
    Failed,

    /// <summary>ツールの実行がキャンセルされた</summary>
    Cancelled,

    /// <summary>ツールの実行がタイムアウトした</summary>
    TimedOut,
}
