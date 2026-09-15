using System.Text.Json;

namespace AgentBridge.Core;

/// <summary>
/// モデルが要求したツール呼び出し（アシスタントメッセージのコンテンツパートとしても用いる）。.
/// </summary>
/// <param name="ToolUseId">プロバイダが付与する tool_use ID（同一応答内で一意）。.</param>
/// <param name="ToolName">登録済みツール名。.</param>
/// <param name="Input">パース済みのツール引数 JSON。.</param>
public sealed record ToolUsePart(
    string ToolUseId,
    string ToolName,
    JsonElement Input) : ContentPart;
