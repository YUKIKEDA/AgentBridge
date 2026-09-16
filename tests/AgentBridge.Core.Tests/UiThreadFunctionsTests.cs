using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentBridge.Core.Tests;

public sealed class UiThreadFunctionsTests
{
    [Fact]
    public async Task Bind_マーシャラ付きで包むとInvokeがマーシャラを経由すること()
    {
        CapturingUiThreadMarshaller marshaller = new();
        AIFunction inner = AIFunctionFactory.Create(() => "ok", name: "ok");
        AIFunction bound = UiThreadFunctions.Bind(inner, marshaller);

        object? result = await bound.InvokeAsync();

        Assert.Equal("ok", result?.ToString());
        Assert.Equal(1, marshaller.DispatchCount);
    }

    [Fact]
    public async Task Bind_既にUIスレッド上ならマーシャリングをバイパスすること()
    {
        CapturingUiThreadMarshaller marshaller = new() { IsOnUiThread = true };
        AIFunction bound = UiThreadFunctions.Bind(
            AIFunctionFactory.Create(() => "ok", name: "ok"),
            marshaller);

        object? result = await bound.InvokeAsync();

        Assert.Equal("ok", result?.ToString());
        Assert.Equal(0, marshaller.DispatchCount);
    }

    [Fact]
    public async Task Bind_投入前にキャンセルされていると関数本体を呼ばないこと()
    {
        CapturingUiThreadMarshaller marshaller = new();
        bool invoked = false;
        AIFunction bound = UiThreadFunctions.Bind(
            AIFunctionFactory.Create(
                () =>
                {
                    invoked = true;
                    return "ok";
                },
                name: "ok"),
            marshaller);

        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => bound.InvokeAsync(cancellationToken: cts.Token).AsTask());
        Assert.False(invoked);
        Assert.Equal(0, marshaller.DispatchCount);
    }

    [Fact]
    public async Task Bind_内側の例外を呼び出し元へ再スローすること()
    {
        CapturingUiThreadMarshaller marshaller = new();
        AIFunction bound = UiThreadFunctions.Bind(
            AIFunctionFactory.Create(
                static string () => throw new InvalidOperationException("boom"),
                name: "fail"),
            marshaller);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => bound.InvokeAsync().AsTask());
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task Bind_FuncTaskをアンラップして戻り値を返すこと()
    {
        CapturingUiThreadMarshaller marshaller = new();
        bool unwrapped = false;
        await marshaller.InvokeAsync(
            async () =>
            {
                await Task.Yield();
                unwrapped = true;
                return 7;
            });

        Assert.True(unwrapped);
        Assert.Equal(1, marshaller.DispatchCount);
    }

    [Fact]
    public void Bind_AIFunction以外のツールはそのまま返すこと()
    {
        CapturingUiThreadMarshaller marshaller = new();
        AITool declaration = AIFunctionFactory.Create(() => "x", name: "x").AsDeclarationOnly();

        IReadOnlyList<AITool> bound = UiThreadFunctions.Bind([declaration], marshaller);

        Assert.Same(declaration, Assert.Single(bound));
    }

    [Fact]
    public void Bind_同じマーシャラで二重に包まないこと()
    {
        CapturingUiThreadMarshaller marshaller = new();
        AIFunction first = UiThreadFunctions.Bind(
            AIFunctionFactory.Create(() => "ok", name: "ok"),
            marshaller);

        AIFunction second = UiThreadFunctions.Bind(first, marshaller);

        Assert.Same(first, second);
    }
}
