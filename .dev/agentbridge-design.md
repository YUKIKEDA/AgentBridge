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
├── ConversationLoop.cs          会話ループ処理本体（リトライガード・tool 実行・履歴コミット）
├── ConversationState.cs         会話履歴とターン排他（TurnLease）の実行コンテキスト
├── AssistantTurnBuilder.cs      ProviderEvent から完全なアシスタント応答を組み立てる内部構築器
├── IToolHandler.cs              アプリ側が実装するツール処理の拡張インターフェース
├── ToolRegistry.cs              ツール定義の管理・一覧提供（ハンドラから Definition 生成）
├── ToolDispatcher.cs            ツールの実行ディスパッチ・並行制御（純粋実行。リトライ状態は持たない）
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

- **`JsonElement input` の寿命（P0）**: `ExecuteAsync` に渡される `input` は **`JsonElement.Clone()` 済み**であり、呼び出し元の `JsonDocument` が Dispose された後もハンドラ内の `await` を跨いで安全に読み取れる。クローン責務は **`ToolDispatcher`**（実行直前に Clone）。
- **`RequiresUiThread` の意味**:
  - `true`: UI スレッド（STA）上で順次実行する。
  - `false`: **任意のスレッドから安全に実行できる**ことをハンドラが保証する（共有可変状態への同期はハンドラ責務）。フェーズ分離は UI/Non-UI 間の競合を抑止するが、Non-UI 同士の並列安全性までは保証しない。
- **`ToolDefinition` の生成**: `ToolRegistry.Register(IToolHandler)` 時にハンドラの `ToolName` / `Description` / `InputSchema` から `ToolDefinition` を生成する。利用側が `ToolDefinition` を手で二重定義しないことで、スキーマ不一致を防ぐ。
- **ツール名の一意性**: `Register` は同一 `ToolName` の重複登録で例外を投げる。登録解除は MVP では任意。
- **UI 非ブロッキング規約**: UI ツールハンドラは UI スレッドを長時間占有（ブロッキング）しない。重い処理は別スレッドへ逃がし、UI 操作のみを UI スレッドで行う。
- **`ConfigureAwait`（P1）**: `RequiresUiThread == true` のハンドラ内では、原則として `.ConfigureAwait(false)` を使わない（UI スレッドへの継続復帰を維持する）。重い処理のみ `Task.Run` でオフロードし、UI コントロール更新の直前で `IUiThreadMarshaller` を明示的に呼ぶ方法も可。

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
// ToolResult.Cancelled(toolUseId, errorCode: "CANCELLED", llmContent: "cancelled")
```

**Status 別契約（P1）**:
- `Success`: `LlmContent` 必須。`ErrorCode` は null。
- `Failed` / `Cancelled` / `TimedOut`: `ErrorCode` 必須。`LlmContent` には LLM 向け短文（理由の要約）を入れる。

**長大コンテンツの切り詰め（P1）**:
- `ConversationOptions.MaxLlmContentLength`（既定 **30000** 文字）を超える `LlmContent` は、Core ユーティリティで末尾を切り詰め、`[Truncated: output exceeded limit, remaining N characters omitted]` 相当の注記を付与する。履歴コミット前（Loop または結果正規化時）に適用する。

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
- **カウント主体・実行ガードの主体はともに `ConversationLoop`**（`ToolDispatcher` はリトライ状態を持たない）。
- **カウント単位**: 同一ターン内の **同一ツール名** ごとに累積する。**引数が異なっていても同一ツールとしてカウント**する。
- 既定実装（`FixedRetryPolicy`）は同一ターン・同一ツール名で 3 回失敗したら `ShouldRetry == false`。
- 失敗回数はターン終了でリセットする。
- **実行ガード（P0）**: `ShouldRetry == false` のツール名を LLM が再度 `tool_use` した場合、Loop がハンドラを実行せず `RETRY_LIMIT_EXCEEDED` の `ToolResult` を自前生成する。超過分は Dispatcher に渡さない（§4.2 / §4.4）。

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

#### ProviderEvent 終端プロトコル（P0）

1 回の `SendAsync` ストリームについて:

1. **正常系**: 終端は必ず **1 回の `TurnComplete`**。その後のイベントは Loop が無視する（プロバイダ実装のバグ扱いでもよい）。
2. **失敗系**: 終端は **`ResponseFailed`**（`TurnComplete` と排他）。`ResponseFailed` 後のイベントは無視する。
3. **キャンセル**: CT 発火により列挙が中断された場合、プロバイダは `TurnComplete` を発火しない。Loop がキャンセル終了として扱う。
4. **`ToolUseId`**: 同一応答内で一意。`ToolCallRequested` の引数は完全にパース済みであること。
5. **`ToolCallStarted` は任意**: `ToolCallRequested` のみのプロバイダも許可する（UI の早期表示用）。
6. **`ToolCallParseFailed`（MaxTokens 切断等）**: ハンドラは実行しない。`TurnComplete` 到達時、当該 ID についてアシスタント側に tool_use 相当を残せる形でコミットし、対応する `ToolResult.Failed(..., "JSON_PARSE_ERROR", "Tool call arguments were truncated or invalid JSON.")` を履歴へ付与して LLM の再試行を促す。

#### AssistantTurnBuilder（P0）

`ProviderEvent` だけでは履歴に必要な「1 つのアシスタントメッセージ」を再構築できない。`ConversationLoop` は内部の `AssistantTurnBuilder` で次を行う。

- `TextDelta` / `ReasoningDelta` / `ToolCallStarted` / `ToolCallRequested` / `ToolCallParseFailed` を受信順に蓄積する。
- 引数 JSON の断片はプロバイダ実装側でバッファし、完成時のみ `ToolCallRequested` を出す。
- **`TurnComplete` を受けたときだけ** `AssistantTurn` を確定する。
- **投機実行は行わない（P0）**: `ToolCallRequested` 時点ではツールを起動しない。`TurnComplete` → 履歴へアシスタントコミット → 一括ディスパッチ、の順とする。
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

- **スキーマ検証**: コア側で過度な型制約は課さず、各プロバイダ実装側で非対応の JSON Schema 構文を検知した際に `ProviderSchemaException` をスローする。
- **OpenAI 互換性**: ベース URL やモデル名を外部設定可能とし、互換エンドポイントにも設定のみで接続可能。

### 4.2 会話ループ制御（`ConversationLoop`）

会話の 1 ターン（ユーザー入力 → LLM 応答 / ツール実行 → 最終応答）を制御します。

#### ターン契約と TurnLease（P0）

- **状態の所有権**: 会話履歴（`ConversationState`）は呼び出し元が保持し、`ConversationLoop` 自体はステートレス。複数タブでは State を分け、Loop インスタンスは共有できる。
- **同時ターンは 1 つ**: 同一 `ConversationState` に対して同時に実行できるターンは 1 つだけ。
- **TurnLease API**: ターンロックは `ConversationState` が所有する。履歴の書き込みは lease 経由のみ。

```csharp
public sealed class ConversationState
{
    public IReadOnlyList<ChatMessage> Messages { get; }

