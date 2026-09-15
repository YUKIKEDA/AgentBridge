# AgentBridge 設計仕様書レビュー

## 総合評価

設計の方向性は良いです。実装に進める価値のある設計ですが、現状は「API の骨格」が中心で、実用ライブラリとして必要な契約がまだ不足しています。

特に良いのは、LLM SDK・UI フレームワーク・アプリ固有のツールを分離し、会話ループとツール実行を独立させている点です。

一方、最も重要な改善点は次の3つです。

1. 会話履歴とターンの所有権・並行性を明確にする

2. ツールの実行安全性、タイムアウト、キャンセル、リトライを設計する

3. LLM プロバイダ間の差異を吸収できる抽象化にする

以下、設計書の内容に沿って、優先度の高い順にレビューします。

## 1. 良い点

### 1.1 コアと SDK・UI の分離は適切

設計書では、次の4パッケージに分離しています。

* `AgentBridge.Core`

* `AgentBridge.Anthropic`

* `AgentBridge.OpenAI`

* `AgentBridge.Wpf`

`ILlmProvider` で LLM 接続を抽象化し、`IUiThreadMarshaller` で UI スレッドを抽象化する方針は合理的です。

agentbridge-design.md

特に、アプリ固有の `IToolHandler` を利用側に置く方針は良いです。AgentBridge が業務ドメインや具体的なアプリのデータモデルに依存せずに済みます。

agentbridge-design.md

### 1.2 ツール実行と UI スレッドを分けている

`RequiresUiThread` に応じて、バックグラウンド実行と UI スレッド実行を分ける設計は、デスクトップアプリ向けとして実用的です。

また、複数のツール呼び出しについて、

* 非 UI ツールは並列実行

* UI ツールは順次実行

* 失敗したツールが他のツールを巻き添えにしない

* 結果を元の呼び出し順に戻す

というルールを明示している点も良いです。

agentbridge-design.md

### 1.3 会話ループをステートレスにする意図は良い

`ConversationState` を呼び出し元が保持し、`ConversationLoop` はステートレスにする方針は、複数タブや複数会話への対応を考えると自然です。

agentbridge-design.md

ただし、後述するように、ステートレスなループと会話履歴の同時更新を安全に行うことは別問題です。


# 2. 重大な改善点

## P0 — 2.1 `ConversationState` の所有権と排他が不十分

設計書では、

> 会話履歴は呼び出し元が保持し、ConversationLoop はステートレス

としています。また、`ConversationState` 内で `Interlocked` による再入排他を行うとしています。

agentbridge-design.md

ここには設計上の不整合が起きる可能性があります。

### 問題

例えば、同じ `ConversationState` に対して、次の2つが同時に発生した場合です。

```
Thread A:
  ConversationLoop.RunAsync(state, "検索して")

Thread B:
  ConversationLoop.RunAsync(state, "保存して")
```

`Interlocked` で再入を検出するだけなら、二重実行は防げます。

しかし、以下は別途保証が必要です。

* 会話履歴の読み取りと書き込みの一貫性

* LLM 応答を履歴に追加するタイミング

* ツール実行結果を履歴に追加する順序

* キャンセルされたターンの履歴をどう扱うか

* 例外発生時に履歴が中途半端にならないか

### 改善案

`ConversationState` を単なるデータ保持クラスではなく、会話単位の実行コンテキストとして設計することを推奨します。

C#

```
public sealed class ConversationState
{
    private readonly SemaphoreSlim _turnLock = new(1, 1);

    public IReadOnlyList<ChatMessage> Messages { get; }

    public Task RunTurnAsync(
        Func<CancellationToken, Task> action,
        CancellationToken ct)
    {
        // ターンの所有権を取得して実行
        // 履歴更新の責務も明確にする
    }
}
```

ただし、`SemaphoreSlim` を使うかどうかよりも重要なのは、次の契約です。

> 同一 ConversationState に対して、同時に実行できるターンは1つ。履歴の変更はそのターンの所有者だけが行う。

さらに、`ConversationState` を外部から直接変更可能な `List<ChatMessage>` として公開するのは避けたいです。

### 判定

P0：実装前に仕様を確定すべき。

## P0 — 2.2 ツールの安全性が不足している

