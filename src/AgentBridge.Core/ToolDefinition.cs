using System.Text.Json;

namespace AgentBridge.Core;

/// <summary>
/// LLM に提示する、プロバイダ非依存のツール定義
/// </summary>
/// <param name="Name">ツール名</param>
/// <param name="Description">人間／LLM 向けの説明</param>
/// <param name="InputSchema">引数の JSON Schema 文書（特定の Schema ライブラリに非依存）</param>
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonElement InputSchema);
