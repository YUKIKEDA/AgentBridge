# AgentBridge 設計仕様書

## 1. 概要とスコープ

`AgentBridge` は、クラウドLLM（Claude、GPT等）の Tool Use（関数呼び出し）機能を、既存のデスクトップアプリケーション内に直接組み込むための軽量な .NET 向けインプロセス統合ライブラリです。

### 提供する機能
- **LLM API 通信制御**: プロバイダ非依存の会話ループ処理（`ConversationLoop`）
- **ツール定義の集約管理**: ツール一覧の保持および LLM への提示（`ToolRegistry`）
- **ツールのディスパッチとスレッド調停**: ツール実行の振り分け、UI スレッドへの安全なマーシャリング（`ToolDispatcher`、`IUiThreadMarshaller`）
- **エラー処理・リトライ管理**: 構造化された実行結果と柔軟なリトライ制御（`ToolResult`、`IRetryPolicy`）

### スコープ外（意図的に扱わない領域）
- マルチエージェント協調、長期記憶、プランニング等の包括的なエージェントフレームワーク機能
- ローカル LLM の推論ホスティング
- MCP（Model Context Protocol）等の外部プロセス間連携プロトコル（必要時は将来検討）

---

## 2. パッケージ構成

コアロジックを UI フレームワークおよび特定 LLM SDK から完全に切り離すため、以下の 4 層/パッケージで構成します。

```
AgentBridge.Core                 汎用コアライブラリ（UIフレームワーク・特定SDKに非依存）
├── LlmClient.cs                 LLM 呼び出しのエントリポイント
├── Abstractions/
│   ├── ILlmProvider.cs          LLM プロバイダ接続の拡張インターフェース
│   ├── ChatMessage.cs           プロバイダ非依存のメッセージ表現（各種 ContentPart）
│   ├── ToolDefinition.cs        プロバイダ非依存のツール定義（名前、説明、JsonSchema）
│   └── ProviderEvent.cs         ストリーミングイベント各種
├── ConversationLoop.cs          会話ループ処理本体（tool_use 受信 → 実行 → tool_result 送信）
├── IToolHandler.cs              アプリ側が実装するツール処理の拡張インターフェース
├── ToolRegistry.cs              ツール定義の管理・一覧提供
├── ToolDispatcher.cs            ツールの実行ディスパッチ、並行制御、リトライ管理
├── ToolResult.cs                実行成否およびエラーコードを保持する結果構造体
├── IUiThreadMarshaller.cs       UI スレッドへのマーシャリングインターフェース
└── RetryPolicy.cs               リトライ判定ポリシー（IRetryPolicy / FixedRetryPolicy）

AgentBridge.Anthropic             Anthropic SDK 向け ILlmProvider 実装
AgentBridge.OpenAI                 OpenAI SDK 向け ILlmProvider 実装（互換エンドポイント対応）
AgentBridge.Wpf                  WPF 向けアダプタ層（チャット UI・スレッドマーシャラ）
```

> **Note**: アプリ固有の具体的な `IToolHandler` 実装は、AgentBridge には含めず利用側アプリケーションのプロジェクト内に配置します。

---

## 3. 主要インターフェース（拡張ポイント）

利用側アプリケーションが実装・カスタマイズする拡張インターフェースです。

### 3.1 ツールハンドラ（`IToolHandler`）
アプリケーション側の各操作（検索、描画更新、データ保存など）をカプセル化します。

```csharp
public interface IToolHandler
{
    string ToolName { get; }
    JsonSchema InputSchema { get; }
    bool RequiresUiThread { get; }
    Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct);

    // デフォルト実装（必要に応じてオーバーライド）
    string InProgressLabel => "実行中...";  // UI表示用文言（例: "検索中...", "ハイライト表示中..."）
    string Category => "General";          // 将来のカテゴリ別絞り込み・動的ロード用
}
```

### 3.2 UI スレッドマーシャラ（`IUiThreadMarshaller`）
WPF や Avalonia などの UI フレームワークへのスレッド切り替えを抽象化します。

```csharp
public interface IUiThreadMarshaller
{
    bool IsOnUiThread { get; }

    Task<T> InvokeAsync<T>(Func<T> action, CancellationToken ct = default);
    Task<T> InvokeAsync<T>(Func<Task<T>> asyncAction, CancellationToken ct = default);
    Task InvokeAsync(Action action, CancellationToken ct = default);
}
```

### 3.3 リトライポリシー（`IRetryPolicy`）
ツール呼び出し失敗時に、再試行を促すか手動操作へフォールバックするかを制御します。

```csharp
public interface IRetryPolicy
{
    // attemptCount: 当該ターンの同一ツール累積失敗回数
    // lastResult: 直前の失敗結果（エラーコード等）
    bool ShouldRetry(string toolName, int attemptCount, ToolResult lastResult);
}
```

---

## 4. コンポーネント詳細仕様

### 4.1 プロバイダ抽象化（`ILlmProvider`）

各 LLM SDK の差異は `ILlmProvider` 実装内部に閉じ込め、コア層にはストリーミングイベントとして公開します。

```csharp
public interface ILlmProvider
{
    IAsyncEnumerable<ProviderEvent> SendAsync(
        ConversationState state,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct);
}

// ストリーミングイベント
public abstract record ProviderEvent;
public record TextDelta(string Text) : ProviderEvent;                              // トークン単位のテキスト
public record ToolCallStarted(string ToolUseId, string ToolName) : ProviderEvent;   // ツール名確定（UI実行中表示のトリガー）
public record ToolCallRequested(ToolUsePart Call) : ProviderEvent;                  // 引数パース完了（実行トリガー）
public record TurnComplete(StopReason Reason) : ProviderEvent;
```