AgentBridge は、LLM がツールを呼び出してデスクトップアプリを操作するためのライブラリです。

この場合、単なる関数呼び出しライブラリよりも重要なのが、LLM に何を実行させてよいかという制御です。

現在の `IToolHandler` は以下です。

C#

```
public interface IToolHandler
{
    string ToolName { get; }
    JsonSchema InputSchema { get; }
    bool RequiresUiThread { get; }

    Task<ToolResult> ExecuteAsync(
        JsonElement input,
        CancellationToken ct);
}
```

agentbridge-design.md

### 不足しているもの

#### 1. ツールの実行確認

例えば、LLM が以下を呼び出せるとします。

```
delete_file
save_document
execute_command
```

ユーザーの意図と異なる操作を実行した場合、取り返しがつかない可能性があります。

`RequiresUiThread` はスレッド制約であり、実行許可の制御ではありません。

#### 2. 読み取り専用と変更操作の区別

ツールに次のような分類があるとよいです。

C#

```
public enum ToolSideEffect
{
    ReadOnly,
    MutatesState,
    Destructive
}
```

あるいは、

C#

```
public enum ToolPermission
{
    Auto,
    RequireConfirmation,
    Denied
}
```

#### 3. ユーザー確認の仕組み

例えば、

C#

```
public interface IToolApprovalHandler
{
    Task<bool> ConfirmAsync(
        ToolCallContext context,
        CancellationToken ct);
}
```

ただし、Core が直接 WPF のダイアログを表示するのではなく、UI 側が実装する形がよいです。

### 改善案

C#

```
public interface IToolHandler
{
    string ToolName { get; }

    JsonSchema InputSchema { get; }

    ToolExecutionPolicy Policy { get; }

    Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolExecutionContext context,
        CancellationToken ct);
}
```

`ToolExecutionPolicy` の例：

C#

```
public sealed record ToolExecutionPolicy(
    bool RequiresUiThread,
    bool RequiresConfirmation,
    bool IsReadOnly,
    TimeSpan Timeout);
```

これはあくまで一例です。

重要なのは、ツールの実行ポリシーを、単なる `RequiresUiThread` から独立させることです。

### 判定

P0：LLM にアプリ操作をさせるライブラリとして、最重要の不足。

## P0 — 2.3 リトライ仕様が曖昧で、無限ループ防止が不十分

設計書では、同一ターン・同一ツールで3回失敗したら打ち切るとしています。

agentbridge-design.md

しかし、現在の `IRetryPolicy` は次の形です。

C#

```
public interface IRetryPolicy
{
    bool ShouldRetry(
        string toolName,
        int attemptCount,
        ToolResult lastResult);
}
```

agentbridge-design.md

### 問題

`ShouldRetry` が `bool` を返すだけでは、以下を制御できません。

* 何ミリ秒待って再試行するか

* エラーの種類によってリトライ可能か

* 最大試行回数

* リトライの累積時間

* LLM 自体の再試行とツール再試行の区別

また、現在の設計では、

```
LLM
 ↓
Tool A 失敗
 ↓
LLM にエラー返却
 ↓
LLM が Tool A を再度呼ぶ
 ↓
Tool A 失敗
 ↓
...
```

という「LLM が自発的に再試行する」ケースと、

```
ToolDispatcher が同じツールを内部で再実行
```

というケースが混在する可能性があります。

### 改善案

まず、以下を明確に分けるべきです。

|
種類

|

説明

|
| --- | --- |
|

Tool execution retry

|

同じツールを内部で再実行

|
|

LLM turn retry

|

LLM API の通信失敗などで再試行

|
|

LLM self-correction

|

ツールエラーを受けてLLMが別の引数で再呼び出し

|

そして、`IRetryPolicy` は例えば次のような結果を返す設計が考えられます。

C#

```
public sealed record RetryDecision(
    bool Retry,
    TimeSpan Delay,
    string? Reason);
```

さらに、`ToolResult` にエラーの分類を持たせます。

C#

```
public enum ToolErrorCode
{
    None,
    InvalidInput,
    NotFound,
    PermissionDenied,
    Timeout,
    Cancelled,
    ExternalFailure,
    InternalError
}
```

### 判定

