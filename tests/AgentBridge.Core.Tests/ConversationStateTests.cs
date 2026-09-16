using AgentBridge.Core;

namespace AgentBridge.Core.Tests;

public sealed class ConversationStateTests
{
    [Fact]
    public async Task AcquireTurnAsync_ターン進行中に再度呼ぶとInvalidOperationExceptionになること()
    {
        ConversationState state = new();
        await using IConversationTurnLease lease = await state.AcquireTurnAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => state.AcquireTurnAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AcquireTurnAsync_leaseを解放すると再度ターンを取得できること()
    {
        ConversationState state = new();
        IConversationTurnLease firstLease = await state.AcquireTurnAsync(CancellationToken.None);
        await firstLease.DisposeAsync();

        await using IConversationTurnLease secondLease = await state.AcquireTurnAsync(CancellationToken.None);

        Assert.NotNull(secondLease);
    }

    [Fact]
    public async Task AcquireTurnAsync_キャンセル済みトークンを渡すとOperationCanceledExceptionになること()
    {
        ConversationState state = new();
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => state.AcquireTurnAsync(cts.Token));
    }

    [Fact]
    public async Task AppendUserMessage_呼ぶとMessagesの末尾にユーザーメッセージが追加されること()
    {
        ConversationState state = new();
        ChatMessage message = ChatMessage.FromUser("hello");

        await using (IConversationTurnLease lease = await state.AcquireTurnAsync(CancellationToken.None))
        {
            lease.AppendUserMessage(message);
        }

        Assert.Same(message, Assert.Single(state.Messages));
    }

    [Fact]
    public async Task AppendUserMessage_AssistantロールのメッセージだとArgumentExceptionになること()
    {
        ConversationState state = new();
        ChatMessage assistantMessage = ChatMessage.FromAssistant(new TextContentPart("hi"));

        await using IConversationTurnLease lease = await state.AcquireTurnAsync(CancellationToken.None);

        Assert.Throws<ArgumentException>(() => lease.AppendUserMessage(assistantMessage));
    }

    [Fact]
    public async Task AppendAssistantMessage_UserロールのメッセージだとArgumentExceptionになること()
    {
        ConversationState state = new();
        ChatMessage userMessage = ChatMessage.FromUser("hello");

        await using IConversationTurnLease lease = await state.AcquireTurnAsync(CancellationToken.None);

        Assert.Throws<ArgumentException>(() => lease.AppendAssistantMessage(userMessage));
    }

    [Fact]
    public async Task 一連の追加操作でMessagesが呼び出し順で保持されること()
    {
        ConversationState state = new();
        ChatMessage userMessage = ChatMessage.FromUser("検索して");
        ChatMessage assistantMessage = ChatMessage.FromAssistant(new TextContentPart("検索します"));
        ToolResult toolResult = ToolResult.Success("call_1", "見つかりました");

        await using (IConversationTurnLease lease = await state.AcquireTurnAsync(CancellationToken.None))
        {
            lease.AppendUserMessage(userMessage);
            lease.AppendAssistantMessage(assistantMessage);
            lease.AppendToolResults([toolResult]);
        }

        Assert.Equal(3, state.Messages.Count);
        Assert.Same(userMessage, state.Messages[0]);
        Assert.Same(assistantMessage, state.Messages[1]);

        ChatMessage toolMessage = state.Messages[2];
        Assert.Equal(ChatRole.Tool, toolMessage.Role);
        ToolResultContentPart part = Assert.IsType<ToolResultContentPart>(Assert.Single(toolMessage.Content));
        Assert.Same(toolResult, part.Result);
    }

    [Fact]
    public async Task AppendToolResults_複数件指定すると1件のToolメッセージにまとまること()
    {
        ConversationState state = new();
        ToolResult first = ToolResult.Success("call_1", "ok1");
        ToolResult second = ToolResult.Failed("call_2", "NOT_FOUND", "ok2");

        await using (IConversationTurnLease lease = await state.AcquireTurnAsync(CancellationToken.None))
        {
            lease.AppendToolResults([first, second]);
        }

        ChatMessage toolMessage = Assert.Single(state.Messages);
        Assert.Equal(ChatRole.Tool, toolMessage.Role);
        Assert.Equal(2, toolMessage.Content.Count);
        Assert.Same(first, Assert.IsType<ToolResultContentPart>(toolMessage.Content[0]).Result);
        Assert.Same(second, Assert.IsType<ToolResultContentPart>(toolMessage.Content[1]).Result);
    }

    [Fact]
    public async Task Messages_直接キャストして書き換えようとしても例外になること()
    {
        ConversationState state = new();
        await using (IConversationTurnLease lease = await state.AcquireTurnAsync(CancellationToken.None))
        {
            lease.AppendUserMessage(ChatMessage.FromUser("hello"));
        }

        IReadOnlyList<ChatMessage> messages = state.Messages;
        Assert.Throws<NotSupportedException>(() => ((IList<ChatMessage>)messages).Add(ChatMessage.FromUser("injected")));
    }

    [Fact]
    public async Task DisposeAsync_複数回呼んでも例外にならないこと()
    {
        ConversationState state = new();
        IConversationTurnLease lease = await state.AcquireTurnAsync(CancellationToken.None);

        await lease.DisposeAsync();
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_後にAppendすると例外になること()
    {
        ConversationState state = new();
        IConversationTurnLease lease = await state.AcquireTurnAsync(CancellationToken.None);
        await lease.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => lease.AppendUserMessage(ChatMessage.FromUser("hello")));
    }
}
