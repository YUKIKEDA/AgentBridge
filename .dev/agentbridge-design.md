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
- 破壊的操作の必須承認ダイアログ、Undo トランザクション基盤、コンテキスト自動要約（これらは拡張 / 将来課題。詳細は §5）

---

## 2. パッケージ構成

コアロジックを UI フレームワークおよび特定 LLM SDK から完全に切り離すため、以下の 4 層/パッケージで構成します。

```
AgentBridge.Core                 汎用コアライブラリ（UIフレームワーク・特定SDKに非依存）
├── LlmClient.cs                 LLM 呼び出しのエントリポイント
├── Abstractions/
│   ├── ILlmProvider.cs          LLM プロバイダ接続の拡張インターフェース
│   ├── ChatMessage.cs           プロバイダ非依存のメッセージ表現（各種 ContentPart）
│   ├── ToolDefinition.cs        プロバイダ非依存のツール定義（名前、説明、InputSchema）
│   └── ProviderEvent.cs         ストリーミングイベント各種
├── ConversationLoop.cs          会話ループ処理本体（tool_use 受信 → 実行 → tool_result 送信）
├── ConversationState.cs         会話履歴とターン排他（TurnLease）の実行コンテキスト
├── AssistantTurnBuilder.cs      ProviderEvent から完全なアシスタント応答を組み立てる内部構築器
├── IToolHandler.cs              アプリ側が実装するツール処理の拡張インターフェース
├── ToolRegistry.cs              ツール定義の管理・一覧提供（ハンドラから Definition 生成）
├── ToolDispatcher.cs            ツールの実行ディスパッチ、並行制御、リトライ上限ガード
├── ToolResult.cs                実行成否・ToolUseId・LLM/診断メッセージを保持する結果構造体
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
    // Core は特定の JSON Schema ライブラリに依存しない。スキーマ文書を JsonElement で保持する。
    JsonElement InputSchema { get; }
    bool RequiresUiThread { get; }
    Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct);

    // デフォルト実装（必要に応じてオーバーライド）
    string InProgressLabel => "実行中...";  // UI表示用文言（例: "検索中...", "ハイライト表示中..."）
    string Category => "General";          // 将来のカテゴリ別絞り込み・動的ロード用
    string Description => "";              // ToolDefinition 生成時に使用
}
```

- **`RequiresUiThread` の意味**:
  - `true`: UI スレッド（STA）上で順次実行する。
  - `false`: **任意のスレッドから安全に実行できる**ことをハンドラが保証する（共有可変状態への同期はハンドラ責務）。フェーズ分離は UI/Non-UI 間の競合を抑止するが、Non-UI 同士の並列安全性までは保証しない。
- **`ToolDefinition` の生成**: `ToolRegistry.Register(IToolHandler)` 時にハンドラの `ToolName` / `Description` / `InputSchema` から `ToolDefinition` を生成する。利用側が `ToolDefinition` を手で二重定義しないことで、スキーマ不一致を防ぐ。
- **UI 非ブロッキング規約**: UI ツールハンドラは UI スレッドを長時間占有（ブロッキング）しない。重い処理は別スレッドへ逃がし、UI 操作のみを UI スレッドで行う。`await` 後の継続が UI に戻るかは `SynchronizationContext` に依存するため、ハンドラ実装ガイドで明示する。

### 3.1.1 ツール実行結果（`ToolResult`）

MVP では成功コンテンツを **文字列（テキスト）** とする。プロバイダ固有形式への変換は各アダプタの責務（§4.1）。

