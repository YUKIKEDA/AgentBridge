using Microsoft.Extensions.AI;

namespace AgentBridge.Core;

/// <summary>
/// <see cref="AIFunction"/> を UI スレッド上で実行するよう包む
/// </summary>
public static class UiThreadFunctions
{
    /// <summary>
    /// <paramref name="function"/> の <c>Invoke</c> が <paramref name="marshaller"/> 経由になるよう包む
    /// </summary>
    /// <param name="function">包む関数</param>
    /// <param name="marshaller">UI スレッドへ載せるマーシャラ</param>
    /// <returns>UI スレッド上で実行される <see cref="AIFunction"/></returns>
    /// <exception cref="ArgumentNullException"><paramref name="function"/> または <paramref name="marshaller"/> が <see langword="null"/></exception>
    public static AIFunction Bind(AIFunction function, IUiThreadMarshaller marshaller)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(marshaller);

        if (function is UiThreadBoundAIFunction bound && ReferenceEquals(bound.Marshaller, marshaller))
        {
            return bound;
        }

        return new UiThreadBoundAIFunction(function, marshaller);
    }

    /// <summary>
    /// 列内の <see cref="AIFunction"/> だけを UI スレッドへ載せ、それ以外の <see cref="AITool"/> はそのまま返す
    /// </summary>
    /// <param name="tools">ツール一覧</param>
    /// <param name="marshaller">UI スレッドへ載せるマーシャラ</param>
    /// <returns>包み済みのツール一覧</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tools"/>、<paramref name="marshaller"/>、または列内の要素が <see langword="null"/></exception>
    public static IReadOnlyList<AITool> Bind(IEnumerable<AITool> tools, IUiThreadMarshaller marshaller)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(marshaller);

        List<AITool> bound = [];
        foreach (AITool tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            bound.Add(tool is AIFunction function ? Bind(function, marshaller) : tool);
        }

        return bound;
    }
}
