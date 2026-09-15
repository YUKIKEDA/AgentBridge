using System.Text.Json;
using AgentBridge.Core;

namespace AgentBridge.Core.Tests;

public sealed class ChatMessageModelTests
{
    [Fact]
    public void FromUser_文字列を指定するとUserロールのテキストメッセージが生成されること()
    {
        ChatMessage message = ChatMessage.FromUser("hello");

        Assert.Equal(ChatRole.User, message.Role);
        TextContentPart part = Assert.IsType<TextContentPart>(Assert.Single(message.Content));
        Assert.Equal("hello", part.Text);
    }

    [Fact]
    public void FromAssistant_複数のパートを指定したとき順序を保持してメッセージが生成されること()
    {
        using JsonDocument inputDoc = JsonDocument.Parse("""{"q":"test"}""");

        ToolUsePart toolUse = new("call_1", "search", inputDoc.RootElement.Clone());
        ChatMessage message = ChatMessage.FromAssistant(
            new TextContentPart("searching"),
            toolUse);

        Assert.Equal(ChatRole.Assistant, message.Role);
        Assert.Equal(2, message.Content.Count);
        Assert.IsType<TextContentPart>(message.Content[0]);
        ToolUsePart actualTool = Assert.IsType<ToolUsePart>(message.Content[1]);
        Assert.Equal("call_1", actualTool.ToolUseId);
        Assert.Equal("search", actualTool.ToolName);
        Assert.Equal(JsonValueKind.Object, actualTool.Input.ValueKind);
    }

    [Fact]
    public void ToolDefinition_指定したJSONスキーマや属性を保持してインスタンスが作成されること()
    {
        using JsonDocument schemaDoc = JsonDocument.Parse(
            """{"type":"object","properties":{"q":{"type":"string"}}}""");

        ToolDefinition definition = new(
            "search",
            "Search documents",
            schemaDoc.RootElement.Clone());

        Assert.Equal("search", definition.Name);
        Assert.Equal("Search documents", definition.Description);
        Assert.Equal(JsonValueKind.Object, definition.InputSchema.ValueKind);
        Assert.True(definition.InputSchema.TryGetProperty("type", out JsonElement type));
        Assert.Equal("object", type.GetString());
    }
}
