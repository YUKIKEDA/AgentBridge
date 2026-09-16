using Microsoft.Extensions.AI;

namespace AgentBridge.Core.Tests;

internal sealed class ScriptedChatClient : IChatClient
{
    private readonly Func<int, IReadOnlyList<ChatMessage>, CancellationToken, Task<ChatResponse>> script;
    private int callCount;

    public ScriptedChatClient(Func<int, ChatResponse> script)
        : this((call, _, _) => Task.FromResult(script(call)))
    {
    }

    public ScriptedChatClient(Func<int, IReadOnlyList<ChatMessage>, CancellationToken, Task<ChatResponse>> script)
    {
        this.script = script;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        int call = Interlocked.Increment(ref this.callCount);
        return await this.script(call, [.. messages], cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await this.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }
}