    public Task<ConversationTurnLease> AcquireTurnAsync(CancellationToken ct);
}

public interface ConversationTurnLease : IAsyncDisposable
{
    void AppendUserMessage(ChatMessage message);
    void AppendAssistantMessage(ChatMessage message);
    void AppendToolResults(IReadOnlyList<ToolResult> results);
}

public enum ConversationCompletionReason
{
    Completed,
    Cancelled,
    MaxLlmCallsExceeded,
    ProviderFailed
}

public sealed record ConversationTurnResult(
    ConversationCompletionReason Reason,
    UsageInfo? Usage = null);
```

- 外部コードによる `Messages` の直接書き換えは原則禁止。
- 未完了ターンへの再入は `InvalidOperationException`（UI は `IsBusy` が一次防御）。
- Loop の戻り値は `ConversationTurnResult`。

#### 履歴コミット順序（P0）

単一の原子 API は強制しない。次の順序契約で整合性を取る。

1. `TurnComplete` 後にのみ `AppendAssistantMessage` を呼ぶ。
2. ツール実行（および ParseFailed / リトライガード結果の合成）が **全件確定してから** `AppendToolResults` を呼ぶ。
3. `AppendAssistantMessage` 後にツールが失敗しても、アシスタント側の `tool_use` は履歴に残す（プロバイダが tool_use / tool_result 対を要求するため）。
4. `AppendAssistantMessage` と `AppendToolResults` の間でキャンセルされた場合も、アシスタントは残し、全 tool_use に対する結果（成功・失敗・Cancelled）を揃えてから `AppendToolResults` する。

#### MaxLlmCalls（P0）

- **名称**: 実装・設定とも **`MaxLlmCalls`** とする（旧称 MaxHops と同義だが判定の曖昧さを避ける）。
- **定義**: 当該ターン内で `ILlmProvider.SendAsync` を呼び出せる最大回数。
- **制約**: 最小 **1**、既定 **10**。`0` 以下は不正（オプション検証で拒否）。
- **判定**: 呼び出し **前** に `if (llmCallCount >= options.MaxLlmCalls)` ならそれ以上呼ばず、`ConversationCompletionReason.MaxLlmCallsExceeded` で返す。呼び出し成功開始後に `llmCallCount++`。
- **例**: `MaxLlmCalls = 1` なら初回 `SendAsync` のみ（tool_result 後の再送信なし）。
- 上限到達時も、それまでにコミット済みのユーザー／アシスタント／tool_result は履歴に残す。

#### リトライ上限ガードの配置（P0）

`ConversationLoop` が `calls` を走査し:

- 上限到達ツール名 → 即座に `ToolResult.Failed(..., "RETRY_LIMIT_EXCEEDED", ...)` を生成。
- 未超過分のみ `ToolDispatcher.ExecuteAllAsync` に渡す。
- ParseFailed 由来の `JSON_PARSE_ERROR` も Loop が結果合成する（Dispatcher 非経由）。

#### キャンセル契約（P0）

1. **LLM ストリーミング**: 即座に中断する（`TurnComplete` なし）。
2. **実行中ツール**: CT を伝播する。強制 Abort はしない。**完了を待ち、実際の完了結果を採用する**（キャンセル要求後に成功したら `Success` のまま）。
3. **未開始ツール**: `Cancelled`（`ErrorCode: CANCELLED`）とする。
4. **履歴**:
   - アシスタント未コミットのキャンセル → 部分アシスタントは残さない。`TurnCancelled` を UI へ。理由は `Cancelled`。
   - アシスタントコミット済み → 全 tool_use に対する結果を揃えて `AppendToolResults`。追加の `SendAsync` は行わない。
5. **UI 整合（`TurnCancelled`）**: 表示済み `TextDelta` の破棄・取り消しを ViewModel が行えるようにする。
6. **タイムアウト（P1）**: オプションでツール単位の待ち上限。超過時は CT 発火のうえ待ち、なお終わらなければ当該結果を `TimedOut` としうる。強制 Abort はしない。

**複数ツール実行中キャンセルの結果規則**:

| 状態                           | 結果                                               |
| :----------------------------- | :------------------------------------------------- |
| 未開始（Pending）              | `Cancelled`                                        |
| 実行中（Running）→ CT 後に完了 | **実際の完了結果**（Success / Failed / Cancelled） |
| 既に Success / Failed          | そのまま                                           |

### 4.3 ツール実行と並行制御（`ToolDispatcher`）

`ToolDispatcher` は **純粋な実行器** とする。リトライ上限判定や ParseFailed 変換は行わない（Loop の責務）。

1. **`RequiresUiThread` に応じた分岐（既定: フェーズ分離）**:
   - **Phase 1**: Non-UI を `MaxDegreeOfParallelism`（既定: `Environment.ProcessorCount`）付きで並列実行し、完了を待つ。
   - **Phase 2**: UI ツールを UI スレッド上で 1 つずつ順次実行する。
   - **順序逆転のトレードオフ（P1）**: LLM が `[UI, Non-UI]` の順で tool_use を出しても、実行は Non-UI → UI になる。並列 tool_use は独立前提。順序依存がある操作は同一ターンの並列呼び出しにせず、1 往復ずつ実行させる。
   - **Non-UI 同士**: ハンドラがスレッド安全を保証する。
   - UI/Non-UI 同時実行は将来の拡張ポイント（オプトイン時は利用側が並行安全性を保証。UI 同士の順序は必ず維持）。
2. **失敗の独立性**: あるツールが失敗しても他は中断しない。
3. **未捕捉例外の変換（P0）**: `ExecuteOneAsync` は try-catch で保護し、`UNHANDLED_EXCEPTION` の `ToolResult` に変換する。
4. **JsonElement Clone（P0）**: `ExecuteAsync` 呼び出し直前に `input.Clone()` する。
5. **結果の順序整合**: 渡された呼び出し順に整列して返す（Loop がガード結果とマージして履歴へ反映）。

```csharp
public sealed class ToolDispatcherOptions
{
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;
}