```csharp
public enum ToolExecutionStatus
{
    Success,
    Failed,
    Cancelled,
    TimedOut
}

public sealed record ToolResult(
    string ToolUseId,                 // 元の tool_use ID（必須。プロバイダ変換に必要）
    ToolExecutionStatus Status,
    string LlmContent,                // LLM に送る短文（パス・スタック・秘密情報を含めない）
    string? DiagnosticDetails = null, // UI / ログ向け詳細
    string? ErrorCode = null);

// ファクトリ例
// ToolResult.Success(toolUseId, content)
// ToolResult.Failed(toolUseId, errorCode, llmContent, diagnosticDetails?)
// ToolResult.Cancelled(toolUseId, llmContent: "cancelled")
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

**実装契約（`Func<Task<T>>`）**:
- `InvokeAsync<T>(Func<Task<T>>)` は内側の `Task<T>` を必ずアンラップし、呼び出し元には単一の `Task<T>` として完了を返す。
- 例外は `TaskCompletionSource<T>` 経由で呼び出し元へ再スローする（§4.5）。
- 既に UI スレッド上ならマーシャリングをバイパスし、`await asyncAction()` 相当で実行する。
- **キャンセル**: Dispatcher へ投入する **前** に CT が発火していれば投入しない。実行開始後は CT をハンドラ／アクションへ伝播するのみ（強制 Abort しない）。
- `Func<T>` / `Func<Task<T>>` / `Action` のそれぞれについて、例外・キャンセル・同一スレッド最適化の挙動を実装テストで固定する。

### 3.3 リトライポリシー（`IRetryPolicy`）
ツール失敗後に LLM が同一ツールを再度呼ぶ（自己修正）ことを許可するかを制御します。

```csharp
public interface IRetryPolicy
{
    // attemptCount: 当該ターンの同一ツール名の累積失敗回数（ConversationLoop がカウント）
    // lastResult: 直前の失敗結果（エラーコード等）
    bool ShouldRetry(string toolName, int attemptCount, ToolResult lastResult);
}
```

- **既定のリトライ種別は LLM self-correction のみ**とする。`ToolDispatcher` が同一呼び出しを内部で即座に再実行する「Tool execution retry」は MVP では行わない。
- カウント主体は `ConversationLoop`（`ToolDispatcher` ではない）。
- **カウント単位**: 同一ターン内の **同一ツール名** ごとに累積する。**引数が異なっていても同一ツールとしてカウント**する。
- 既定実装（`FixedRetryPolicy`）は同一ターン・同一ツール名で 3 回失敗したら `ShouldRetry == false`。
- 失敗回数はターン終了でリセットする。
- **実行ガード（P0）**: `ShouldRetry == false` になったツールを LLM が再度 `tool_use` した場合、メタ指示に頼らず **ハンドラを実行せず** 即座に `ToolResult.Failed(..., "RETRY_LIMIT_EXCEEDED", ...)` を返す（§4.4）。

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

// ストリーミングイベント（UI / 実行制御向け）
public abstract record ProviderEvent;
public record TextDelta(string Text) : ProviderEvent;
public record ReasoningDelta(string Text) : ProviderEvent; // 推論モデル向け。未使用プロバイダは発火しない（破壊的変更回避の先置き）
public record ToolCallStarted(string ToolUseId, string ToolName) : ProviderEvent;
public record ToolCallRequested(ToolUsePart Call) : ProviderEvent;
public record ToolCallParseFailed(string ToolUseId, string ToolName, string Error) : ProviderEvent;
public record ResponseFailed(Exception Error) : ProviderEvent;
public record TurnComplete(StopReason Reason, UsageInfo? Usage = null) : ProviderEvent;

// 履歴コミット用の完全なアシスタント応答（ConversationLoop 内部で構築）
public sealed record AssistantTurn(
    IReadOnlyList<ContentPart> Content, // Text / ToolUse 等。受信順を保持
    StopReason StopReason,
    UsageInfo? Usage);
```

#### AssistantTurnBuilder（P0）

`ProviderEvent` だけでは履歴に必要な「1 つのアシスタントメッセージ」を再構築できない。`ConversationLoop` は内部の `AssistantTurnBuilder` で次を行う。

- `TextDelta` / `ReasoningDelta` / `ToolCallStarted` / `ToolCallRequested` を受信順に蓄積する（テキストと Tool Use の順序、複数 Tool Use の並び、`tool_use_id` の一意性を保持）。
- 引数 JSON の断片はプロバイダ実装側でバッファし、完成時のみ `ToolCallRequested` を出す（既存方針）。
- **`TurnComplete` を受けたときだけ** `AssistantTurn` を確定し、ターン所有者（`ConversationTurnLease`）経由で履歴へコミットする。
- `ResponseFailed` またはキャンセル時、未完了のビルダ内容は **履歴にコミットしない**（§4.2）。

#### プロバイダ変換責務（P0）

Core の `ChatMessage` / `ContentPart` / `ToolResult` はプロバイダ非依存。SDK リクエスト型への変換は各アダプタが行う。