P0：失敗時の動作を確定してから実装したい。

# 3. API 設計のレビュー

## 3.1 `IToolHandler` の `JsonSchema` は具体的な型を決めた方がよい

設計書では `JsonSchema InputSchema` としています。

agentbridge-design.md

ただし、どの JSON Schema 実装を使うかが決まっていません。

例えば、

* `JsonSchema.Net`

* `NJsonSchema`

* `Microsoft.OpenApi`

* 独自の JSON Schema 表現

など、候補によって API やライセンス、AOT 互換性が変わります。

### おすすめ

AgentBridge.Core が特定の JSON Schema ライブラリに依存する必要がなければ、コアでは `JsonElement` または JSON 文字列で保持する方が単純です。

C#

```
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonElement InputSchema);
```

あるいは、

C#

```
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonDocument InputSchema);
```

ただし、Schema 検証を Core が行うのであれば、検証ライブラリの選定は必要です。

### 判定

P1：依存ライブラリとライセンス、AOT の要件を確定する。

## 3.2 `ProviderEvent` はもう少し拡張性が必要

現在は以下のイベントです。

C#

```
public abstract record ProviderEvent;

public record TextDelta(string Text) : ProviderEvent;

public record ToolCallStarted(
    string ToolUseId,
    string ToolName) : ProviderEvent;

public record ToolCallRequested(
    ToolUsePart Call) : ProviderEvent;

public record TurnComplete(
    StopReason Reason) : ProviderEvent;
```

agentbridge-design.md

基本的な Tool Use ループには使えます。

しかし、実際の LLM API を抽象化する場合、以下のイベントが必要になる可能性があります。

C#

```
public record MessageStarted : ProviderEvent;

public record MessageCompleted(
    UsageInfo Usage) : ProviderEvent;

public record ProviderError(
    Exception Exception) : ProviderEvent;

public record ReasoningDelta(
    string Text) : ProviderEvent;

public record ToolCallFailed(
    string ToolUseId,
    string Error) : ProviderEvent;
```

特に重要なのは LLM のレスポンスが完了したことと、ストリーミングが切断されたことを区別することです。

`TurnComplete` だけでは、途中切断と正常終了をどのように区別するかが曖昧です。

### 改善案

C#

```
public abstract record ProviderEvent;

public record ResponseStarted : ProviderEvent;

public record TextDelta(string Text) : ProviderEvent;

public record ToolCallStarted(
    string ToolUseId,
    string ToolName) : ProviderEvent;

public record ToolCallRequested(
    ToolUsePart Call) : ProviderEvent;

public record ResponseCompleted(
    StopReason Reason,
    UsageInfo? Usage) : ProviderEvent;

public record ResponseFailed(
    Exception Error) : ProviderEvent;
```

`UsageInfo` は、入力トークン・出力トークンなどをプロバイダ非依存に表す型です。

### 判定

P1：ストリーミング UI とログ、コスト表示を考えるなら必要。

## 3.3 `ToolCallStarted` と `ToolCallRequested` の責務を明確にする

設計書では、

* `ToolCallStarted`: ツール名確定

* `ToolCallRequested`: 引数パース完了

としています。

agentbridge-design.md

これは良いですが、次の点を決めたいです。

### 問題

LLM のストリーミング中に、ツール名が確定した後で引数 JSON が不正になった場合、

```
ToolCallStarted
    ↓
JSON parse failure
```

となります。

この場合、`ToolCallRequested` は発火しません。

UI 側は「実行中...」の表示を消す必要があります。

### 改善案

C#

```
public record ToolCallParseFailed(
    string ToolUseId,
    string ToolName,
    string Error) : ProviderEvent;
```

または、ツール実行状態を独立したイベントで管理する方法もあります。

C#

```
public enum ToolCallStatus
{
    Started,
    ArgumentsReceived,
    Executing,
    Completed,
    Failed,
    Cancelled
}
```

### 判定

P1：ストリーミング UI の状態管理に必要。

# 4. `ToolDispatcher` の並行制御

## 4.1 現在の UI ツール直列化は合理的だが、実装に注意

設計書では、

C#

