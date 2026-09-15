namespace AgentBridge.Core;

/// <summary>
/// Role of a chat message in the provider-neutral conversation history.
/// </summary>
public enum ChatRole
{
    /// <summary>System / developer instructions.</summary>
    System,

    /// <summary>End-user message.</summary>
    User,

    /// <summary>Model assistant message (text and/or tool calls).</summary>
    Assistant,

    /// <summary>Tool execution results associated with prior tool uses.</summary>
    Tool,
}
