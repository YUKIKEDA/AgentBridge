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
├── ConversationState.cs         会話履歴とターン排他の実行コンテキスト
├── IToolHandler.cs              アプリ側が実装するツール処理の拡張インターフェース
├── ToolRegistry.cs              ツール定義の管理・一覧提供
├── ToolDispatcher.cs            ツールの実行ディスパッチ、並行制御
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
    // Core は特定の JSON Schema ライブラリに依存しない。スキーマ文書を JsonElement で保持する。
    JsonElement InputSchema { get; }
    bool RequiresUiThread { get; }
    Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct);

    // デフォルト実装（必要に応じてオーバーライド）
    string InProgressLabel => "実行中...";  // UI表示用文言（例: "検索中...", "ハイライト表示中..."）
    string Category => "General";          // 将来のカテゴリ別絞り込み・動的ロード用
}
```

`ToolDefinition` も同様に `JsonElement InputSchema` を保持します。プロバイダ実装側で非対応構文を検知した際の検証は §4.1 を参照。

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
- `Func<T>` / `Func<Task<T>>` / `Action` のそれぞれについて、例外・キャンセル・同一スレッド最適化の挙動を実装テストで固定する。

### 3.3 リトライポリシー（`IRetryPolicy`）
ツール失敗後に LLM が同一ツールを再度呼ぶ（自己修正）ことを許可するかを制御します。

```csharp
public interface IRetryPolicy
{
    // attemptCount: 当該ターンの同一ツール累積失敗回数（ConversationLoop がカウント）
    // lastResult: 直前の失敗結果（エラーコード等）
    bool ShouldRetry(string toolName, int attemptCount, ToolResult lastResult);
}
```

- **既定のリトライ種別は LLM self-correction のみ**とする。`ToolDispatcher` が同一呼び出しを内部で即座に再実行する「Tool execution retry」は MVP では行わない。
- カウント主体は `ConversationLoop`（`ToolDispatcher` ではない）。
- 既定実装（`FixedRetryPolicy`）は同一ターン・同一ツール名で 3 回失敗したら `ShouldRetry == false`。
- 失敗回数はターン終了でリセットする。

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
public record ToolCallParseFailed(string ToolUseId, string ToolName, string Error) : ProviderEvent; // 引数パース失敗（UI の実行中表示解除）
public record ResponseFailed(Exception Error) : ProviderEvent;                      // ストリーム切断・プロバイダエラー
public record TurnComplete(StopReason Reason, UsageInfo? Usage = null) : ProviderEvent;
```

- **スキーマ検証**: コア側で過度な型制約は課さず、各プロバイダ実装側で非対応の JSON Schema 構文（例: `oneOf` など）を検知した際に `ProviderSchemaException` をスローする実行時検証方式を採用しています。
- **イベントの粒度**: ツールの引数 JSON の受信断片は内部でバッファリングし、UI 側には「ツール名確定（`ToolCallStarted`）」と「引数パース完了（`ToolCallRequested`）」を通知します。`ToolCallStarted` の後に JSON が不正な場合は `ToolCallRequested` の代わりに `ToolCallParseFailed` を発火し、UI が「実行中」表示を解除できるようにします。
- **完了と失敗の区別**: 正常終了は `TurnComplete`、途中切断・API エラーは `ResponseFailed` とする。`UsageInfo`（入出力トークン等）は `TurnComplete` に任意付与する。
- **OpenAI 互換性**: `AgentBridge.OpenAI` はベース URL やモデル名を外部設定可能とし、OpenAI API だけでなく互換エンドポイント（社内ゲートウェイや vLLM 等）にも設定のみで接続可能です。

### 4.2 会話ループ制御（`ConversationLoop`）

会話の 1 ターン（ユーザー入力 → LLM 応答 / ツール実行 → 最終応答）を制御します。

#### ターン契約（P0）

- **状態の所有権**: 会話履歴（`ConversationState`）は呼び出し元（ViewModel 等）が保持し、`ConversationLoop` 自体はステートレスに保ちます。これにより、複数タブ等での並行会話でも単一の Loop インスタンスを共有できます。
- **同時ターンは 1 つ**: 同一 `ConversationState` に対して同時に実行できるターンは 1 つだけ。未完了ターンへの再入には `InvalidOperationException` をスローする（UI 側は `IsBusy` 等で入力を disable にするのが一次防御）。
- **履歴変更の権限**: 履歴（メッセージ列）の変更は、そのターンを所有している実行だけが行う。呼び出し元がターン外で `Messages` を直接書き換えないよう、`ConversationState` は書き込み可能な生の `List<ChatMessage>` を公開しない（読み取り用の `IReadOnlyList` と、Loop 経由の更新 API に限定する）。
- **MaxHops**: 1 ターン内の「LLM 往復」（ユーザー入力後の初回送信 + tool_result 送信の繰り返し）に上限を設ける。既定値は **10**。超過時はそれ以上 LLM を呼ばず、ターンを打ち切って呼び出し元に完了（打ち切り理由付き）を返す。

