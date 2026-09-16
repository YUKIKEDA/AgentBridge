using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentBridge.Wpf;

/// <summary>
/// 同一 <see cref="AgentSession"/> に対するストリーミング実行を 1 本に制限する
/// </summary>
public sealed class AgentRunController : INotifyPropertyChanged, IDisposable
{
    private readonly AIAgent agent;
    private readonly AgentSession session;
    private readonly object gate = new();
    private readonly SynchronizationContext? synchronizationContext;
    private CancellationTokenSource? runCts;
    private bool isBusy;
    private bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentRunController"/> class
    /// </summary>
    /// <param name="agent">実行するエージェント</param>
    /// <param name="session">会話セッション。同時実行は 1 つに制限する</param>
    /// <exception cref="ArgumentNullException"><paramref name="agent"/> または <paramref name="session"/> が <see langword="null"/></exception>
    public AgentRunController(AIAgent agent, AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(session);
        this.agent = agent;
        this.session = session;
        this.synchronizationContext = SynchronizationContext.Current;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets 紐づくエージェント
    /// </summary>
    public AIAgent Agent => this.agent;

    /// <summary>
    /// Gets 紐づくセッション
    /// </summary>
    public AgentSession Session => this.session;

    /// <summary>
    /// Gets a value indicating whether ストリーミング実行中である
    /// </summary>
    public bool IsBusy
    {
        get
        {
            lock (this.gate)
            {
                return this.isBusy;
            }
        }
    }

    /// <summary>
    /// ユーザー入力をストリーミング実行する
    /// </summary>
    /// <param name="userInput">ユーザー発話</param>
    /// <param name="cancellationToken">呼び出し元のキャンセル トークン</param>
    /// <returns>Agent Framework の更新列</returns>
    /// <exception cref="ArgumentException"><paramref name="userInput"/> が空</exception>
    /// <exception cref="InvalidOperationException">既に実行中</exception>
    /// <exception cref="ObjectDisposedException">破棄済み</exception>
    public IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        string userInput,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userInput);
        return this.RunStreamingAsync([new ChatMessage(ChatRole.User, userInput)], cancellationToken);
    }

    /// <summary>
    /// メッセージ列をストリーミング実行する
    /// </summary>
    /// <param name="messages">今回送るメッセージ</param>
    /// <param name="cancellationToken">呼び出し元のキャンセル トークン</param>
    /// <returns>Agent Framework の更新列</returns>
    /// <exception cref="ArgumentNullException"><paramref name="messages"/> または列内の要素が <see langword="null"/></exception>
    /// <exception cref="InvalidOperationException">既に実行中</exception>
    /// <exception cref="ObjectDisposedException">破棄済み</exception>
    public async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ChatMessage[] snapshot = [.. messages];
        foreach (ChatMessage message in snapshot)
        {
            ArgumentNullException.ThrowIfNull(message);
        }

        this.BeginRun();
        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            this.GetRunToken());
        try
        {
            await foreach (AgentResponseUpdate update in this.agent
                .RunStreamingAsync(snapshot, this.session, cancellationToken: linked.Token)
                .ConfigureAwait(false))
            {
                yield return update;
            }
        }
        finally
        {
            linked.Dispose();
            this.EndRun();
        }
    }

    /// <summary>
    /// 実行中なら協調キャンセルする。実行していなければ何もしない
    /// </summary>
    public void Cancel()
    {
        CancellationTokenSource? cts;
        lock (this.gate)
        {
            cts = this.runCts;
        }

        cts?.Cancel();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this.Cancel();
        lock (this.gate)
        {
            this.runCts?.Dispose();
            this.runCts = null;
            this.disposed = true;
            this.isBusy = false;
        }
    }

    private void BeginRun()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        lock (this.gate)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            if (this.isBusy)
            {
                throw new InvalidOperationException("同一セッションで同時に実行することはできません。");
            }

            this.runCts = new CancellationTokenSource();
            this.isBusy = true;
        }

        this.RaiseIsBusyChanged();
    }

    private void EndRun()
    {
        lock (this.gate)
        {
            this.runCts?.Dispose();
            this.runCts = null;
            if (!this.isBusy)
            {
                return;
            }

            this.isBusy = false;
        }

        this.RaiseIsBusyChanged();
    }

    private CancellationToken GetRunToken()
    {
        lock (this.gate)
        {
            return this.runCts?.Token ?? CancellationToken.None;
        }
    }

    private void RaiseIsBusyChanged()
    {
        PropertyChangedEventArgs args = new(nameof(this.IsBusy));
        if (this.synchronizationContext is { } context)
        {
            context.Post(_ => this.PropertyChanged?.Invoke(this, args), null);
            return;
        }

        this.PropertyChanged?.Invoke(this, args);
    }
}
