# AgentBridge 設計仕様書レビュー

## 総合評価

# B+ 相当

実装前に要設計修正

対象：AgentBridge 設計仕様書 v1相当（365行）

結論： かなり良い設計です。単なる LLM API ラッパーではなく、会話ループ、ツール実行、UI スレッド、履歴の整合性、リトライ制御まで分離して考えられています。

ただし、現状は「設計の方向性が良い」段階であり、実装に入る前に 会話履歴の状態遷移・キャンセル・並行実行・プロバイダ契約 をさらに具体化する必要があります。

今回のレビューでは、提示された仕様書の内容を基準に評価します。コード実装や実際の SDK の動作確認は行っていません。

## 1. 良い点

### 1.1 Core とプロバイダ・UI の分離

`AgentBridge.Core` を中心に、Anthropic / OpenAI / WPF を別パッケージに分離する方針は適切です。

特に以下は良い設計です。

* `ILlmProvider` によるプロバイダ抽象化

* `ProviderEvent` によるストリーミングイベントの統一

* `IToolHandler` によるアプリ側ツールの拡張

* `IUiThreadMarshaller` による UI フレームワーク依存の隔離

* `ToolResult` による LLM 向け情報と診断情報の分離

既存のデスクトップアプリに組み込むライブラリとして、責務の境界はかなり良いです。

### 1.2 AssistantTurnBuilder を導入した点

これは特に評価できます。

> `TurnComplete` を受けたときだけ `AssistantTurn` を確定し、履歴へコミットする。

ストリーミング中の `TextDelta` やツール引数断片を、そのまま会話履歴へ書き込まない設計は重要です。

`ResponseFailed` やキャンセル時に不完全なアシスタントメッセージを残さない方針も、履歴破損を避けるうえで合理的です。

### 1.3 TurnLease によるターン排他

`ConversationState` が履歴とターンロックを所有し、`ConversationLoop` をステートレスにする方針は良いです。

```
ConversationState
  ├── Messages
  └── TurnLease

ConversationLoop
  └── State を受け取って 1 ターンを実行
```

複数タブで State を分離し、Loop を共有できるという設計も自然です。

ただし、後述するように、履歴の可視性と同時ターンの制御 はもう少し厳密に定義したいです。

### 1.4 ツールの UI / Non-UI 分離

`RequiresUiThread` に応じて実行を分けること、Non-UI ツールに並列度制限を設けること、結果を元の呼び出し順に戻すことは良いです。

また、

> Non-UI 同士の並列安全性はハンドラ責務

と明記している点も、Core が過剰なスレッド安全性を保証しないため適切です。

### 1.5 リトライ上限ガードを実行側に置いた点

これは P0 として妥当です。

LLM に「もう呼ばないでください」と指示するだけでは安全弁になりません。仕様書では、上限到達後の同名ツール呼び出しをハンドラ実行前に遮断しています。

```
LLM が同名ツールを再呼び出し
        ↓
ConversationLoop / Dispatcher のガード
        ↓
RETRY_LIMIT_EXCEEDED
        ↓
ハンドラは実行しない
```

このような防御的な設計は良いです。

# 2. 重要な指摘（P0）

## P0-1. ConversationState の履歴コミットが原子性を欠いている

最重要の指摘です。

現在の仕様では、ターンの流れが次のようになっています。

```
SendAsync
  ↓
AssistantTurnBuilder
  ↓
AppendAssistantMessage
  ↓
ToolDispatcher.ExecuteAllAsync
  ↓
AppendToolResults
  ↓
次の SendAsync
```

問題は、`AppendAssistantMessage` と `AppendToolResults` が別々の操作になっていることです。

### 問題が起きる例

LLM が2つのツールを返したとします。

```
Assistant:
  tool_use: search
  tool_use: update_view
```

その後、

```
search       → 成功
update_view  → 実行中に例外
```

となった場合、履歴はどのタイミングで何を含むべきでしょうか。

さらに、次のようなケースもあります。

* `AppendAssistantMessage` 後にプロセスが終了する

* `AppendToolResults` の前にキャンセルされる

* ToolDispatcher が予期せず例外を投げる

* 履歴の追加中に別の問題が発生する