| 項目              | 方針                                                                                                                     |
| :---------------- | :----------------------------------------------------------------------------------------------------------------------- |
| Anthropic         | 同一アシスタント応答内の全 `tool_use` に対し、次メッセージの `content` に **すべての `tool_result` を過不足なく** 含める |
| OpenAI            | 各ツール結果を独立した `role: "tool"` メッセージとして送る                                                               |
| `ToolUseId`       | Core `ToolResult.ToolUseId` をプロバイダの呼び出し ID にマップする                                                       |
| 成功結果          | MVP はテキスト（`LlmContent`）。JSON 構造が必要になったら後続で拡張                                                      |
| 失敗 / キャンセル | プロバイダの `is_error` 相当へマップする                                                                                 |

- **スキーマ検証**: コア側で過度な型制約は課さず、各プロバイダ実装側で非対応の JSON Schema 構文（例: `oneOf` など）を検知した際に `ProviderSchemaException` をスローする実行時検証方式を採用しています。
- **イベントの粒度**: UI 側には `ToolCallStarted` / `ToolCallRequested`（または `ToolCallParseFailed`）を通知し、ちらつきを防ぐ。
- **完了と失敗の区別**: 正常終了は `TurnComplete`、途中切断・API エラーは `ResponseFailed`。
- **OpenAI 互換性**: `AgentBridge.OpenAI` はベース URL やモデル名を外部設定可能とし、互換エンドポイントにも設定のみで接続可能です。

### 4.2 会話ループ制御（`ConversationLoop`）

会話の 1 ターン（ユーザー入力 → LLM 応答 / ツール実行 → 最終応答）を制御します。

#### ターン契約と TurnLease（P0）

- **状態の所有権**: 会話履歴（`ConversationState`）は呼び出し元（ViewModel 等）が保持し、`ConversationLoop` 自体はステートレスに保つ。複数タブでは State を分け、Loop インスタンスは共有できる。
- **同時ターンは 1 つ**: 同一 `ConversationState` に対して同時に実行できるターンは 1 つだけ。
- **TurnLease API**: ターンロックは `ConversationState` が所有する。履歴の書き込みは lease 経由のみ。

```csharp
public sealed class ConversationState
{
    public IReadOnlyList<ChatMessage> Messages { get; }

    // ロック取得。取得前のキャンセルは可能。解放は DisposeAsync（finally 相当）で必ず行う。
    public Task<ConversationTurnLease> AcquireTurnAsync(CancellationToken ct);
}

public interface ConversationTurnLease : IAsyncDisposable
{
    void AppendUserMessage(ChatMessage message);
    void AppendAssistantMessage(ChatMessage message); // AssistantTurn から構築
    void AppendToolResults(IReadOnlyList<ToolResult> results); // プロバイダ非依存。送信時にアダプタが変換
}
```

- 外部コードによる `Messages` の直接書き換えは原則禁止（生の `List<ChatMessage>` は公開しない）。
- 未完了ターンへの再入（ロック取得失敗）は `InvalidOperationException`（UI は `IsBusy` が一次防御）。

#### MaxHops / MaxLlmCalls（P0）

- **定義**: 当該ターン内で `ILlmProvider.SendAsync` を呼び出せる **最大回数**（初回送信 + tool_result 後の再送信を含む）。名称 `MaxHops` はこの意味で用いる（実装上の別名として `MaxLlmCalls` でもよい）。
- **既定値**: **10**。超過時はそれ以上 LLM を呼ばず、ターンを打ち切って呼び出し元に完了（打ち切り理由付き）を返す。

#### キャンセル契約（P0）

`CancellationToken` が発火した場合の方針を次で固定する。

1. **LLM ストリーミング**: 即座に中断する。
2. **実行中ツール**: 同一トークンを `ExecuteAsync` に伝える。中断するか安全に完了させるかは各ハンドラの判断に委ねる（強制スレッド Abort は行わない）。
3. **履歴と tool_result**:
   - すでにアシスタント側へ `tool_use` を履歴へコミット済みの場合、未完了・キャンセルされた各呼び出しについて `Status: Cancelled` の `ToolResult` を履歴へ書き込む。
   - そのターンでは **追加の LLM 往復は行わない**。
   - ストリーミング途中でアシスタントメッセージが未コミットのままキャンセルされた場合、不完全なアシスタントメッセージは履歴に残さない。
