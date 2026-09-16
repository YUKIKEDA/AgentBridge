namespace AgentBridge.Core;

/// <summary>
/// 先行する tool_use に対応するツール実行結果のコンテンツパート
/// </summary>
/// <param name="Result">ツール実行結果</param>
public sealed record ToolResultContentPart(ToolResult Result) : ContentPart;