仕様書では「履歴書き込みは lease 経由」と定義されていますが、複数の履歴変更をひとまとまりとして扱う契約がまだ明確ではありません。

### 改善案

ターン内の履歴コミット単位を明示してください。

C#

```
public interface ConversationTurnLease : IAsyncDisposable
{
    void AppendUserMessage(ChatMessage message);

    void CommitAssistantAndToolResults(
        AssistantTurn assistant,
        IReadOnlyList<ToolResult> results);
}
```

あるいは、現在の API を維持するなら、次を仕様化します。

1. `AppendAssistantMessage` は `TurnComplete` 後にのみ呼び出す。

2. ToolDispatcher の結果は全件確定してから `AppendToolResults` を呼ぶ。

3. `AppendAssistantMessage` 後にツール実行が失敗しても、アシスタントの `tool_use` は履歴に残す。

4. 途中キャンセル時の履歴状態をテストで固定する。

「履歴の整合性」と「実行結果の整合性」は別物 なので、ここを分けて設計すると強くなります。

## P0-2. キャンセル時の履歴と UI の整合性がまだ不十分

§4.2 ではキャンセル時に、

> すでにアシスタント側へ tool_use を履歴へコミット済みの場合、未完了・キャンセルされた各呼び出しについて Cancelled の ToolResult を履歴へ書き込む。

としています。

この方針自体は良いのですが、次のケースを具体化する必要があります。

### ケース：複数ツールの実行中にキャンセル

```
AssistantTurn
  ├── tool_use A
  ├── tool_use B
  └── tool_use C

A → 成功
B → 実行中
C → 実行待ち
```

ここでキャンセルされた場合、

```
A → Success
B → Cancelled / または実際の完了結果
C → Cancelled
```

とするのか。

仕様書では「未完了・キャンセルされた各呼び出し」としていますが、実行中のツールがキャンセル要求後に完了した場合の扱い を決めておきたいです。

### 推奨

ツールごとに状態を管理します。

C#

```
public enum ToolCallState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    CancelRequested,
    Cancelled
}
```

ただし、これは実装上の必須 enum というより、状態遷移を設計書で定義することが目的です。

重要なのは、

* キャンセル要求を出した

* 実際にツールがキャンセルされた

* ツールがキャンセル要求後に成功した

を区別することです。

`CancellationToken` が発火したからといって、実行中のツールが必ずキャンセルされたとは限りません。

## P0-3. MaxHops の上限判定に曖昧さがある

仕様書では、

> `SendAsync` を呼び出せる最大回数。既定値10。

と定義しています。

一方、フローには、

```
loop while llmCallCount <= MaxHops
```

とあります。

この表現だと、初期値が0なのか1なのかによって、11回呼び出せる可能性があります。

### 推奨する明確な契約

C#

```
if (llmCallCount >= options.MaxLlmCalls)
{
    // これ以上 SendAsync を呼ばない
}
```

呼び出し直前にカウントを増やす方式でも構いませんが、次を仕様書に明記してください。

* `MaxLlmCalls = 0` は許可するか

* `MaxLlmCalls = 1` なら初回送信だけ可能か

* 上限到達時の `TurnResult` は何を返すか

* ユーザー入力は履歴に残るか

* 最後のアシスタント応答は履歴に残るか

なお、名称は `MaxHops` より `MaxLlmCalls` の方が意味が明確 です。現在の定義なら、実装名も `MaxLlmCalls` を推奨します。

## P0-4. ProviderEvent の終了契約を強化したい

現在は以下のイベントがあります。

C#

```
TextDelta
ReasoningDelta
ToolCallStarted
ToolCallRequested
ToolCallParseFailed
ResponseFailed
TurnComplete
```

基本構成は良いのですが、`IAsyncEnumerable<ProviderEvent>` の契約として、次が未定義です。

### 未定義の重要事項

#### 1. `TurnComplete` が複数回発火した場合

```
TextDelta
TurnComplete
TurnComplete
```

これはプロバイダ実装のバグとして扱うのか、Core が無視するのか。

#### 2. `ResponseFailed` 後にイベントが来た場合

```
TextDelta
ResponseFailed
TextDelta
```

これは履歴に影響するため、明確にしたいです。

#### 3. `ToolCallStarted` と `ToolCallRequested` の対応

