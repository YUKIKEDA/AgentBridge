namespace AgentBridge.Core;

/// <summary>
/// 会話履歴とターン排他（<see cref="IConversationTurnLease"/>）を保持する実行コンテキスト
/// 状態は呼び出し元が保持し、<c>ConversationLoop</c> 自体はステートレス（複数タブでは State を分け、Loop は共有できる）
/// </summary>
public sealed class ConversationState
{
    private readonly List<ChatMessage> messages = [];
    private int turnActive;

    /// <summary>
    /// Gets これまでに確定した会話履歴（受信順）
    /// 外部コードはこの列を直接書き換えられない。書き込みは <see cref="IConversationTurnLease"/> 経由のみ
    /// </summary>
    public IReadOnlyList<ChatMessage> Messages => this.messages.AsReadOnly();

    /// <summary>
    /// ターンロックを取得します
    /// 同一 <see cref="ConversationState"/> に対して同時に実行できるターンは1つだけです
    /// </summary>
    /// <param name="ct">取得前キャンセル用のトークン</param>
    /// <returns>取得したターンロック。<see cref="IAsyncDisposable.DisposeAsync"/> で解放する</returns>
    /// <exception cref="InvalidOperationException">既に進行中のターンがある場合</exception>
    public Task<IConversationTurnLease> AcquireTurnAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (Interlocked.CompareExchange(ref this.turnActive, 1, 0) != 0)
        {
            throw new InvalidOperationException("既に進行中のターンがあるため、新しいターンを開始できません");
        }

        return Task.FromResult<IConversationTurnLease>(new TurnLease(this));
    }

    private void Append(ChatMessage message)
    {
        this.messages.Add(message);
    }

    private void ReleaseTurn()
    {
        Volatile.Write(ref this.turnActive, 0);
    }

    private sealed class TurnLease(ConversationState state) : IConversationTurnLease
    {
        private int disposed;

        public void AppendUserMessage(ChatMessage message)
        {
            this.EnsureNotDisposed();
            ArgumentNullException.ThrowIfNull(message);
            if (message.Role != ChatRole.User)
            {
                throw new ArgumentException("ユーザーメッセージには ChatRole.User が必要です", nameof(message));
            }

            state.Append(message);
        }

        public void AppendAssistantMessage(ChatMessage message)
        {
            this.EnsureNotDisposed();
            ArgumentNullException.ThrowIfNull(message);
            if (message.Role != ChatRole.Assistant)
            {
                throw new ArgumentException("アシスタントメッセージには ChatRole.Assistant が必要です", nameof(message));
            }

            state.Append(message);
        }

        public void AppendToolResults(IReadOnlyList<ToolResult> results)
        {
            this.EnsureNotDisposed();
            state.Append(ChatMessage.FromToolResults(results));
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) == 0)
            {
                state.ReleaseTurn();
            }

            return ValueTask.CompletedTask;
        }

        private void EnsureNotDisposed()
        {
            if (Volatile.Read(ref this.disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(IConversationTurnLease));
            }
        }
    }
}