```
var nonUiTask = Task.WhenAll(
    nonUiCalls.Select(c => ExecuteOneAsync(c, ct)));

var uiResults = new List<ToolResult>();

foreach (var c in uiCalls)
    uiResults.Add(await ExecuteOneAsync(c, ct));

var nonUiResults = await nonUiTask;
```

という実装例を示しています。

agentbridge-design.md

このコードは、非 UI ツールを並列実行しながら UI ツールを順次実行するという意図を表現できています。

ただし、いくつか注意点があります。

### 注意点1：`Task.WhenAll` は失敗時に即座に全体を中断しない

`Task.WhenAll` は、いずれかのタスクが失敗しても、他のタスクの完了を待ちます。

設計書の「失敗の独立性」という方針とは整合しています。

しかし、`ExecuteOneAsync` 内で例外をそのまま投げる設計だと、`ToolResult` に変換されず、全体が例外終了する可能性があります。

`ExecuteOneAsync` はツールの実行例外を捕捉し、`ToolResult` に変換する責務を持つべきです。

### 注意点2：UI スレッド上の非同期処理

C#

```
await ExecuteOneAsync(c, ct);
```

が UI スレッド上で実行される場合、`ExecuteOneAsync` 内でどのように `IUiThreadMarshaller` を利用するかが重要です。

例えば、

C#

```
await _marshaller.InvokeAsync(
    () => handler.ExecuteAsync(input, ct),
    ct);
```

のような実装では、`Func<Task<T>>` と `Func<T>` の扱いが曖昧になることがあります。

`InvokeAsync<T>(Func<Task<T>> asyncAction)` を使う場合、UI スレッドへのマーシャリング後に非同期処理がどう実行されるかを明確にしてください。

### 注意点3：同一 UI スレッド上の実行順序

UI ツールが複数ある場合、

```
Tool A → Tool B → Tool C
```

の順序で実行する仕様は良いです。

しかし、LLM が同一ターン内で、

```
update_selection
update_selection
```

のような同じ対象を変更するツールを複数呼び出した場合、直列化だけで十分なのか、アプリ側にトランザクションや排他が必要なのかは別問題です。

### 判定

P1：ツール実行結果の例外変換と UI マーシャリング契約を確定すべき。

# 5. キャンセル設計

設計書では、`CancellationToken` を LLM ストリーミングとツール実行の両方に渡し、ツール側が中断するか安全に完了させるか判断するとしています。

agentbridge-design.md

この方針は理解できますが、キャンセルの責務が利用側に寄りすぎています。

## 5.1 ツールがキャンセルを無視した場合

例えば、ツールが以下のような処理をしているとします。

C#

```
public async Task<ToolResult> ExecuteAsync(
    JsonElement input,
    CancellationToken ct)
{
    await LongRunningOperationAsync();
    return ToolResult.Success();
}
```

この処理が `ct` を無視すると、ユーザーがキャンセルしても終わりません。

AgentBridge 側がどうするかを決める必要があります。

### 推奨

キャンセルは3種類に分けるとよいです。

|
種類

|

意味

|
| --- | --- |
|

User cancellation

|

ユーザーが停止ボタンを押した

|
|

Timeout

|

ツールが制限時間を超えた

|
|

Provider cancellation

|

LLM API の通信がキャンセルされた

|

また、`ToolResult` にはキャンセルの理由を含めるとよいです。

C#

```
public enum ToolExecutionStatus
{
    Success,
    Failed,
    Cancelled,
    TimedOut
}
```

## 5.2 キャンセルされたツールの扱い

重要なのは、ユーザーがキャンセルした後に、LLM へ `tool_result` を送るのかどうかです。

例えば、

```
LLM → Tool A
ユーザーがキャンセル
Tool A が停止
```

この場合、

* `tool_result` を送らない

* `is_error: true` の結果を送る

* 会話ターン自体をキャンセル扱いにして履歴へ残す

など、複数の方針があります。

この仕様がないと、プロバイダ実装ごとに挙動が異なる可能性があります。

### 判定

P0：キャンセル後の会話履歴とプロバイダ送信の扱いを確定したい。

# 6. リトライ・エラー処理

## 6.1 エラーコードは良いが、エラーの種類を増やすとよい

設計書では `ELEMENT_NOT_FOUND` のようなエラーコードをハンドラ側から返す方針です。