```
ToolCallStarted(id=A)
ToolCallRequested(id=A)
```

が必ず成立するのか。

また、`ToolCallRequested` だけが来るプロバイダも許可するのか。

#### 4. `ToolCallParseFailed` の履歴

引数 JSON が壊れていた場合、

```
ToolCallParseFailed
TurnComplete
```

となるのか。完全な `tool_use` として履歴に残すのか。

### 推奨

イベントストリームのプロトコル契約を追加してください。

```
1. 1回の SendAsync は、正常系では必ず1回の TurnComplete で終了する
2. ResponseFailed は終端イベント
3. キャンセル時は TurnComplete を発火するか、キャンセル終了として扱う
4. ToolCallRequested の ToolUseId は1回の応答内で一意
5. ToolCallRequested は引数が完全にパース済みであること
```

この部分は P0 です。

# 3. 重要な指摘（P1）

## P1-1. ToolDispatcher のフェーズ分離は安全だが、並列性を制限しすぎる可能性がある

現在の仕様：

```
Phase 1: Non-UI ツールを並列実行
    ↓ 全完了待ち
Phase 2: UI ツールを順次実行
```

これは安全性を優先する MVP として理解できます。

ただし、LLM が次のようなツールを返した場合、

```
search_files        → Non-UI
calculate_statistics → Non-UI
update_highlight    → UI
```

本来は、

```
search_files ───────┐
                     ├── 完了
calculate_statistics ┘
                     ↓
              update_highlight
```

で問題ありません。

一方、UI ツールと Non-UI ツールが独立しているなら、並列化の余地があります。

仕様書では「UI/Non-UI 同時実行はオプトイン」としていますが、オプトインの方法が未定義です。

### 推奨

MVP では現行方式で良いですが、次を明記してください。

* フェーズ分離は既定の安全な実行方式

* 同時実行は将来の拡張ポイント

* オプトイン時は共有状態の整合性を利用側が保証

* UI ツール同士の順序は必ず維持する

* Non-UI ツールの結果順序は元の呼び出し順に戻す

なお、`Environment.ProcessorCount` をそのまま既定値にするのは、ツールが I/O 待ち中心の場合には過剰でも不足でもあり得ます。設定可能にしている点は良いので、実際のアプリで調整できるようにしてください。

## P1-2. `RequiresUiThread` だけではツールの実行制約を表現しきれない

現在は、

C#

```
bool RequiresUiThread { get; }
```

だけで実行先を決めています。

しかし、実際のデスクトップアプリでは次のような制約があります。

|
ツール

|

必要な制約

|
| --- | --- |
|

ファイル検索

|

バックグラウンド可能

|
|

UI 更新

|

UI スレッド

|
|

ドキュメント編集

|

特定ドキュメントの排他

|
|

SQLite 書き込み

|

DB の書き込み制御

|
|

外部プロセス起動

|

同時起動数制限

|
|

GPU リソース操作

|

特定スレッド・コンテキスト

|

`RequiresUiThread` は「UI スレッドが必要か」を表すだけです。

### 改善案

MVP では `RequiresUiThread` のままでよいですが、将来的に以下を追加できる余地を残すと良いです。

C#

```
public enum ToolExecutionAffinity
{
    AnyThread,
    UiThread,
    Serialized
}
```

ただし、`Serialized` だけでは何に対して直列化するのかが不明なので、実際にはリソースキーなどが必要になります。

これは P1 の将来課題です。MVP で無理に複雑化する必要はありません。

## P1-3. IToolHandler の `JsonElement` は実用的だが、入力検証の責務を明確にしたい

C#

```
Task<ToolResult> ExecuteAsync(
    JsonElement input,
    CancellationToken ct);
```

という設計は、Core が特定の JSON Schema ライブラリに依存しないため良いです。

ただし、次の問題があります。

* `JsonElement` の型が期待と異なる

* 必須プロパティが欠落

* 未知のプロパティがある

* JSON Schema は正しいが、実際の値が不正

* 文字列の長さ・配列数などの制限がない

仕様書では、各プロバイダ側で非対応 JSON Schema を検知する方針ですが、これは LLM に送るスキーマの検証 です。

一方、ツール実行時の入力検証は別の問題です。

### 推奨

