namespace AgentBridge.Core;

/// <summary>
/// 単一ターンの間だけ有効な、会話履歴への書き込み権限
/// <see cref="ConversationState.AcquireTurnAsync"/> が返す。<see cref="IAsyncDisposable.DisposeAsync"/> でターンロックを解放する
/// </summary>
public interface IConversationTurnLease : IAsyncDisposable
{
    /// <summary>
    /// ユーザーメッセージを履歴の末尾に追加します
    /// </summary>
    /// <param name="message">Role が <see cref="ChatRole.User"/> のメッセージ</param>
    void AppendUserMessage(ChatMessage message);

    /// <summary>
    /// アシスタントメッセージを履歴の末尾に追加します
    /// </summary>
    /// <param name="message">Role が <see cref="ChatRole.Assistant"/> のメッセージ</param>
    void AppendAssistantMessage(ChatMessage message);

    /// <summary>
    /// ツール実行結果をまとめて履歴の末尾に追加します
    /// </summary>
    /// <param name="results">先行するアシスタントメッセージの tool_use に対応する結果の列</param>
    void AppendToolResults(IReadOnlyList<ToolResult> results);
}
