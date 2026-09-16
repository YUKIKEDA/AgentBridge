namespace AgentBridge.Core;

/// <summary>
/// <see cref="AgentBridgeHost.Create"/> の組み立てオプション
/// </summary>
public sealed class AgentBridgeHostOptions
{
    /// <summary>
    /// 設計上の既定反復上限
    /// </summary>
    public const int DefaultMaximumIterationsPerRequest = 10;

    /// <summary>
    /// Gets 1 リクエストあたりの function invocation 反復上限
    /// </summary>
    /// <remarks>
    /// Agent Framework 既定の 40 ではなく、設計上の 10 をホスト既定にする
    /// </remarks>
    public int MaximumIterationsPerRequest { get; init; } = DefaultMaximumIterationsPerRequest;

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
    public string? Instructions { get; init; }
}