4. **UI との整合（`TurnCancelled`）**: ストリーム中に UI へ流した `TextDelta` 等が履歴に残らない場合、画面とメモリが乖離しうる。Loop は `TurnCancelled`（または同等の完了理由付きイベント）を発火し、ViewModel が表示済みデルタの破棄・取り消し線表示などを行えるようにする。
5. **タイムアウト（P1・推奨）**: オプションでツール単位の待ち上限を設け、超過時はキャンセル要求を発火したうえで待ち、なお完了しない場合はターンを `TimedOut` として失敗扱いにする。強制 Abort はしない。

### 4.3 ツール実行と並行制御（`ToolDispatcher`）

LLM から 1 ターン内に複数のツール呼び出し（並列 tool_use）が返された場合、以下のルールに従ってディスパッチします。

1. **リトライ上限ガード（P0）**: 当該ターンで既に `ShouldRetry == false` のツール名への呼び出しは、ハンドラを実行せず `RETRY_LIMIT_EXCEEDED` の `ToolResult` を返す（メタ指示より優先する水際防御）。
2. **`RequiresUiThread` に応じた分岐（既定: フェーズ分離）**:
   - **Phase 1**: `RequiresUiThread == false` のツールをバックグラウンド並列実行し、**完了を待つ**。並列度は `ToolDispatcherOptions.MaxDegreeOfParallelism`（既定: `Environment.ProcessorCount`）で制限する。
   - **Phase 2**: `RequiresUiThread == true` のツールを UI スレッド（STA）上で 1 つずつ順次実行する。
   - UI ツールと Non-UI ツールを同時に走らせない。
   - **Non-UI 同士**: 並列実行されるため、共有状態へのアクセスは各ハンドラがスレッド安全を保証する。
   - UI/Non-UI 同時実行はオプトイン。その場合も利用側が並行安全性を保証する。
3. **失敗の独立性**: あるツールが失敗しても他は中断しない。完了後、失敗分だけエラー結果として LLM へ返す。
4. **未捕捉例外の変換（P0）**: `ExecuteOneAsync` は try-catch で保護し、未捕捉例外を `UNHANDLED_EXCEPTION` の `ToolResult` に変換する。
5. **結果の順序整合**: 元のツール呼び出し順に整列して履歴へ反映する。

```csharp
public sealed class ToolDispatcherOptions
{
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;
}

public class ToolDispatcher
{
    public async Task<IReadOnlyList<ToolResult>> ExecuteAllAsync(
        IReadOnlyList<ToolUsePart> calls, CancellationToken ct)
    {
        // 1. リトライ上限到達ツールはハンドラ未実行で RETRY_LIMIT_EXCEEDED を返す
        // 2. Phase 1: Non-UI を MaxDegreeOfParallelism 付きで並列実行して完了待ち
        // 3. Phase 2: UI を順次実行
        // 4. MergeInOriginalOrder
    }

    // ExecuteOneAsync: 未捕捉例外は必ず ToolResult に変換する
}
```

### 4.4 エラーハンドリングとリトライ

- **アプリ状態の変化への対応**: 対象オブジェクトが変更・削除されていた場合、ドキュメント全体ロックではなく `ELEMENT_NOT_FOUND` 等を返し、LLM の自己修正を促す。
- **リトライ制御（LLM self-correction）**:
  - `IRetryPolicy`（既定: 同一ターン・同一ツール名 3 回失敗で打ち切り。引数違いは問わない）を `ConversationLoop` が適用する。
  - `ShouldRetry == false` となった直後の `tool_result` には、既定でメタ指示（「試行上限に達した。他の手段を試すかユーザーに説明すること」）を `LlmContent` に付与する。
  - **その後も LLM が同名ツールを呼んだ場合**は §4.3 の実行ガードで `RETRY_LIMIT_EXCEEDED` を返す（ハンドラ未実行）。
  - オプションで当該ターンの残りの往復から `tools` 一覧の一時除外も可能（実装コストは低いが、既定はメタ指示 + 実行ガード）。
  - 打ち切り時に例外でループを落とさない。
- **MaxHops との関係**: ツール別リトライ上限に加え、`SendAsync` 回数上限（§4.2）が最終安全弁。
- **LLM 向け / UI 向け**: `ToolResult.LlmContent` と `DiagnosticDetails` を分離する（§3.1.1）。絶対パス・スタック・秘密情報を LLM 向けに載せない。

### 4.5 WPF 実装の技術的考慮（`DispatcherMarshaller`）

WPF の `Dispatcher` と連携する際は、以下の落とし穴を回避する実装としています。

