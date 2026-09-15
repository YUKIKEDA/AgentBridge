namespace AgentBridge.Core;

/// <summary>
/// プレーンテキストのコンテンツパート
/// </summary>
/// <param name="Text">テキスト本体</param>
public sealed record TextContentPart(string Text) : ContentPart;