責務を分けて明記してください。

```
ToolDefinition.InputSchema
  → LLM に提示するスキーマ
  → プロバイダ側で互換性を検証

IToolHandler.ExecuteAsync(input)
  → 実行時の入力値検証
  → ハンドラ側の責務
```

そして、不正入力時の標準エラーコードを定義すると良いです。

```
INVALID_INPUT
MISSING_REQUIRED_PROPERTY
INVALID_PROPERTY_TYPE
INVALID_PROPERTY_VALUE
```

## P1-4. ToolResult の `Status` と `LlmContent` の契約が曖昧

C#

```
public sealed record ToolResult(
    string ToolUseId,
    ToolExecutionStatus Status,
    string LlmContent,
    string? DiagnosticDetails = null,
    string? ErrorCode = null);
```

基本的には良いです。

ただし、次の組み合わせが許されるのかを決めたいです。

```
Success + ErrorCodeあり
Failed + ErrorCodeなし
Cancelled + DiagnosticDetailsあり
TimedOut + LlmContentが空
```

特に `LlmContent` は必須になっているため、成功時・失敗時・キャンセル時の最低限の内容を定義すると実装が安定します。

### 推奨

```
Success:
  ErrorCode = null
  LlmContent = 成功内容

Failed:
  ErrorCode = 必須
  LlmContent = LLM向けのエラー説明

Cancelled:
  ErrorCode = CANCELLED
  LlmContent = キャンセルされた旨

TimedOut:
  ErrorCode = TOOL_TIMEOUT
  LlmContent = タイムアウトした旨
```

## P1-5. ツール名の一意性と登録ライフサイクルが未定義

`ToolRegistry.Register(IToolHandler)` が定義されていますが、以下が不明です。

* 同じ `ToolName` を登録したらどうなるか

* 大文字・小文字を区別するか

* 登録後に削除できるか

* 登録済みハンドラを置き換えられるか

* `ConversationLoop` 実行中に Registry を変更できるか

* 登録された `IToolHandler` が Dispose された場合の扱い

### 推奨

MVP で最低限、以下を決めてください。

```
ToolName は Registry 内で一意
重複登録は InvalidOperationException
ToolName の比較は Ordinal
実行中のターンではツール定義のスナップショットを使用
```

特に最後が重要です。

LLM への提示ツール一覧と、実際のツールディスパッチ対象が途中で変わると、ツール不一致が発生する可能性があります。

# 4. プロバイダ抽象化の改善点

## 4.1 ILlmProvider にリクエスト設定がない

現在のインターフェースは、

C#

```
IAsyncEnumerable<ProviderEvent> SendAsync(
    ConversationState state,
    IReadOnlyList<ToolDefinition> tools,
    CancellationToken ct);
```

です。

しかし実際の LLM API では、少なくとも以下のような設定が必要になります。

* Model

* Temperature

* Max output tokens

* System prompt

* Stop sequences

* API 固有のオプション

* Request timeout

* Provider 固有のヘッダ

これらを `ConversationState` に持たせるのか、`ILlmProvider` の設定に持たせるのか、明記したいです。

### 推奨

MVP では、次のような `LlmRequestOptions` を追加するのが自然です。

C#

```
public sealed record LlmRequestOptions(
    string Model,
    int? MaxOutputTokens = null,
    double? Temperature = null);
```

ただし、プロバイダ固有設定まで Core の共通型に詰め込むと、抽象化が崩れます。

そのため、

C#

```
LlmRequestOptions
ProviderOptions
```

を分離する設計が望ましいです。

## 4.2 ストリーミングと非ストリーミングの扱い

現在は `IAsyncEnumerable<ProviderEvent>` だけです。

これは良い抽象化ですが、プロバイダによってはストリーミングを使わない場合もあります。

その場合、

```
TextDelta 1回
ToolCallRequested
TurnComplete
```

のようなイベント列を返す方式で統一できます。

この点を仕様書に明記すると、アダプタの実装が楽になります。

## 4.3 ReasoningDelta の扱い

C#

```
public record ReasoningDelta(string Text) : ProviderEvent;
```

を先行追加しているのは理解できます。

ただし、推論系のイベントはプロバイダによって意味や公開可否が異なるため、次を定義しておきたいです。

