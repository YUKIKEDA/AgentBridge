using System.Text.Json;

namespace AgentBridge.Core;

/// <summary>
/// Tool invocation requested by the model (also used as a content part in assistant messages).
/// </summary>
/// <param name="ToolUseId">Provider-assigned tool use identifier (unique within a response).</param>
/// <param name="ToolName">Registered tool name.</param>
/// <param name="Input">Fully parsed tool arguments as JSON.</param>
public sealed record ToolUsePart(
    string ToolUseId,
    string ToolName,
    JsonElement Input) : ContentPart;