- **スキーマ検証**: コア側で過度な型制約は課さず、各プロバイダ実装側で非対応の JSON Schema 構文（例: `oneOf` など）を検知した際に `ProviderSchemaException` をスローする実行時検証方式を採用しています。
- **イベントの粒度**: ツールの引数 JSON の受信断片は内部でバッファリングし、UI 側には「ツール名確定（`ToolCallStarted`）」と「引数パース完了（`ToolCallRequested`）」のみを通知して UI 表示のちらつきを防ぎます。
- **OpenAI 互換性**: `AgentBridge.OpenAI` はベース URL やモデル名を外部設定可能とし、OpenAI API だけでなく互換エンドポイント（社内ゲートウェイや vLLM 等）にも設定のみで接続可能です。

### 4.2 会話ループ制御（`ConversationLoop`）

会話の 1 ターン（ユーザー入力 → LLM 応答 / ツール実行 → 最終応答）を制御します。

- **状態の所有権**: 会話履歴（`ConversationState`）は呼び出し元（ViewModel 等）が保持し、`ConversationLoop` 自体はステートレスに保ちます。これにより、複数タブ等での並行会話でも単一インスタンスを共有できます。
- **キャンセル挙動**: `CancellationToken` が発火した場合、LLM ストリーミング受信は即座に中断します。一方、実行中のツール（`IToolHandler.ExecuteAsync`）には同じトークンを伝えますが、処理を中断するか安全に完了させるかは各ツールの実装判断に委ねます。
- **ターンの再入排他**: ツール実行中に次のメッセージを重複送信する事態を防ぐため、`ConversationState` 内で `Interlocked` による排他制御を行い、未完了ターンの再入には `InvalidOperationException` をスローします（UI 側は `IsBusy` 等で入力を disable にするのが一次防御）。

### 4.3 ツール実行と並行制御（`ToolDispatcher`）

LLM から 1 ターン内に複数のツール呼び出し（並列 tool_use）が返された場合、以下のルールに従ってディスパッチします。

1. **`RequiresUiThread` に応じた分岐**:
   - `false` のツール: `Task.WhenAll` によりバックグラウンドで並列実行します。
   - `true` のツール: UI スレッド（STA）上で 1 つずつ順次実行します（WPF の制約上、直列化が前提）。
2. **失敗の独立性（巻き添えキャンセルの抑止）**:
   - あるツールの実行が失敗しても、並行して走っている他のツール呼び出しは中断しません。全て完了させた上で、失敗したものだけ `is_error: true` の `ToolResult` を LLM へ返却します。
3. **結果の順序整合**:
   - すべての実行完了後、LLM から渡された元のツール呼び出し順に整列し直して会話履歴へ反映します。

```csharp
public class ToolDispatcher
{
    public async Task<IReadOnlyList<ToolResult>> ExecuteAllAsync(
        IReadOnlyList<ToolUsePart> calls, CancellationToken ct)
    {
        var uiCalls = calls.Where(c => GetHandler(c).RequiresUiThread).ToList();
        var nonUiCalls = calls.Where(c => !GetHandler(c).RequiresUiThread).ToList();

        var nonUiTask = Task.WhenAll(nonUiCalls.Select(c => ExecuteOneAsync(c, ct)));

        var uiResults = new List<ToolResult>();
        foreach (var c in uiCalls)
            uiResults.Add(await ExecuteOneAsync(c, ct));

        var nonUiResults = await nonUiTask;
        return MergeInOriginalOrder(calls, uiResults, nonUiResults);
    }
}
```

### 4.4 エラーハンドリングとリトライ

- **アプリ状態の変化への対応**: ツール実行中に対象オブジェクトがユーザー操作等で変更・削除されていた場合、AgentBridge はドキュメント全体をロックするのではなく、ハンドラ側で `ELEMENT_NOT_FOUND` 等のエラーコードを返却する方針をとります。LLM にエラー結果をそのまま戻すことで、候補の再検索など自己修正を促します。
- **リトライ制御**: 連続失敗による無限ループを防ぐため、`IRetryPolicy`（デフォルトは同一ターン・同一ツール 3 回失敗で打ち切り）を適用します。失敗回数カウントはターンごとにリセットされ、過去の失敗が以降の会話を永続的に阻害しないように配慮します。

### 4.5 WPF 実装の技術的考慮（`DispatcherMarshaller`）

WPF の `Dispatcher` と連携する際は、以下の落とし穴を回避する実装としています。

- **例外の確実な伝播**: `DispatcherOperation` をそのまま `await` した場合に例外が正しく呼び出し元へ伝わらない現象を防ぐため、`TaskCompletionSource<T>` でラップして例外を補足・再スローします。
- **同一スレッド呼び出しの最適化**: 既に UI スレッド上で実行されている場合は `_dispatcher.CheckAccess()` で検知し、マーシャリングをバイパスして同期実行します。

---

## 5. バージョニングと拡張方針

- **インターフェースの拡張**: `IToolHandler` などの公開拡張ポイントへメンバーを追加する場合は、C# 8 の **デフォルトインターフェースメソッド（DIM）** を使用します。これにより、ライブラリのマイナーバージョンアップで既存ハンドラ実装のコンパイルエラーを防ぎます。
- **パッケージリリース方針**: `AgentBridge.Core`、`.Anthropic`、`.OpenAI`、`.Wpf` は常に同一のバージョン番号でリリースするロックステップ方式を採用します。
- **動的ロード機能（将来課題）**: 現状は `ToolRegistry` に登録された全ツール定義を毎回 LLM に提示します。将来的にツール数が 100 件規模に肥大化した場合に限り、`Category` 情報を利用したツールの動的検索・ロード（`search_tools` 等）の拡張を検討します。