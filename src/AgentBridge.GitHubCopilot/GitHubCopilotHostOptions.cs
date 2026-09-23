namespace AgentBridge.GitHubCopilot;

/// <summary>
/// <see cref="GitHubCopilotHost.Create"/> の組み立てオプション
/// </summary>
public sealed class GitHubCopilotHostOptions
{
    /// <summary>
    /// Gets エージェント名
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets エージェントの説明
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets システム指示
    /// </summary>
    /// <remarks>
    /// Copilot 既定のシステムメッセージへ追記する
    /// </remarks>
    public string? Instructions { get; init; }

    /// <summary>
    /// Gets 使うモデル名
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> なら Copilot の既定モデル
    /// </remarks>
    public string? Model { get; init; }

    /// <summary>
    /// Gets a value indicating whether エージェントの破棄時に <c>CopilotClient</c> も破棄するか
    /// </summary>
    public bool OwnsClient { get; init; }
}