#### キャンセル契約（P0）

`CancellationToken` が発火した場合の方針を次で固定する。

1. **LLM ストリーミング**: 即座に中断する。
2. **実行中ツール**: 同一トークンを `ExecuteAsync` に伝える。中断するか安全に完了させるかは各ハンドラの判断に委ねる（強制スレッド Abort は行わない）。
3. **履歴と tool_result（確定方針）**:
   - すでにアシスタント側へ `tool_use` を履歴へコミット済みの場合、未完了・キャンセルされた各呼び出しについて `status: Cancelled` の `ToolResult` を履歴へ書き込む（プロバイダが tool_use / tool_result の対を要求するため）。
   - そのターンでは **追加の LLM 往復は行わない**（キャンセル結果を LLM に送って自己修正させない）。
   - ストリーミング途中で `tool_use` が未確定のままキャンセルされた場合、不完全なアシスタントメッセージは履歴に残さない。
4. **タイムアウト（P1・推奨）**: オプションでツール単位の待ち上限を設け、超過時はキャンセル要求を発火したうえで待ち、なお完了しない場合はターンを `TimedOut` として失敗扱いにする。強制 Abort はしない。

### 4.3 ツール実行と並行制御（`ToolDispatcher`）

LLM から 1 ターン内に複数のツール呼び出し（並列 tool_use）が返された場合、以下のルールに従ってディスパッチします。

1. **`RequiresUiThread` に応じた分岐（既定: フェーズ分離）**:
   - **Phase 1**: `RequiresUiThread == false` のツールを `Task.WhenAll` でバックグラウンド並列実行し、**完了を待つ**。
   - **Phase 2**: `RequiresUiThread == true` のツールを UI スレッド（STA）上で 1 つずつ順次実行する。
   - UI ツールと Non-UI ツールを同時に走らせないことで、同一ドキュメント／データモデルへの競合をライブラリ既定で抑止する。
   - スループット重視で UI/Non-UI 同時実行が必要な場合は、オプションでオプトイン可能とする。その場合の契約は「Non-UI ハンドラは UI ハンドラと並行しても安全であること（共有可変状態への独自同期）」を利用側が保証する。
2. **失敗の独立性（巻き添えキャンセルの抑止）**:
   - あるツールの実行が失敗しても、並行して走っている他のツール呼び出しは中断しません。全て完了させた上で、失敗したものだけ `is_error: true` の `ToolResult` を LLM へ返却します。
3. **未捕捉例外の変換（P0）**:
   - `ExecuteOneAsync` はハンドラ実行を必ず try-catch で保護し、未捕捉例外を `ToolResult.Failed(code: "UNHANDLED_EXCEPTION", ...)` に変換する。例外を `Task.WhenAll` へ漏らしてディスパッチ全体を Faulted にしない。
4. **結果の順序整合**:
   - すべての実行完了後、LLM から渡された元のツール呼び出し順に整列し直して会話履歴へ反映します。

```csharp
public class ToolDispatcher
{
    public async Task<IReadOnlyList<ToolResult>> ExecuteAllAsync(
        IReadOnlyList<ToolUsePart> calls, CancellationToken ct)
    {
        var uiCalls = calls.Where(c => GetHandler(c).RequiresUiThread).ToList();
        var nonUiCalls = calls.Where(c => !GetHandler(c).RequiresUiThread).ToList();

        // Phase 1: Non-UI を並列実行して完了待ち
        var nonUiResults = await Task.WhenAll(
            nonUiCalls.Select(c => ExecuteOneAsync(c, ct)));

        // Phase 2: UI を順次実行
        var uiResults = new List<ToolResult>();
        foreach (var c in uiCalls)
            uiResults.Add(await ExecuteOneAsync(c, ct));

        return MergeInOriginalOrder(calls, uiResults, nonUiResults);
    }

    // ExecuteOneAsync: 未捕捉例外は必ず ToolResult に変換する
}
```

### 4.4 エラーハンドリングとリトライ

- **アプリ状態の変化への対応**: ツール実行中に対象オブジェクトがユーザー操作等で変更・削除されていた場合、AgentBridge はドキュメント全体をロックするのではなく、ハンドラ側で `ELEMENT_NOT_FOUND` 等のエラーコードを返却する方針をとります。LLM にエラー結果をそのまま戻すことで、候補の再検索など自己修正を促します。
- **リトライ制御（LLM self-correction）**:
  - `IRetryPolicy`（既定: 同一ターン・同一ツール 3 回失敗で打ち切り）を `ConversationLoop` が適用する。
  - `ShouldRetry == false` となったツールについては、次の LLM 往復で次のいずれかを行う（既定はメタ指示）:
    1. **メタ指示（既定）**: 当該 `tool_result` に「このツールは試行上限に達した。他の手段を試すか、ユーザーに状況を説明すること」旨を付与する。
    2. **一時除外（オプション）**: 当該ターンの残りの往復で `tools` 一覧からそのツールを外す。
  - 打ち切り時に例外でループを落とすことはしない（会話を途中で破壊しない）。