agentbridge-design.md

これは良いです。

LLM に対して、単なる例外メッセージよりも構造化されたエラーを返す方が、自己修正に向いています。

ただし、次のような分類があるとよいです。

C#

```
public enum ToolErrorCode
{
    None,

    // 入力
    InvalidInput,
    SchemaValidationFailed,

    // 対象
    ElementNotFound,
    FileNotFound,

    // 権限
    PermissionDenied,

    // 実行
    Timeout,
    Cancelled,
    ExternalFailure,
    InternalError
}
```

## 6.2 エラーに機密情報を含めない

LLM に返すエラーは、必ずしもユーザーに表示するエラーと同じではありません。

例えば、

```
C:\Users\username\Documents\secret-project\...
```

のような絶対パスや内部例外のスタックトレースを、そのまま LLM に渡すのは避けたいです。

`ToolResult` は、少なくとも以下を分ける設計がよいです。

C#

```
public sealed record ToolResult(
    bool IsSuccess,
    ToolErrorCode? ErrorCode,
    string? Message,
    object? Data);
```

実際には、`Message` の用途をさらに分けてもよいです。

C#

```
public sealed record ToolResult(
    bool IsSuccess,
    ToolErrorCode? ErrorCode,
    string? ModelMessage,
    string? UserMessage,
    object? Data);
```

### 判定

P1：LLM 向けエラーと UI 向けエラーを分離したい。

# 7. `IUiThreadMarshaller` のレビュー

設計書では、WPF の `Dispatcher` を抽象化し、例外伝播と同一スレッド最適化を考慮しています。

agentbridge-design.md

方向性は良いです。

ただし、`IUiThreadMarshaller` の以下のAPIは、少し重複しています。

C#

```
Task<T> InvokeAsync<T>(Func<T> action, CancellationToken ct = default);

Task<T> InvokeAsync<T>(
    Func<Task<T>> asyncAction,
    CancellationToken ct = default);

Task InvokeAsync(
    Action action,
    CancellationToken ct = default);
```

### 改善案

API の使い方を明確にするため、次のようにしてもよいです。

C#

```
public interface IUiThreadMarshaller
{
    bool IsOnUiThread { get; }

    Task<T> InvokeAsync<T>(
        Func<T> action,
        CancellationToken ct = default);

    Task InvokeAsync(
        Action action,
        CancellationToken ct = default);
}
```

`Func<Task<T>>` のオーバーロードは、必要になってから追加する方法もあります。

ただし、非同期処理を UI スレッド上で開始する必要がある場合は、`Func<Task<T>>` が便利です。

### 注意点

UI スレッドでの `async` 処理は、次の2つが異なります。

C#

```
// UI スレッドで処理を開始する
await dispatcher.InvokeAsync(
    async () => await DoSomethingAsync());
```

C#

```
// UI スレッドで同期処理を実行し、完了を待つ
await dispatcher.InvokeAsync(
    () => DoSomething());
```

WPF の `Dispatcher.InvokeAsync` では、`Task<Task<T>>` の扱いが絡む場合があります。

実装時には、

* `Func<T>`

* `Func<Task<T>>`

* `Action`

のそれぞれについて、例外・キャンセル・同期実行時の挙動をテストしてください。

### 判定

P1：API 自体は妥当。実装契約を明文化したい。

# 8. パッケージ構成・バージョニング

## 8.1 ロックステップ方式は初期段階では良い

設計書では、4パッケージを同一バージョンでリリースする方針です。

agentbridge-design.md

初期段階では、これは良いと思います。

```
AgentBridge.Core      0.1.0
AgentBridge.Anthropic 0.1.0
AgentBridge.OpenAI    0.1.0
AgentBridge.Wpf       0.1.0
```

Core の API 変更に伴い、他のパッケージも一緒に更新する方が、利用側は扱いやすいです。

### 注意点

将来的には、Core の変更がない場合に、プロバイダパッケージだけを更新したくなる可能性があります。

例えば、

```
AgentBridge.Core      1.2.0
AgentBridge.Anthropic 1.2.1
AgentBridge.OpenAI    1.2.0
AgentBridge.Wpf       1.2.0
```

という方式です。

ただし、初期段階ではロックステップで問題ありません。