* UI に表示するのか

* 履歴に保存するのか

* LLM への次回リクエストに含めるのか

* センシティブな内容として扱うのか

現状は「未使用プロバイダは発火しない」とありますが、将来的な扱いを決めておくと安全です。

# 5. UI スレッドマーシャラのレビュー

`IUiThreadMarshaller` はよく考えられています。

特に以下は良いです。

* `Func<T>` と `Func<Task<T>>` を分ける

* `TaskCompletionSource<T>` による例外伝播

* `CheckAccess()` による同一スレッド最適化

* Dispatcher 投入前のキャンセル

* 実行開始後は強制 Abort しない

### 改善したい点

#### `InvokeAsync(Func<Task<T>>)` のキャンセル挙動

仕様書では、

> 実行開始後は CT をハンドラ／アクションへ伝播するのみ

としています。

これは妥当ですが、UI スレッド上で実行される `asyncAction` が長時間 await する場合、Dispatcher のキューを占有するわけではないものの、呼び出し元は待ち続けます。

そのため、以下を明記すると良いです。

* `asyncAction` は UI スレッド上で実行される

* `await` 中は UI スレッドを占有しない

* `asyncAction` 内で同期ブロッキングしてはいけない

* 実行中のキャンセルは強制中断しない

また、`TaskCompletionSource<T>` の作成時には、通常 `RunContinuationsAsynchronously` を利用するかどうかも実装方針として決めておくと、継続処理によるスレッド上の予期しない実行を避けやすくなります。

# 6. エラー処理・リトライのレビュー

## 良い点

`ToolResult.LlmContent` と `DiagnosticDetails` の分離は、かなり良い設計です。

特に、

> 絶対パス・スタック・秘密情報を LLM 向けに載せない。

という方針は、デスクトップアプリに組み込むライブラリとして重要です。

## 改善点：リトライのカウント単位

現在は、

> 同一ターン内の同一ツール名ごとに累積する。引数が異なっていても同一ツールとしてカウント。

としています。

これは無限ループ防止には有効です。

ただし、例えば次のケースでは少し厳しいです。

```
search_files("*.stl") → Failed
search_files("*.obj") → Failed
search_files("*.png") → Failed
```

3回失敗した時点で同じツールを打ち切るため、LLM の自己修正余地が減ります。

一方で、引数をキーにすると、

```
search_files("a")
search_files("b")
search_files("c")
...
```

と無限に試せる可能性があります。

### 推奨

MVP は現在の「同一ツール名でカウント」で良いと思います。

ただし、将来拡張として、

```
PerToolName
PerToolNameAndArgumentHash
GlobalFailureBudget
```

のような戦略を検討しても良いです。

# 7. スコープと MVP のレビュー

## 良い点

スコープ外が明確です。

* MCP を後回し

* ローカル LLM ホスティングを対象外

* 長期記憶・プランニングを対象外

* Undo / 承認 / 自動要約を将来課題

これはライブラリの責務を絞るうえで正しいです。

## 注意点：承認機能は「将来」でも設計余地が必要

現在、

> 破壊的操作の必須承認ダイアログは将来課題

としています。

Core に必須の承認ダイアログを置かない方針は良いです。

ただし、将来追加する場合、ツール実行の直前に割り込める必要があります。

例えば、

```
LLM → ToolCall
      ↓
  ApprovalHandler
      ↓
  Confirm / Deny
      ↓
  ToolHandler.ExecuteAsync
```

となるため、現在の `ToolDispatcher` に承認フックを追加できる構造にしておくとよいです。

`RequiresUiThread` は実行許可ではない、と明記している点は正しいです。

# 8. 実装前に追加したい仕様

ここからは、現在の設計を実装可能なレベルへ引き上げるために、追加してほしい項目です。

## 8.1 ConversationTurnResult の定義

現在は `ConversationLoop` の完了結果が明示されていません。

例えば、

C#

```
public enum ConversationCompletionReason
{
    Completed,
    Cancelled,
    MaxLlmCallsExceeded,
    ProviderFailed,
    ToolFailed
}
```

のような結果型を用意すると、呼び出し側が状態を判定しやすくなります。

C#

```
public sealed record ConversationTurnResult(
    ConversationCompletionReason Reason,
    UsageInfo? Usage);
```