- **MaxHops との関係**: ツール別リトライ上限に加え、ターン全体の往復上限（§4.2）が最終安全弁となる。
- **LLM 向け / UI 向けメッセージの分離（P1）**:
  - `ToolResult` は LLM に返す短文（パスやスタックトレースを含めない）と、UI／ログ向け詳細を分離できる形とする。
  - 絶対パス、内部例外スタック、秘密情報を LLM 向けメッセージに載せない。

### 4.5 WPF 実装の技術的考慮（`DispatcherMarshaller`）

WPF の `Dispatcher` と連携する際は、以下の落とし穴を回避する実装としています。

- **例外の確実な伝播**: `DispatcherOperation` をそのまま `await` した場合に例外が正しく呼び出し元へ伝わらない現象を防ぐため、`TaskCompletionSource<T>` でラップして例外を補足・再スローします。
- **`Func<Task<T>>` のアンラップ**: `Dispatcher.InvokeAsync` に非同期ラムダを渡すと `DispatcherOperation<Task<T>>` になり得るため、TCS で内側タスクの結果／例外を橋渡しし、二重 Task を呼び出し元に露出しない（§3.2）。
- **同一スレッド呼び出しの最適化**: 既に UI スレッド上で実行されている場合は `_dispatcher.CheckAccess()` で検知し、マーシャリングをバイパスして同期実行します。

---

## 5. バージョニングと拡張方針

- **インターフェースの拡張**: `IToolHandler` などの公開拡張ポイントへメンバーを追加する場合は、C# 8 の **デフォルトインターフェースメソッド（DIM）** を使用します。これにより、ライブラリのマイナーバージョンアップで既存ハンドラ実装のコンパイルエラーを防ぎます。
- **パッケージリリース方針**: `AgentBridge.Core`、`.Anthropic`、`.OpenAI`、`.Wpf` は常に同一のバージョン番号でリリースするロックステップ方式を採用します。
- **動的ロード機能（将来課題）**: 現状は `ToolRegistry` に登録された全ツール定義を毎回 LLM に提示します。将来的にツール数が 100 件規模に肥大化した場合に限り、`Category` 情報を利用したツールの動的検索・ロード（`search_tools` 等）の拡張を検討します。

### 5.1 拡張ポイント / 将来課題（Core MVP の必須ではない）

レビューで挙がったが、軽量ライブラリの責務境界から **Core 必須にはしない** 項目。

| 項目                               | 扱い               | 方針                                                                                                                                                                       |
| :--------------------------------- | :----------------- | :------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| ツール実行承認（破壊的操作の確認） | 拡張ポイント（P1） | Core に必須ポリシーは置かない。利用側またはオプションの `IToolApprovalHandler` で Confirm / Deny を実装可能にする。`RequiresUiThread` はスレッド制約であり実行許可ではない |
| 読み取り専用 / 副作用の分類        | ガイドライン       | 必要なら利用側で `ToolExecutionPolicy` 相当をハンドラに付与。Core の `IToolHandler` 最小面は維持                                                                           |
| Undo / トランザクション            | 文書のみ           | `IUndoableToolHandler` 等の Core API 化はしない。キャンセルや部分失敗時の整合はハンドラ実装ガイドで示す                                                                    |
| Context pruning（履歴切り詰め）    | 将来課題（P2）     | 長期記憶はスコープ外。長大 `tool_result` の要約・切り詰めフックは MVP 後に `ConversationState` へ検討                                                                      |
| `ToolHandlerBase<TInput>`          | DX（P2）           | ジェネリック基底で `JsonElement` → `TInput` デシリアライズを提供するのは有用だが、Core MVP のブロッカーではない                                                            |
| Reasoning 系イベント               | 段階導入           | `ReasoningDelta` 等はプロバイダ対応が必要になった時点で追加                                                                                                                |

---

## 6. 会話ループ概要（契約まとめ）

```
UserMessage
  → AcquireTurnLock（同一 State で同時ターン不可）
  → ILlmProvider.SendAsync
  → ToolCalls?
       yes → ToolDispatcher（Phase1 Non-UI → Phase2 UI）
            → ToolResults を履歴へ反映
            → MaxHops / IRetryPolicy 判定
                 continue → 再度 SendAsync（self-correction）
                 stop     → メタ指示付与またはターン打ち切り
       no  → TurnComplete
  → Cancellation 時は §4.2 のキャンセル契約に従う
```