## 8.2 `AgentBridge.Wpf` は本当に必要か

設計書では、WPF 向けアダプタ層にチャット UI・スレッドマーシャラを含めています。

agentbridge-design.md

ここは少し気になります。

### 理由

AgentBridge の主目的は、

> LLM の Tool Use を既存デスクトップアプリに組み込むための軽量な .NET 向けインプロセス統合ライブラリ

です。

agentbridge-design.md

この目的であれば、WPF のチャット UI は必須ではありません。

例えば、WPF アプリ側が独自のチャット UI を作る場合、

```
AgentBridge.Core
AgentBridge.Anthropic
AgentBridge.OpenAI
AgentBridge.Wpf
```

をすべて導入する必要はありません。

### 推奨構成

```
AgentBridge.Core
AgentBridge.Anthropic
AgentBridge.OpenAI
AgentBridge.Wpf
AgentBridge.Wpf.Controls   // 必要になったら
```

`AgentBridge.Wpf` は `DispatcherMarshaller` などの技術アダプタだけにして、チャット UI は別パッケージに分ける方が、責務が明確です。

### 判定

P1：チャット UI の責務を Core から離す。

# 9. 追加すべき仕様

現状の設計書は、主要なクラスと処理フローは書かれていますが、実装前に以下を追加すると完成度が上がります。

## 9.1 会話履歴の仕様

最低限、次を決めたいです。

* `ConversationState` の生成と破棄

* メッセージの追加・更新方法

* ツール呼び出しの履歴形式

* ツール結果の履歴形式

* ターンの途中で例外が起きた場合の履歴

* キャンセルされた場合の履歴

* 最大履歴長・トークン制限

* 複数ターンの同時実行可否

## 9.2 プロバイダ差異の仕様

Anthropic と OpenAI では、Tool Use の形式、ストリーミングイベント、エラー、使用量の表現が異なります。

`ILlmProvider` では、どこまで共通化し、どこから先をプロバイダ固有にするかを決める必要があります。

例えば、

C#

```
public sealed record ProviderCapabilities(
    bool SupportsParallelToolCalls,
    bool SupportsStreaming,
    bool SupportsReasoning,
    bool SupportsVision);
```

のような能力情報を持たせる方法があります。

## 9.3 ツール登録の仕様

`ToolRegistry` は、ツール一覧の管理を行うとしています。

agentbridge-design.md

しかし、次の仕様がありません。

* 同じ `ToolName` を登録した場合

* ツールを削除する場合

* ツールの上書き

* ツールの有効・無効

* ツール一覧のスナップショット

* ツール定義変更中に会話を開始した場合

おすすめは、LLM へ送るツール一覧をスナップショットとして取得することです。

C#

```
public interface IToolRegistry
{
    void Register(IToolHandler handler);

    bool Remove(string toolName);

    IReadOnlyList<ToolDefinition> GetSnapshot();
}
```

## 9.4 ログ・診断

デスクトップアプリに組み込む場合、LLM 通信・ツール実行・エラーのログが重要になります。

ただし、プロンプトやツール引数には機密情報が含まれる可能性があります。

したがって、

* 通信ログの有効・無効

* ツール引数のマスキング

* LLM のレスポンス全文ログ

* ツール実行時間

* リトライ回数

* エラー分類

* 相関 ID

を設計しておくとよいです。

# 10. テスト戦略

現状の設計書にはテスト仕様がほとんどありません。

AgentBridge は、LLM API に依存する部分と、純粋な会話ループ・ツール実行制御を分離できるため、テストしやすい構造です。

## 10.1 Core のユニットテスト

|
テスト対象

|

テスト内容

|
| --- | --- |
|

ConversationLoop

|

Tool Use → 実行 → tool_result → 最終応答

|
|

ConversationLoop

|

複数ツール呼び出し

|
|

ToolDispatcher

|

非 UI ツールの並列実行

|
|

ToolDispatcher

|

UI ツールの順次実行

|
|

ToolDispatcher

|

1つのツール失敗時の独立性

|
|

ToolDispatcher

|

結果の順序整合

|
|

RetryPolicy

|

3回失敗で打ち切り

|
|

ToolRegistry

|

重複登録・削除

|
|