- **例外の確実な伝播**: `TaskCompletionSource<T>` でラップして例外を補足・再スローする。
- **`Func<Task<T>>` のアンラップ**: 二重 Task を呼び出し元に露出しない（§3.2）。
- **同一スレッド呼び出しの最適化**: `_dispatcher.CheckAccess()` で検知し、マーシャリングをバイパスする。
- **キャンセル**: 投入前キャンセルと実行開始後の CT 伝播のみ（§3.2）。

---

## 5. バージョニングと拡張方針

- **インターフェースの拡張**: `IToolHandler` などの公開拡張ポイントへメンバーを追加する場合は、C# 8 の **デフォルトインターフェースメソッド（DIM）** を使用する。破壊的変更になりやすいメンバー追加はメジャーバージョンで検討する。
- **パッケージリリース方針**: `AgentBridge.Core`、`.Anthropic`、`.OpenAI`、`.Wpf` は常に同一のバージョン番号でリリースするロックステップ方式を採用する。
- **動的ロード機能（将来課題）**: 現状は `ToolRegistry` に登録された全ツール定義を毎回 LLM に提示する。ツール数が 100 件規模に肥大化した場合に限り、`Category` を利用した動的検索・ロードを検討する。

### 5.1 拡張ポイント / 将来課題（Core MVP の必須ではない）

| 項目                               | 扱い               | 方針                                                                                                                          |
| :--------------------------------- | :----------------- | :---------------------------------------------------------------------------------------------------------------------------- |
| ツール実行承認（破壊的操作の確認） | 拡張ポイント（P1） | Core に必須ポリシーは置かない。オプションの `IToolApprovalHandler` 等で Confirm / Deny。`RequiresUiThread` は実行許可ではない |
| 読み取り専用 / 副作用の分類        | ガイドライン       | 利用側で `ToolExecutionPolicy` 相当を付与可。Core の `IToolHandler` 最小面は維持                                              |
| Undo / トランザクション            | 文書のみ           | Core API 化しない。ハンドラ実装ガイドで示す                                                                                   |
| Context pruning（履歴切り詰め）    | 将来課題（P2）     | 長大 `tool_result` の切り詰めフックは MVP 後                                                                                  |
| `ToolHandlerBase<TInput>`          | DX（P2）           | `JsonElement` → `TInput` デシリアライズ基底。MVP ブロッカーではない                                                           |
| JsonSchema 自動生成                | DX（P2）           | .NET 9 `JsonSchemaExporter` 等で DTO からスキーマを生成するヘルパーを Core ユーティリティとして後追い提供可                   |
| Reasoning 系の本格対応             | 段階導入           | `ReasoningDelta` はイベント面に先置き済み。履歴への扱いが必要になった時点で拡充                                               |

---

## 6. 会話ループ概要（契約まとめ）

```
AcquireTurnLease（同一 State で同時ターン不可。解放は DisposeAsync）
  → AppendUserMessage
  → loop while llmCallCount <= MaxHops (SendAsync 回数):
       SendAsync
         → AssistantTurnBuilder が ProviderEvent を蓄積
         → TurnComplete?
              yes → lease.AppendAssistantMessage(AssistantTurn)
                    ToolCalls?
                      yes → リトライ上限ガード
                            → 通過分のみ ToolDispatcher（Phase1 Non-UI → Phase2 UI）
                            → lease.AppendToolResults
                            → continue（self-correction）
                      no  → ターン正常完了 → ReleaseLease
              ResponseFailed / Cancel（未コミット）
                    → 履歴に部分アシスタントを残さない
                    → TurnCancelled を UI へ通知
                    → コミット済み tool_use があれば Cancelled の ToolResult を付与
                    → ReleaseLease（追加 SendAsync なし）
  → MaxHops 超過 → 打ち切り理由付きで完了 → ReleaseLease
```

### 6.1 MVP 実装の推奨順序（参考）

1. Core データモデル（`ChatMessage` / `ContentPart` / `ToolResult` / `ConversationState` + TurnLease）
2. Fake `ILlmProvider` で `ConversationLoop` + `AssistantTurnBuilder`（正常系・複数ツール・エラー・キャンセル）
3. `ToolDispatcher`（フェーズ分離・並列度・例外変換・リトライガード）
4. Anthropic / OpenAI アダプタ（ストリーム→履歴、`ToolResult` 変換）
5. WPF `DispatcherMarshaller`（例外・キャンセル・同一スレッド）