public class ToolDispatcher
{
    // リトライ状態は受け取らない。実行可能な calls のみを渡される前提。
    public async Task<IReadOnlyList<ToolResult>> ExecuteAllAsync(
        IReadOnlyList<ToolUsePart> calls, CancellationToken ct)
    {
        // Phase 1: Non-UI（並列・並列度制限・Clone 済み input）
        // Phase 2: UI（順次）
        // MergeInOriginalOrder
    }
}
```

### 4.4 エラーハンドリングとリトライ

- **アプリ状態の変化への対応**: `ELEMENT_NOT_FOUND` 等を返し、LLM の自己修正を促す。
- **リトライ制御（LLM self-correction）**:
  - `IRetryPolicy` を `ConversationLoop` が適用（同一ターン・同一ツール名、引数違いは問わない）。
  - `ShouldRetry == false` 直後の結果にはメタ指示を `LlmContent` に付与（既定）。
  - **再呼び出し時**は Loop の実行ガードで `RETRY_LIMIT_EXCEEDED`（Dispatcher 非経由）。
  - オプションで `tools` 一時除外も可。例外でループを落とさない。
- **MaxLlmCalls との関係**: ツール別上限に加え、`SendAsync` 回数上限が最終安全弁。
- **LLM 向け / UI 向け**: `LlmContent` と `DiagnosticDetails` を分離。機密・スタックを LLM 向けに載せない。切り詰めは §3.1.1。

### 4.5 WPF 実装の技術的考慮（`DispatcherMarshaller`）

- **例外の確実な伝播**: `TaskCompletionSource<T>` でラップして再スロー。
- **`Func<Task<T>>` のアンラップ**: 二重 Task を露出しない（§3.2）。
- **同一スレッド最適化**: `_dispatcher.CheckAccess()` でバイパス。
- **キャンセル**: 投入前キャンセルと実行開始後の CT 伝播のみ（§3.2）。

---

## 5. バージョニングと拡張方針

- **インターフェースの拡張**: 公開拡張ポイントへのメンバー追加は DIM を使用。破壊的変更はメジャーで検討。
- **パッケージリリース方針**: Core / Anthropic / OpenAI / Wpf はロックステップ版付け。
- **動的ロード機能（将来課題）**: ツール 100 件規模まで肥大化した場合に `Category` ベースの動的ロードを検討。

### 5.1 拡張ポイント / 将来課題（Core MVP の必須ではない）

| 項目                        | 扱い               | 方針                                                                              |
| :-------------------------- | :----------------- | :-------------------------------------------------------------------------------- |
| ツール実行承認              | 拡張ポイント（P1） | 必須ポリシーにしない。`IToolApprovalHandler` 等                                   |
| 読み取り専用 / 副作用の分類 | ガイドライン       | Core 最小 API は維持                                                              |
| Undo / トランザクション     | 文書のみ           | Core API 化しない                                                                 |
| Context pruning（履歴全体） | 将来課題（P2）     | 単発 `LlmContent` 切り詰め（§3.1.1）とは別。履歴要約は MVP 後                     |
| 永続化 / 再起動復元         | 将来課題（P2）     | Core 型は STJ でシリアライズしやすい DTO とする。`JsonPolymorphic` 必須化はしない |
| `ToolHandlerBase<TInput>`   | DX（P2）           | デシリアライズ基底                                                                |
| JsonSchema 自動生成         | DX（P2）           | `JsonSchemaExporter` 等のヘルパー                                                 |
| Reasoning 本格対応          | 段階導入           | `ReasoningDelta` は先置き済み                                                     |

---

## 6. 会話ループ概要（契約まとめ）

```
AcquireTurnLease
  → AppendUserMessage
  → llmCallCount = 0
  → loop:
       if llmCallCount >= MaxLlmCalls
            → return MaxLlmCallsExceeded / ReleaseLease
       SendAsync（開始後 llmCallCount++）
         → AssistantTurnBuilder（投機実行なし）
         → 終端:
              TurnComplete
                → AppendAssistantMessage
                → ToolCalls? 
                     yes → Loop が blocked（RETRY_LIMIT / JSON_PARSE_ERROR）と runnable に分割
                           → runnable のみ ToolDispatcher（Phase1 Non-UI → Phase2 UI）
                           → 全結果確定後 AppendToolResults
                           → continue（self-correction）
                     no  → return Completed / ReleaseLease
              ResponseFailed
                → 未コミットなら履歴に残さない
                → return ProviderFailed / ReleaseLease
              Cancel
                → 未コミットなら TurnCancelled（UI）・履歴に部分アシスタントなし
                → コミット済みなら in-flight 完了待ち + Pending は Cancelled → AppendToolResults
                → return Cancelled（追加 SendAsync なし）/ ReleaseLease
```

### 6.1 MVP 実装の推奨順序（参考）

1. Core データモデル（`ChatMessage` / `ContentPart` / `ToolResult` / `ConversationState` + TurnLease）
2. Fake `ILlmProvider` で `ConversationLoop` + `AssistantTurnBuilder`（正常系・複数ツール・エラー・キャンセル・MaxLlmCalls・リトライガード）
3. `ToolDispatcher`（フェーズ分離・並列度・Clone・例外変換）
4. Anthropic / OpenAI アダプタ（ストリーム→履歴、`ToolResult` 変換、終端プロトコル）
5. WPF `DispatcherMarshaller`（例外・キャンセル・同一スレッド）
