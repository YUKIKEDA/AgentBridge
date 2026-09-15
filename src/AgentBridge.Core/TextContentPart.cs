namespace AgentBridge.Core;

/// <summary>
/// Plain text content part.
/// </summary>
/// <param name="Text">Text payload.</param>
public sealed record TextContentPart(string Text) : ContentPart;
