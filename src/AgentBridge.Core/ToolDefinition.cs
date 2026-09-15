using System.Text.Json;

namespace AgentBridge.Core;

/// <summary>
/// Provider-neutral tool definition presented to the LLM.
/// </summary>
/// <param name="Name">Tool name.</param>
/// <param name="Description">Human/LLM facing description.</param>
/// <param name="InputSchema">JSON Schema document for tool arguments (not tied to a schema library).</param>
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonElement InputSchema);
