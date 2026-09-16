namespace AgentBridge.Core;

/// <summary>
/// 順序付きコンテンツパートで構成される、プロバイダ非依存のチャットメッセージ
/// </summary>
/// <param name="Role">メッセージの役割</param>
/// <param name="Content">コンテンツパートの列（テキスト、ツール呼び出し、ツール結果など）</param>
public sealed record ChatMessage(
    ChatRole Role,
    IReadOnlyList<ContentPart> Content)
{
    /// <summary>
    /// テキスト1件からなるユーザーメッセージを作成します
    /// </summary>
    /// <param name="text">ユーザー入力テキスト</param>
    /// <returns>ユーザー向けの <see cref="ChatMessage"/></returns>
    public static ChatMessage FromUser(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return new ChatMessage(ChatRole.User, [new TextContentPart(text)]);
    }

    /// <summary>
    /// コンテンツパート列からアシスタントメッセージを作成します
    /// </summary>
    /// <param name="parts">順序付きコンテンツパート</param>
    /// <returns>アシスタント向けの <see cref="ChatMessage"/></returns>
    public static ChatMessage FromAssistant(params ContentPart[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Length == 0)
        {
            throw new ArgumentException("アシスタントメッセージには1件以上のコンテンツパートが必要です。", nameof(parts));
        }

        return new ChatMessage(ChatRole.Assistant, parts);
    }

    /// <summary>
    /// ツール実行結果の列から Tool ロールのメッセージを作成します
    /// </summary>
    /// <param name="results">先行する tool_use に対応するツール実行結果の列</param>
    /// <returns>Tool ロール向けの <see cref="ChatMessage"/></returns>
    public static ChatMessage FromToolResults(IReadOnlyList<ToolResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0)
        {
            throw new ArgumentException("Tool メッセージには1件以上のツール実行結果が必要です。", nameof(results));
        }

        ContentPart[] parts = new ContentPart[results.Count];
        for (int i = 0; i < results.Count; i++)
        {
            parts[i] = new ToolResultContentPart(results[i]);
        }

        return new ChatMessage(ChatRole.Tool, parts);
    }
}
