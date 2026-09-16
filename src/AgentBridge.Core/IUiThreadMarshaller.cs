namespace AgentBridge.Core;

/// <summary>
/// UI スレッドへ処理を載せるためのマーシャラ
/// </summary>
public interface IUiThreadMarshaller
{
    /// <summary>
    /// Gets a value indicating whether 呼び出し元が既に UI スレッド上にいる
    /// </summary>
    bool IsOnUiThread { get; }

    /// <summary>
    /// 同期処理を UI スレッド上で実行する
    /// </summary>
    /// <typeparam name="T">戻り値の型</typeparam>
    /// <param name="action">UI スレッド上で実行する処理</param>
    /// <param name="cancellationToken">キャンセル トークン</param>
    /// <returns>処理の戻り値</returns>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> が <see langword="null"/></exception>
    /// <exception cref="OperationCanceledException">Dispatcher 投入前にキャンセルされた</exception>
    Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default);

    /// <summary>
    /// 非同期処理を UI スレッド上で実行し、内側の <see cref="Task{TResult}"/> をアンラップする
    /// </summary>
    /// <typeparam name="T">戻り値の型</typeparam>
    /// <param name="asyncAction">UI スレッド上で開始する非同期処理</param>
    /// <param name="cancellationToken">キャンセル トークン</param>
    /// <returns>内側のタスクが完了したあとの戻り値</returns>
    /// <exception cref="ArgumentNullException"><paramref name="asyncAction"/> が <see langword="null"/></exception>
    /// <exception cref="OperationCanceledException">Dispatcher 投入前にキャンセルされた</exception>
    Task<T> InvokeAsync<T>(Func<Task<T>> asyncAction, CancellationToken cancellationToken = default);

    /// <summary>
    /// 戻り値のない同期処理を UI スレッド上で実行する
    /// </summary>
    /// <param name="action">UI スレッド上で実行する処理</param>
    /// <param name="cancellationToken">キャンセル トークン</param>
    /// <returns>投入と実行の完了を表すタスク</returns>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> が <see langword="null"/></exception>
    /// <exception cref="OperationCanceledException">Dispatcher 投入前にキャンセルされた</exception>
    Task InvokeAsync(Action action, CancellationToken cancellationToken = default);
}