実際には、最後のアシスタントメッセージやエラー情報なども必要になる可能性があります。

## 8.2 ToolCall のライフサイクル

最低限、以下の状態遷移を設計書に追加したいです。

```
ToolCallRequested
       ↓
Pending
       ↓
Running
       ↓
Succeeded / Failed / Cancelled / TimedOut
```

さらに、

```
Pending → Cancelled
Running → CancelRequested → Cancelled
Running → CancelRequested → Succeeded
```

なども定義しておくと、キャンセルの扱いが明確になります。

## 8.3 ツール実行のタイムアウト

§4.2 では P1 として記載されています。

ただし、LLM を組み込んだデスクトップアプリで、ツールが永遠に戻らないケースは UI / UX に大きな影響があります。

MVP で必須にするかは別として、少なくとも次を決めたいです。

* デフォルトタイムアウト

* ツール単位の上書き

* タイムアウト時にキャンセルを要求

* タイムアウト後も完了を待つか

* ターンを終了するか

* `ToolResult` を履歴に残すか

# 9. テスト計画の評価

MVP 実装順序は良いです。

```
1. Core データモデル
2. Fake ILlmProvider
3. ToolDispatcher
4. Anthropic / OpenAI
5. WPF DispatcherMarshaller
```

特に Fake Provider から始めるのは正解です。

ただし、テスト項目をもう少し具体化したいです。

## 必須テスト

### ConversationLoop

* 通常のテキスト応答

* ツール1個 → 結果 → 最終応答

* 複数ツールの結果順序

* Non-UI ツールの並列実行

* UI ツールの順次実行

* ツール例外の変換

* `MaxLlmCalls = 1`

* `MaxLlmCalls` 超過

* 同一ツール3回失敗

* リトライ上限後の実行ガード

* LLM ストリーム中キャンセル

* ツール実行中キャンセル

* ツール実行後、次の SendAsync 前のキャンセル

* ProviderEvent の異常な順序

### ConversationState / TurnLease

* 同時ターン取得

* 取得前キャンセル

* 解放後の再取得

* lease DisposeAsync の二重呼び出し

* 履歴の直接書き換え不可

* ターン途中での例外

* ターン途中でのキャンセル

### DispatcherMarshaller

* UI スレッド上の呼び出し

* UI スレッド外の呼び出し

* `Func<T>` の例外

* `Func<Task<T>>` の例外

* `Func<Task<T>>` のアンラップ

* 投入前キャンセル

* 実行開始後キャンセル

* 同一スレッド最適化

# 10. 修正優先順位

P0

実装前に修正推奨

1. ConversationState の履歴コミットと原子性

2. キャンセル時のツール状態・履歴・UI 整合性

3. MaxLlmCalls の上限判定の明確化

4. ProviderEvent の終了・異常順序契約

P1

MVP 実装中に固める

1. ToolDispatcher の並行性と将来の実行制約

2. JsonElement 入力検証の責務

3. ToolResult の状態別契約

4. ToolRegistry の一意性とライフサイクル

5. ConversationTurnResult の定義

P2

後続の DX / 拡張

1. ToolHandlerBase<TInput>

2. JsonSchema 自動生成

3. 動的ツールロード

4. Context pruning

5. 承認・Undo・Reasoning 本格対応

## 最終評価

この設計書は、AgentBridge を実際にデスクトップアプリへ組み込むための良い土台 になっています。

特に評価できるのは、単に API を呼ぶだけではなく、

* 会話履歴の所有権

* ターン排他

* ストリームの完全応答構築

* UI スレッド調停

* ツール並列実行

* LLM self-correction の制限

* LLM 向け情報と診断情報の分離

まで考慮されている点です。

一方で、最も注意したいのは ConversationLoop が会話履歴をどう確定させるか です。

ここが曖昧なまま実装に入ると、後からキャンセル、例外、複数ツール、プロバイダ差異が絡み合って、設計変更が大きくなる可能性があります。

おすすめの次の作業は、§4.2 の ConversationLoop を中心に、正常系・複数ツール・キャンセル・MaxLlmCalls 超過の4つの状態遷移を仕様化することです。 そこまで固めれば、Core MVP の実装にかなり入りやすい状態になります。