IUiThreadMarshaller

|

UI スレッド上の実行

|
|

ConversationState

|

同時ターンの排他

|

## 10.2 Fake LLM Provider

`ILlmProvider` をモックにすれば、LLM API に接続せず会話ループを検証できます。

C#

```
public sealed class FakeLlmProvider : ILlmProvider
{
    public IAsyncEnumerable<ProviderEvent> SendAsync(
        ConversationState state,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct)
    {
        // 事前に用意したイベントを返す
    }
}
```

テストシナリオ例：

```
1. ユーザーが「検索して」と入力
2. FakeProvider が ToolCallRequested を返す
3. ToolDispatcher がハンドラを実行
4. ToolResult を履歴へ追加
5. FakeProvider が最終テキストを返す
6. 正しい履歴が完成する
```

これは、実装初期から作る価値があります。

# 11. 実装前に決めるべき優先順位

P0

会話状態とターン排他

履歴の所有権、同時ターン、キャンセル後の扱い。

P0

ツール実行の安全性

確認要求、読み取り専用、破壊的操作、タイムアウト。

P0

リトライ・エラー契約

内部再試行とLLM自己修正の区別、エラー分類。

P1

プロバイダ抽象化の確定

Anthropic / OpenAI の差異をどこまで共通化するか。

P1

ツール登録・イベント・ログ

重複登録、ストリーム終了、診断情報。

P2

動的ツールロード・拡張

100件規模のツール検索などは、基本ループ完成後でよい。

# 12. 私ならこう設計を修正する

現状の設計思想を維持したまま、最小限の変更で改善するなら、次の構成にします。

```
AgentBridge.Core
│
├── Abstractions/
│   ├── ILlmProvider.cs
│   ├── IToolHandler.cs
│   ├── IToolRegistry.cs
│   ├── IRetryPolicy.cs
│   └── IUiThreadMarshaller.cs
│
├── Conversation/
│   ├── ConversationState.cs
│   ├── ConversationLoop.cs
│   ├── ConversationTurn.cs
│   └── ConversationOptions.cs
│
├── Providers/
│   ├── ProviderEvent.cs
│   ├── ToolCall.cs
│   └── UsageInfo.cs
│
├── Tools/
│   ├── ToolDefinition.cs
│   ├── ToolRegistry.cs
│   ├── ToolDispatcher.cs
│   ├── ToolResult.cs
│   └── ToolExecutionPolicy.cs
│
└── Errors/
    ├── ToolErrorCode.cs
    └── ProviderException.cs
```

そして、会話ループは以下の責務に限定します。

```
ConversationLoop
    │
    ├── LLM へリクエスト
    │
    ├── ストリーミングイベント処理
    │
    ├── ToolCall の収集
    │
    ├── ToolDispatcher へ実行依頼
    │
    ├── ToolResult を履歴へ反映
    │
    └── 最終応答を返す
```

ツールの実行許可や UI の確認ダイアログは、`ToolDispatcher` と利用側アプリの間で扱います。

# 最終評価

|
評価項目

|

評価

|
| --- | --- |
|

目的・スコープ

|

良い

|
|

パッケージ分離

|

良い

|
|

LLM プロバイダ抽象化

|

良いが拡張が必要

|
|

ツールディスパッチ

|

良い

|
|

UI スレッド調停

|

良い

|
|

会話履歴の設計

|

要改善

|
|

キャンセル・タイムアウト

|

要改善

|
|

リトライ設計

|

要改善

|
|

ツール実行の安全性

|

要改善

|
|

テスト仕様

|

不足

|
|

将来拡張性

|

良い

|

結論：設計の土台は十分に良いです。

特に、デスクトップアプリに LLM の Tool Use を組み込むための軽量ライブラリとして、`Core` と SDK・UI の分離、`ConversationLoop`、`ToolDispatcher` という構造は妥当です。

ただし、現状のまま実装を始めると、後から大きな修正が発生しやすいのは `ConversationState`、キャンセル、リトライ、安全性です。

この3つを先に仕様化すれば、実用的な AgentBridge の MVP に進めると思います。

次のステップとしては、`ConversationLoop` の状態遷移図と、`ToolDispatcher` の実行契約を固めるのが最も効果的です。
