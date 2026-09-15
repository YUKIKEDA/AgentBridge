namespace AgentBridge.Core;

/// <summary>
/// 会話履歴におけるメッセージの役割（プロバイダ非依存）。.
/// </summary>
public enum ChatRole
{
    /// <summary>システム／開発者向け指示。.</summary>
    System,

    /// <summary>エンドユーザーのメッセージ。.</summary>
    User,

    /// <summary>モデル（アシスタント）のメッセージ（テキストおよび／またはツール呼び出し）。.</summary>
    Assistant,

    /// <summary>先行する tool_use に対応するツール実行結果。.</summary>
    Tool,
}
