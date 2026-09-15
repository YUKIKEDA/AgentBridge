using AgentBridge.Core;

namespace AgentBridge.Core.Tests;

public sealed class LlmContentTruncatorTests
{
    [Fact]
    public void Truncate_上限未満の場合はそのまま返されること()
    {
        string content = new('a', 10);

        string actual = LlmContentTruncator.Truncate(content, maxLength: 20);

        Assert.Equal(content, actual);
    }

    [Fact]
    public void Truncate_文字数が上限と等しい場合はそのまま返されること()
    {
        string content = new('a', 20);

        string actual = LlmContentTruncator.Truncate(content, maxLength: 20);

        Assert.Equal(content, actual);
    }

    [Fact]
    public void Truncate_上限を超える場合は末尾が切り詰められ省略文字数の注記が付与されること()
    {
        string content = new('a', 25);

        string actual = LlmContentTruncator.Truncate(content, maxLength: 20);

        Assert.StartsWith(new string('a', 20), actual, StringComparison.Ordinal);
        Assert.Contains("[Truncated: output exceeded limit, remaining 5 characters omitted]", actual, StringComparison.Ordinal);
    }

    [Fact]
    public void Truncate_maxLengthを省略すると既定値30000文字が使われること()
    {
        string content = new('a', LlmContentTruncator.DefaultMaxLength + 1);

        string actual = LlmContentTruncator.Truncate(content);

        Assert.Contains("remaining 1 characters omitted", actual, StringComparison.Ordinal);
    }

    [Fact]
    public void Truncate_maxLengthが0以下だと例外になること()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LlmContentTruncator.Truncate("abc", maxLength: 0));
    }

    [Fact]
    public void Truncate_contentがnullだと例外になること()
    {
        Assert.Throws<ArgumentNullException>(() => LlmContentTruncator.Truncate(null!));
    }
}
