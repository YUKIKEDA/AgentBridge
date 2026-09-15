namespace AgentBridge.Core;

/// <summary>
/// Provider-neutral chat message composed of ordered content parts.
/// </summary>
/// <param name="Role">Message role.</param>
/// <param name="Content">Ordered content parts (text, tool use, tool result, etc.).</param>
public sealed record ChatMessage(
    ChatRole Role,
    IReadOnlyList<ContentPart> Content)
{
    /// <summary>
    /// Creates a user message with a single text part.
    /// </summary>
    /// <param name="text">User text.</param>
    /// <returns>A user <see cref="ChatMessage"/>.</returns>
    public static ChatMessage FromUser(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return new ChatMessage(ChatRole.User, [new TextContentPart(text)]);
    }

    /// <summary>
    /// Creates an assistant message from content parts.
    /// </summary>
    /// <param name="parts">Ordered content parts.</param>
    /// <returns>An assistant <see cref="ChatMessage"/>.</returns>
    public static ChatMessage FromAssistant(params ContentPart[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Length == 0)
        {
            throw new ArgumentException("Assistant message requires at least one content part.", nameof(parts));
        }

        return new ChatMessage(ChatRole.Assistant, parts);
    }
}
