namespace AgentBridge.Core;

/// <summary>
/// <see cref="ToolResult.LlmContent"/> 等、LLM に送るテキストの長大コンテンツを切り詰めるユーティリティ
/// </summary>
public static class LlmContentTruncator
{
    /// <summary>切り詰めの既定上限文字数</summary>
    public const int DefaultMaxLength = 30000;

    /// <summary>
    /// 上限文字数を超えるコンテンツの末尾を切り詰め、省略した文字数を示す注記を付与します
    /// 上限以下の場合はそのまま返します
    /// </summary>
    /// <param name="content">切り詰め対象のテキスト</param>
    /// <param name="maxLength">上限文字数（既定 <see cref="DefaultMaxLength"/>）</param>
    /// <returns>上限以下に切り詰められたテキスト</returns>
    public static string Truncate(string content, int maxLength = DefaultMaxLength)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maxLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength), maxLength, "maxLength は 1 以上である必要があります。");
        }

        if (content.Length <= maxLength)
        {
            return content;
        }

        int omittedCount = content.Length - maxLength;
        return $"{content[..maxLength]}\n[Truncated: output exceeded limit, remaining {omittedCount} characters omitted]";
    }
}
