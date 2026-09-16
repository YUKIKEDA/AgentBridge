using Microsoft.Extensions.AI;

namespace AgentBridge.Core;

/// <summary>
/// <see cref="AIFunction"/> の呼び出しを <see cref="IUiThreadMarshaller"/> 経由に載せる包み
/// </summary>
internal sealed class UiThreadBoundAIFunction : DelegatingAIFunction
{
    public UiThreadBoundAIFunction(AIFunction innerFunction, IUiThreadMarshaller marshaller)
        : base(innerFunction)
    {
        this.Marshaller = marshaller;
    }

    public IUiThreadMarshaller Marshaller { get; }

    /// <inheritdoc />
    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        return new ValueTask<object?>(this.Marshaller.InvokeAsync(
            () => this.InnerFunction.InvokeAsync(arguments, cancellationToken).AsTask(),
            cancellationToken));
    }
}
