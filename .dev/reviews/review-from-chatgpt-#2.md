# AgentBridge 設計仕様書レビュー

## 総合評価

# 良い設計。ただし、MVP実装前に詰めるべきP0がある

実装前レビュー

AgentBridge は、デスクトップアプリにLLMのTool Useを組み込むための軽量な .NET ライブラリとして、責務分離と拡張性がよく整理されています。特に、LLMプロバイダ、ツール実行、UIスレッド調停、会話ループを分離している点は適切です。

agentbridge-design.md

設計の成熟度

## 高め

責務・境界・契約が明記されている

MVP実装可否

## 条件付きGo

会話履歴とキャンセル等の契約を補強

最重要の結論： 現状は「設計の方向性は良いが、実際にLLMとツールを接続したときのプロトコル整合性・状態管理・キャンセル・再実行安全性がまだ不十分」です。

特に、以下の3点は実装前に仕様を確定したいです。

1. `ConversationState` の履歴とターンロックの厳密な所有権。

2. プロバイダのストリーミングイベントから、正しいアシスタントメッセージと `tool_result` を構築する仕組み。

3. キャンセル・タイムアウト・LLM再試行時のツール副作用の安全性。

以下、設計書の各章に沿ってレビューします。


## 1. 良い点

### 1.1 責務分離が明確

設計書の4パッケージ構成は、かなり良いです。

|
パッケージ

|

評価

|
| --- | --- |
|

`AgentBridge.Core`

|

◎ UI・SDK非依存の中心

|
|

`AgentBridge.Anthropic`

|

◎ プロバイダ差異の隔離

|
|

`AgentBridge.OpenAI`

|

◎ 互換エンドポイント対応

|
|

`AgentBridge.Wpf`

|

○ UI依存部分を分離

|

`ConversationLoop`、`ToolRegistry`、`ToolDispatcher`、`IUiThreadMarshaller` を独立させることで、LLM通信とアプリケーションのUI・業務ロジックが混ざりにくい構造です。

agentbridge-design.md

また、MCPやローカルLLM、マルチエージェントをスコープ外にしているのも良い判断です。最初から何でもできるフレームワークにせず、デスクトップアプリへのTool Use統合に集中できています。

### 1.2 ツールの実行順序と並行性を明示している

`ToolDispatcher` の以下の方針は、意図が明確です。

* Non-UIツールは並列実行。

* UIツールはUIスレッドで順次実行。

* 失敗したツールがあっても他のツールは継続。

* LLMが指定したツール呼び出し順に結果を整列。

このあたりは、単純な「ツールを1個ずつ呼ぶ」実装よりも、実際のTool Useに対応した設計になっています。

agentbridge-design.md

### 1.3 キャンセルを真剣に扱っている

キャンセル時に、

> すでに `tool_use` を履歴へコミット済みなら、未完了呼び出しを `Cancelled` の結果として記録する。

という方針は、LLM APIのメッセージ整合性を意識した良い設計です。

また、強制スレッドAbortを行わず、ハンドラへCancellationTokenを渡すのも適切です。

agentbridge-design.md

# 2. 重大な指摘（P0）

## P0-1. `ConversationState` の所有権と排他が矛盾している

設計書では、以下を定めています。

* `ConversationState` は呼び出し元が保持する。

* `ConversationLoop` はステートレス。

* 同じ `ConversationState` に対して同時ターンは1つ。

* 履歴はLoop経由で変更する。

* 複数タブでLoopインスタンスを共有できる。

  agentbridge-design.md

これは方向性としては良いのですが、誰がターンロックを取得し、誰が履歴の書き込みを担当するのかが実装契約として足りません。

### 問題になるケース

C#

```
var loop = new ConversationLoop(...);
var state = new ConversationState();

var task1 = loop.RunAsync(state, "検索して");
var task2 = loop.RunAsync(state, "保存して");
```

同時実行時に、次のような問題が発生し得ます。

* 片方のターンが履歴を追加している最中に、もう片方が読み取る。

* `Messages` の読み取りと書き込みのタイミングが競合する。

* 例外・キャンセル時にロックが解除されない。

* 外部コードが履歴を変更した場合、Loopが保持する前提と食い違う。

### 改善案

`ConversationState` にターンの所有権を持たせる設計を推奨します。

C#

```
public sealed class ConversationState
{
    private readonly SemaphoreSlim _turnLock = new(1, 1);

    public IReadOnlyList<ChatMessage> Messages { get; }

    internal Task<ConversationTurnLease> AcquireTurnAsync(
        CancellationToken ct);
}
```

ただし、単純に `SemaphoreSlim` を追加するだけでは不十分です。

重要なのは、ターンロックの保持中だけ履歴の書き込みを許可すること。

例えば、

C#

```
await using var turn = await state.AcquireTurnAsync(ct);

turn.AppendUserMessage(userMessage);
turn.AppendAssistantMessage(assistantMessage);
turn.AppendToolResult(toolResult);
```

のように、ターン所有者以外が履歴を変更できない構造が望ましいです。

### 追加で確定したい仕様

|
項目

|

推奨

|
| --- | --- |
|

ターンロック

|

`ConversationState` が所有

|
|

ロック取得

|

`RunAsync` の最初

|
|

履歴書き込み

|

ターン所有者のみ

|
|

キャンセル待ち

|

ロック取得前にキャンセル可能

|
|

ロック解放

|

`finally` / `IAsyncDisposable`

|
|

外部からの履歴変更

|

原則禁止

|

## P0-2. `ProviderEvent` だけでは履歴を正しく再構築できない

これは今回の設計で、最も重要な技術的な指摘です。

現在のイベント定義は以下です。

C#

```
public record TextDelta(string Text) : ProviderEvent;

public record ToolCallStarted(
    string ToolUseId,
    string ToolName) : ProviderEvent;

public record ToolCallRequested(
    ToolUsePart Call) : ProviderEvent;

public record ToolCallParseFailed(
    string ToolUseId,
    string ToolName,
    string Error) : ProviderEvent;

public record ResponseFailed(
    Exception Error) : ProviderEvent;

public record TurnComplete(
    StopReason Reason,
    UsageInfo? Usage = null) : ProviderEvent;
```

agentbridge-design.md

このイベント群では、LLMから受信したアシスタントメッセージを正確に保存するための情報が不足しています。

### 具体的な問題

LLMが次のような応答を返すとします。

```
「検索します。」
tool_use: search_files
arguments: { "query": "test" }
```

イベントとしては、

```
TextDelta("検索します。")
ToolCallStarted("call_1", "search_files")
ToolCallRequested(...)
TurnComplete(...)
```

となります。

しかし、履歴には通常、

JSON

```
{
  "role": "assistant",
  "content": [
    {
      "type": "text",
      "text": "検索します。"
    },
    {
      "type": "tool_use",
      "id": "call_1",
      "name": "search_files",
      "input": {
        "query": "test"
      }
    }
  ]
}
```

のような、テキストとTool Useを含む1つのアシスタントメッセージが必要です。

`ToolCallStarted` と `ToolCallRequested` だけを使っていると、以下が曖昧になります。

* テキストとツール呼び出しの順序。

* 複数のツール呼び出しの順序。

* 同じツール名が複数回呼ばれた場合。

* 途中でストリームが切断された場合。

* 受信した生の引数JSON。

* SDK固有の追加フィールド。

### 改善案：プロバイダイベントと履歴構築を分離

`ProviderEvent` はUIや実行制御向けのイベントとして残しつつ、別途、LLM応答の完全な構造を表す内部モデルを持つべきです。

C#

```
public sealed record AssistantTurn(
    IReadOnlyList<ContentPart> Content,
    StopReason StopReason,
    UsageInfo? Usage);
```

C#

```
public interface ILlmProvider
{
    IAsyncEnumerable<ProviderEvent> SendAsync(
        ConversationState state,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct);
}
```

このままでも構いませんが、`ConversationLoop` がイベントを受信しながら `AssistantTurnBuilder` のような内部構築器で完全な応答を組み立てる設計を明記するとよいです。

さらに、プロバイダによっては、Tool Useの引数がJSON文字列の断片として到着するため、引数の受信状態を保持する必要があります。

C#

```
internal sealed class AssistantTurnBuilder
{
    // TextDelta を保持
    // ToolCallStarted を保持
    // 引数JSONの断片を保持
    // ToolCallRequested で完成
    // TurnComplete で最終的なAssistantTurnを生成
}
```

### 追加すべきP0仕様

* アシスタントメッセージの構築規則。

* テキストとTool Useの順序保持。

* `tool_use_id` の一意性。

* 複数Tool Useの並び順。

* 部分ストリームの履歴コミット規則。

* ストリーム失敗時に何を履歴へ残すか。

## P0-3. `ToolResult` のプロバイダ変換が未定義

設計書では、`ToolResult` をLLMへ返す方針がありますが、プロバイダ固有の形式への変換仕様がありません。

agentbridge-design.md

例えば、Anthropic系とOpenAI系では、ツール結果のメッセージ構造が異なります。

したがって、Coreにある汎用 `ToolResult` をそのまま各SDKに渡すことはできません。

### 改善案

Coreでは、次のようなプロバイダ非依存の結果を持たせます。

C#

```
public sealed record ToolResult(
    string ToolUseId,
    ToolExecutionStatus Status,
    string? LlmMessage,
    string? DetailedMessage,
    string? ErrorCode);
```

そして各プロバイダ実装側で変換します。

```
ToolResult
   │
   ├── AgentBridge.Anthropic
   │       └── Anthropic tool_result
   │
   └── AgentBridge.OpenAI
           └── OpenAI tool output
```

### ここで必要な仕様

|
項目

|

内容

|
| --- | --- |
|

ToolUseId

|

元の呼び出しIDを保持

|
|

成功結果

|

LLMに返すテキスト / JSON

|
|

失敗結果

|

`is_error` 相当の表現

|
|

キャンセル

|

プロバイダ別の表現

|
|

大きな結果

|

サイズ上限 / 切り詰め方針

|
|

変換責務

|

各プロバイダアダプタ

|

特に、`ToolResult` の成功結果を `string` のみで扱うのか、JSON構造を許可するのかは、早めに決めておきたいです。

# 3. 重要な指摘（P1）

## P1-1. `RequiresUiThread` は実行場所の指定としては不十分

設計書では、

C#

```
bool RequiresUiThread { get; }
```

によって、UIスレッドが必要かどうかを指定しています。

しかし、実際のデスクトップアプリでは、次のような操作もあります。

* UIスレッドで実行する必要がある操作。

* UIスレッドではないが、特定のドキュメントスレッドで実行する操作。

* バックグラウンドで実行するが、アプリのメインデータモデルにアクセスする操作。

* UI状態を読み取るだけの操作。

* UI更新を要求するが、実行自体はバックグラウンドで可能な操作。

`RequiresUiThread` だけでは、実行場所の制約を表現できない場合があります。

### MVPでの改善案

現時点では、`RequiresUiThread` を残してもよいです。ただし、以下の注意書きを設計書に追加したいです。

> `RequiresUiThread == false` は、任意のスレッドから安全に実行できることを意味する。共有状態へのアクセスやスレッド安全性は、各ハンドラが保証する。

また、将来拡張として、

C#

```
public enum ToolExecutionContext
{
    Background,
    UiThread,
    Serialized,
}
```

のような抽象化を検討できます。

ただし、MVPで無理に複雑化する必要はありません。

## P1-2. Non-UI並列実行の安全性が不足している

現在の仕様では、Non-UIツールは `Task.WhenAll` で並列実行します。

agentbridge-design.md

これは性能面では良いですが、次のようなツールが同時に呼ばれた場合は危険です。

```
Tool A: ドキュメントを更新
Tool B: 同じドキュメントを保存
Tool C: 同じドキュメントの状態を取得
```

すべてNon-UI扱いなら、並列実行によってデータ競合が発生する可能性があります。

### 改善案

ツールごとに実行グループを指定できる設計が有用です。

C#

```
public interface IToolHandler
{
    string ToolName { get; }

    bool RequiresUiThread { get; }

    string? ExecutionGroup { get; }

    Task<ToolResult> ExecuteAsync(
        JsonElement input,
        CancellationToken ct);
}
```

例えば、

```
ExecutionGroup = "Document:123"
```

のように、同じドキュメントに関係する操作は同一グループ内で順次実行する仕組みです。

ただし、これはMVPの必須要件ではありません。

最低限、並列実行が安全であることをハンドラ側が保証するという契約を明記すべきです。

## P1-3. ツール実行の再試行と副作用の扱い

設計書では、LLM self-correctionのみを既定とし、`ToolDispatcher` による即時再実行は行わない方針です。

agentbridge-design.md

これは非常に良いです。

ただし、LLMがエラー結果を受け取って、同じツールを再度呼ぶ場合があります。

例えば、

```
LLM → save_file
     → 成功
LLM → save_file
     → もう一度保存
```

となる可能性があります。

### 問題

ツールが副作用を持つ場合、

* ファイルが二重保存される。

* データが二重追加される。

* 同じ操作が二度実行される。

* ユーザーの意図しない変更が起きる。

### 改善案

`IToolHandler` に副作用の性質を明示する仕組みを将来的に追加することを推奨します。

C#

```
public enum ToolSideEffect
{
    ReadOnly,
    Mutating,
    Destructive,
}
```

ただし、設計書の §5.1 では、読み取り専用 / 副作用の分類はガイドラインとして扱い、Coreの最小APIには含めない方針です。

agentbridge-design.md

この方針自体は理解できますが、少なくともアプリケーション側の実装ガイドでは必須に近い扱いにした方がよいです。

特にデスクトップアプリに組み込むなら、破壊的操作の承認ポリシーは重要です。

## P1-4. リトライポリシーの設計が少し曖昧

設計書では、

> 同一ターン・同一ツール名で3回失敗したら `ShouldRetry == false`

としています。

agentbridge-design.md

しかし、ツール名だけで数えると、異なる呼び出しが同じツール名だった場合に混同します。

例えば、

```
search_files("test") → 失敗
search_files("hello") → 失敗
search_files("world") → 失敗
```

これらを同じ「同一ツールの3回失敗」とみなすのか、引数が違えば別試行とみなすのかが未定義です。

### 改善案

`ToolUseId` を基準にするか、少なくとも次のいずれかを明記すべきです。

* 同一 `ToolUseId` の再実行。

* 同一ツール名 + 同一ターン。

* 同一ツール名 + 引数ハッシュ。

* ツール全体の累積失敗回数。

MVPなら、次のような単純な定義が扱いやすいです。

> リトライカウントは、同一ターン内の同一ツール名ごとに累積する。引数が異なっていても同一ツールとしてカウントする。

ただし、同じツール名でも別々の目的で呼び出すことがあるため、これは仕様として明記したうえで採用するのがよいです。

## P1-5. MaxHopsの定義が曖昧

設計書では、MaxHopsの既定値を10とし、

> 1ターン内の「LLM往復」に上限を設ける。

としています。

agentbridge-design.md

しかし、次のどれを数えるのかが曖昧です。

```
UserMessage
  → LLM
  → Tool A
  → LLM
  → Tool B
  → LLM
  → Final
```

これを、

* LLM呼び出し回数 = 3

* Tool実行回数 = 2

* 往復回数 = 2

* Hops = 5

のどれとして数えるのか、明確にする必要があります。

### 改善案

MVPでは、以下の定義がわかりやすいです。

C#

```
int llmCallCount = 0;

while (true)
{
    if (++llmCallCount > options.MaxLlmCalls)
    {
        // 打ち切り
    }

    // LLM呼び出し
}
```

そして、設定名も `MaxHops` より `MaxLlmCalls` の方が、実装者にとって理解しやすい可能性があります。

`MaxHops` を残す場合は、次を明記すればよいです。

> MaxHopsは、当該ターン内で `ILlmProvider.SendAsync` を呼び出せる最大回数とする。

# 4. API設計のレビュー

## 4.1 `IToolHandler` はシンプルで良い

現在のAPIは、

C#

```
public interface IToolHandler
{
    string ToolName { get; }
    JsonElement InputSchema { get; }
    bool RequiresUiThread { get; }

    Task<ToolResult> ExecuteAsync(
        JsonElement input,
        CancellationToken ct);
}
```

となっています。

agentbridge-design.md

この最小構成は良いです。

特に、Coreが特定のJSON Schemaライブラリに依存せず、`JsonElement` を保持する設計は、依存関係を増やしにくいです。

### 気になる点：`InputSchema` と `ToolDefinition` の重複

`IToolHandler` にも `InputSchema` があり、`ToolDefinition` にも `InputSchema` があります。

このため、登録時に両者が不一致になる可能性があります。

C#

```
registry.Register(new ToolDefinition
{
    Name = "search_files",
    InputSchema = schemaA
});
```

一方でハンドラは、

C#

```
public JsonElement InputSchema => schemaB;
```

となると、どちらを正とするか不明です。

### 改善案

MVPなら、どちらかに統一した方がよいです。

案A：ハンドラが定義を持つ

C#

```
public interface IToolHandler
{
    ToolDefinition Definition { get; }

    Task<ToolResult> ExecuteAsync(
        JsonElement input,
        CancellationToken ct);
}
```

案B：Registryが定義を持つ

C#

```
public interface IToolHandler
{
    string ToolName { get; }

    Task<ToolResult> ExecuteAsync(
        JsonElement input,
        CancellationToken ct);
}
```

そして `ToolRegistry` が `ToolDefinition` を保持します。

個人的には、AgentBridgeのようなライブラリなら、案Aの方が登録ミスを減らしやすいと思います。

## 4.2 `IUiThreadMarshaller` は良いが、キャンセル契約を補強したい

`Func<T>`、`Func<Task<T>>`、`Action` の3種類を持つのは便利です。

agentbridge-design.md

ただし、次のケースは仕様に追加したいです。

### UIスレッドへの投入後にキャンセルされた場合

```
Background Thread
    ↓ InvokeAsync
UI Dispatcher Queue
    ↓
CancellationToken 発火
    ↓
UI側の処理はどうする？
```

キャンセルされたら、

* UIキューに入る前ならキャンセル。

* UIキューに入った後なら実行を取り消せない可能性がある。

* UI処理中なら強制停止しない。

* UI処理終了後に結果を返す。

という契約が必要です。

特に、UI操作の途中でキャンセルされた場合に、部分的なUI変更が残ることがあります。

これはAgentBridge側がUndoやトランザクションを持たない方針とも関係します。

# 5. プロバイダ抽象化のレビュー

## 5.1 `ILlmProvider` の責務は適切

C#

```
public interface ILlmProvider
{
    IAsyncEnumerable<ProviderEvent> SendAsync(
        ConversationState state,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct);
}
```

このAPIはシンプルです。

agentbridge-design.md

ただし、実装を始める前に、プロバイダが何を受け取るかをもう少し明確にしたいです。

例えば、

* 会話履歴は `ConversationState` からプロバイダが直接読むのか。

* `ConversationState` はプロバイダに公開する読み取り専用インターフェースなのか。

* システムプロンプトはどこで管理するのか。

* モデル名やtemperatureなどのリクエスト設定はどこで指定するのか。

* ツールの提示を毎回行うのか。

* ツールごとに有効・無効を切り替えられるのか。

### 改善案

将来的に次のようなリクエストモデルを導入すると、拡張しやすいです。

C#

```
public sealed record LlmRequest(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ToolDefinition> Tools,
    LlmRequestOptions Options);
```

C#

```
public interface ILlmProvider
{
    IAsyncEnumerable<ProviderEvent> SendAsync(
        LlmRequest request,
        CancellationToken ct);
}
```

ただし、`ConversationState` を渡す現行案でも、MVPでは問題ありません。

## 5.2 OpenAI互換エンドポイントの扱い

`AgentBridge.OpenAI` で、OpenAI APIだけでなく互換エンドポイントにも接続する方針は良いです。

agentbridge-design.md

ただし、「OpenAI互換」だからといって、すべてのTool Use仕様が完全互換とは限りません。

例えば、以下が異なる場合があります。

* Tool Callのストリーミング形式。

* JSON Schemaの対応範囲。

* `tool_choice` の対応。

* `parallel_tool_calls` の対応。

* エラー形式。

* Usageの返却形式。

したがって、`AgentBridge.OpenAI` の責務として、以下を明記したいです。

> OpenAI互換エンドポイントは、対象APIがAgentBridgeの要求するTool Use仕様を満たす場合に利用可能とする。非対応機能は実行時に明示的なエラーとして返す。

# 6. エラーハンドリングのレビュー

## 6.1 未捕捉例外の変換は適切

`ExecuteOneAsync` で未捕捉例外を `ToolResult.Failed` に変換する方針は良いです。

agentbridge-design.md

これにより、ツール1個の例外で `Task.WhenAll` 全体がFaultedすることを防げます。

ただし、次の点を決めたいです。

### 例外の分類

現在は、

```
UNHANDLED_EXCEPTION
```

というエラーコードが示されています。

これに加えて、少なくとも以下の分類があると便利です。

```
TOOL_NOT_FOUND
INVALID_ARGUMENTS
EXECUTION_FAILED
CANCELLED
TIMED_OUT
PERMISSION_DENIED
ELEMENT_NOT_FOUND
```

これらを `ToolResult` に持たせることで、LLMが自己修正しやすくなります。

### LLM向けエラーとUI向けエラーの分離

この方針は非常に良いです。

```
LLM向け:
  "指定されたファイルが見つかりません。"

UI / Log向け:
  "C:\Users\...\file.txt が存在しない"
  StackTrace...
```

絶対パスやスタックトレースをLLMに送らない方針は、情報漏洩の抑止にも役立ちます。

agentbridge-design.md

# 7. キャンセル・タイムアウトのレビュー

## 7.1 キャンセル方針は良いが、状態遷移を図にしたい

現状のキャンセル契約は、かなり丁寧に書かれています。

agentbridge-design.md

ただし、実装時に迷いやすい部分があります。

### 推奨する状態遷移

図のオプション

![](data\:image/svg+xml;utf8,%3Csvg%20id%3D%22mermaid-_r_ia_%22%20width%3D%22622.7849731445312%22%20xmlns%3D%22http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%22%20class%3D%22flowchart%22%20height%3D%221104.5999755859375%22%20viewBox%3D%224%204%20622.7849731445312%201104.5999755859375%22%20role%3D%22graphics-document%20document%22%20aria-roledescription%3D%22flowchart-v2%22%3E%3Cstyle%3E%23mermaid-_r_ia_%7Bfont-family%3A%22-apple-system%22%2C%22BlinkMacSystemFont%22%2C%22Segoe%20UI%22%2C%22Roboto%22%2C%22Oxygen%22%2C%22Ubuntu%22%2C%22Cantarell%22%2C%22Helvetica%20Neue%22%2C%22Arial%22%2C%22sans-serif%22%3Bfont-size%3A14px%3Bfill%3Argb\(255%2C%20255%2C%20255\)%3B%7D%40keyframes%20edge-animation-frame%7Bfrom%7Bstroke-dashoffset%3A0%3B%7D%7D%40keyframes%20dash%7Bto%7Bstroke-dashoffset%3A0%3B%7D%7D%23mermaid-_r_ia_%20.edge-animation-slow%7Bstroke-dasharray%3A9%2C5!important%3Bstroke-dashoffset%3A900%3Banimation%3Adash%2050s%20linear%20infinite%3Bstroke-linecap%3Around%3B%7D%23mermaid-_r_ia_%20.edge-animation-fast%7Bstroke-dasharray%3A9%2C5!important%3Bstroke-dashoffset%3A900%3Banimation%3Adash%2020s%20linear%20infinite%3Bstroke-linecap%3Around%3B%7D%23mermaid-_r_ia_%20.error-icon%7Bfill%3Argb\(33%2C%2033%2C%2033\)%3B%7D%23mermaid-_r_ia_%20.error-text%7Bfill%3Argb\(255%2C%20255%2C%20255\)%3Bstroke%3Argb\(255%2C%20255%2C%20255\)%3B%7D%23mermaid-_r_ia_%20.edge-thickness-normal%7Bstroke-width%3A1px%3B%7D%23mermaid-_r_ia_%20.edge-thickness-thick%7Bstroke-width%3A3.5px%3B%7D%23mermaid-_r_ia_%20.edge-pattern-solid%7Bstroke-dasharray%3A0%3B%7D%23mermaid-_r_ia_%20.edge-thickness-invisible%7Bstroke-width%3A0%3Bfill%3Anone%3B%7D%23mermaid-_r_ia_%20.edge-pattern-dashed%7Bstroke-dasharray%3A3%3B%7D%23mermaid-_r_ia_%20.edge-pattern-dotted%7Bstroke-dasharray%3A2%3B%7D%23mermaid-_r_ia_%20.marker%7Bfill%3Argb\(205%2C%20205%2C%20205\)%3Bstroke%3Argb\(205%2C%20205%2C%20205\)%3B%7D%23mermaid-_r_ia_%20.marker.cross%7Bstroke%3Argb\(205%2C%20205%2C%20205\)%3B%7D%23mermaid-_r_ia_%20svg%7Bfont-family%3A%22-apple-system%22%2C%22BlinkMacSystemFont%22%2C%22Segoe%20UI%22%2C%22Roboto%22%2C%22Oxygen%22%2C%22Ubuntu%22%2C%22Cantarell%22%2C%22Helvetica%20Neue%22%2C%22Arial%22%2C%22sans-serif%22%3Bfont-size%3A14px%3B%7D%23mermaid-_r_ia_%20p%7Bmargin%3A0%3B%7D%23mermaid-_r_ia_%20.label%7Bfont-family%3A%22-apple-system%22%2C%22BlinkMacSystemFont%22%2C%22Segoe%20UI%22%2C%22Roboto%22%2C%22Oxygen%22%2C%22Ubuntu%22%2C%22Cantarell%22%2C%22Helvetica%20Neue%22%2C%22Arial%22%2C%22sans-serif%22%3Bcolor%3Argb\(255%2C%20255%2C%20255\)%3B%7D%23mermaid-_r_ia_%20.cluster-label%20text%7Bfill%3Argb\(255%2C%20255%2C%20255\)%3B%7D%23mermaid-_r_ia_%20.cluster-label%20span%7Bcolor%3Argb\(255%2C%20255%2C%20255\)%3B%7D%23mermaid-_r_ia_%20.cluster-label%20span%20p%7Bbackground-color%3Atransparent%3B%7D%23mermaid-_r_ia_%20.label%20text%2C%23mermaid-_r_ia_%20span%7Bfill%3Argb\(255%2C%20255%2C%20255\)%3Bcolor%3Argb\(255%2C%20255%2C%20255\)%3B%7D%23mermaid-_r_ia_%20.node%20rect%2C%23mermaid-_r_ia_%20.node%20circle%2C%23mermaid-_r_ia_%20.node%20ellipse%2C%23mermaid-_r_ia_%20.node%20polygon%2C%23mermaid-_r_ia_%20.node%20path%7Bfill%3Argb\(9%2C%2023%2C%2044\)%3Bstroke%3Argb\(31%2C%2078%2C%20148\)%3Bstroke-width%3A1px%3B%7D%23mermaid-_r_ia_%20.rough-node%20.label%20text%2C%23mermaid-_r_ia_%20.node%20.label%20text%2C%23mermaid-_r_ia_%20.image-shape%20.label%2C%23mermaid-_r_ia_%20.icon-shape%20.label%7Btext-anchor%3Amiddle%3B%7D%23mermaid-_r_ia_%20.node%20.katex%20path%7Bfill%3A%23000%3Bstroke%3A%23000%3Bstroke-width%3A1px%3B%7D%23mermaid-_r_ia_%20.rough-node%20.label%2C%23mermaid-_r_ia_%20.node%20.label%2C%23mermaid-_r_ia_%20.image-shape%20.label%2C%23mermaid-_r_ia_%20.icon-shape%20.label%7Btext-align%3Acenter%3B%7D%23mermaid-_r_ia_%20.node.clickable%7Bcursor%3Apointer%3B%7D%23mermaid-_r_ia_%20.root%20.anchor%20path%7Bfill%3Argb\(205%2C%20205%2C%20205\)!important%3Bstroke-width%3A0%3Bstroke%3Argb\(205%2C%20205%2C%20205\)%3B%7D%23mermaid-_r_ia_%20.arrowheadPath%7Bfill%3Argb\(205%2C%20205%2C%20205\)%3B%7D%23mermaid-_r_ia_%20.edgePath%20.path%7Bstroke%3Argb\(205%2C%20205%2C%20205\)%3Bstroke-width%3A2.0px%3B%7D%23mermaid-_r_ia_%20.flowchart-link%7Bstroke%3Argb\(205%2C%20205%2C%20205\)%3Bfill%3Anone%3B%7D%23mermaid-_r_ia_%20.edgeLabel%7Bbackground-color%3Argb\(0%2C%200%2C%200\)%3Btext-align%3Acenter%3B%7D%23mermaid-_r_ia_%20.edgeLabel%20p%7Bbackground-color%3Argb\(0%2C%200%2C%200\)%3B%7D%23mermaid-_r_ia_%20.edgeLabel%20rect%7Bopacity%3A0.5%3Bbackground-color%3Argb\(0%2C%200%2C%200\)%3Bfill%3Argb\(0%2C%200%2C%200\)%3B%7D%23mermaid-_r_ia_%20.labelBkg%7Bbackground-color%3Argba\(0%2C%200%2C%200%2C%200.5\)%3B%7D%23mermaid-_r_ia_%20.cluster%20rect%7Bfill%3Argb\(33%2C%2033%2C%2033\)%3Bstroke%3Argba\(255%2C%20255%2C%20255%2C%200.05\)%3Bstroke-width%3A1px%3B%7D%23mermaid-_r_ia_%20.cluster%20text%7Bfill%3Argb\(255%2C%20255%2C%20255\)%3B%7D%23mermaid-_r_ia_%20.cluster%20span%7Bcolor%3Argb\(255%2C%20255%2C%20255\)%3B%7D%23mermaid-_r_ia_%20div.mermaidTooltip%7Bposition%3Aabsolute%3Btext-align%3Acenter%3Bmax-width%3A200px%3Bpadding%3A2px%3Bfont-family%3A%22-apple-system%22%2C%22BlinkMacSystemFont%22%2C%22Segoe%20UI%22%2C%22Roboto%22%2C%22Oxygen%22%2C%22Ubuntu%22%2C%22Cantarell%22%2C%22Helvetica%20Neue%22%2C%22Arial%22%2C%22sans-serif%22%3Bfont-size%3A12px%3Bbackground%3Argb\(33%2C%2033%2C%2033\)%3Bborder%3A1px%20solid%20rgba\(255%2C%20255%2C%20255%2C%200.05\)%3Bborder-radius%3A2px%3Bpointer-events%3Anone%3Bz-index%3A100%3B%7D%23mermaid-_r_ia_%20.flowchartTitleText%7Btext-anchor%3Amiddle%3Bfont-size%3A18px%3Bfill%3Argb\(255%2C%20255%2C%20255\)%3B%7D%23mermaid-_r_ia_%20rect.text%7Bfill%3Anone%3Bstroke-width%3A0%3B%7D%23mermaid-_r_ia_%20.icon-shape%2C%23mermaid-_r_ia_%20.image-shape%7Bbackground-color%3Argb\(0%2C%200%2C%200\)%3Btext-align%3Acenter%3B%7D%23mermaid-_r_ia_%20.icon-shape%20p%2C%23mermaid-_r_ia_%20.image-shape%20p%7Bbackground-color%3Argb\(0%2C%200%2C%200\)%3Bpadding%3A2px%3B%7D%23mermaid-_r_ia_%20.icon-shape%20rect%2C%23mermaid-_r_ia_%20.image-shape%20rect%7Bopacity%3A0.5%3Bbackground-color%3Argb\(0%2C%200%2C%200\)%3Bfill%3Argb\(0%2C%200%2C%200\)%3B%7D%23mermaid-_r_ia_%20.label-icon%7Bdisplay%3Ainline-block%3Bheight%3A1em%3Boverflow%3Avisible%3Bvertical-align%3A-0.125em%3B%7D%23mermaid-_r_ia_%20.node%20.label-icon%20path%7Bfill%3AcurrentColor%3Bstroke%3Arevert%3Bstroke-width%3Arevert%3B%7D%23mermaid-_r_ia_%20.node%20text%7Bfont-size%3A16px%3Bfont-weight%3A600%3Bletter-spacing%3A-0.32px%3Bfill%3A%2399ceff%3B%7D%23mermaid-_r_ia_%20.edgeLabels%20text%7Bfont-size%3A13px%3Bfont-weight%3A600%3Bletter-spacing%3A-0.08px%3Bfill%3A%2399ceff%3B%7D%23mermaid-_r_ia_%20.node%20tspan%5Bfont-weight%3D%22normal%22%5D%2C%23mermaid-_r_ia_%20.edgeLabels%20tspan%5Bfont-weight%3D%22normal%22%5D%7Bfont-weight%3A600%3B%7D%23mermaid-_r_ia_%20.edgeLabel%20.label%20rect%7Bopacity%3A1%3Brx%3A13px%3Bry%3A13px%3Bfill%3A%23000e1a%3Bstroke%3Argb\(26%2C%2062%2C%2095\)%3Bstroke-width%3A1px%3B%7D%23mermaid-_r_ia_%20.node%20rect%2C%23mermaid-_r_ia_%20.node%20circle%2C%23mermaid-_r_ia_%20.node%20ellipse%2C%23mermaid-_r_ia_%20.node%20polygon%2C%23mermaid-_r_ia_%20.node%20path%7Bfill%3Argb\(0%2C%2040%2C%2077\)%3Bstroke%3Argba\(255%2C%20255%2C%20255%2C%200.1\)%3Bstroke-width%3A1px%3B%7D%23mermaid-_r_ia_%20.node%20rect%7Brx%3A16px%3Bry%3A16px%3B%7D%23mermaid-_r_ia_%20.node.mermaid-decision%20.label-container%7Bfill%3A%23000e1a%3Bstroke%3Argb\(26%2C%2062%2C%2095\)%3Bstroke-dasharray%3A2%202%3B%7D%23mermaid-_r_ia_%20.edgePaths%20.flowchart-link%7Bstroke%3Argb\(26%2C%2062%2C%2095\)%3Bstroke-width%3A1px%3Bstroke-linecap%3Around%3Bstroke-linejoin%3Around%3B%7D%23mermaid-_r_ia_%20.marker%7Bfill%3Argb\(26%2C%2062%2C%2095\)%3Bstroke%3Argb\(26%2C%2062%2C%2095\)%3B%7D%23mermaid-_r_ia_%20.node%7Bcolor-scheme%3Adark%3B%7D%23mermaid-_r_ia_%20%3Aroot%7B--mermaid-font-family%3A%22-apple-system%22%2C%22BlinkMacSystemFont%22%2C%22Segoe%20UI%22%2C%22Roboto%22%2C%22Oxygen%22%2C%22Ubuntu%22%2C%22Cantarell%22%2C%22Helvetica%20Neue%22%2C%22Arial%22%2C%22sans-serif%22%3B%7D%3C%2Fstyle%3E%3Cg%3E%3Cmarker%20id%3D%22mermaid-_r_ia__flowchart-v2-pointEnd%22%20class%3D%22marker%20flowchart-v2%22%20viewBox%3D%22-5%20-5%2010%2010%22%20refX%3D%220%22%20refY%3D%220%22%20markerUnits%3D%22userSpaceOnUse%22%20markerWidth%3D%2210%22%20markerHeight%3D%2210%22%20orient%3D%22auto%22%3E%3Cpath%20d%3D%22M%200%200%20L%204%200%20M%200.8180194846605362%20-3.181980515339464%20L%204%200%20L%200.8180194846605362%203.181980515339464%22%20class%3D%22arrowMarkerPath%22%20style%3D%22stroke-width%3A%201%3B%20stroke-dasharray%3A%20none%3B%20fill%3A%20none%3B%20stroke-linecap%3A%20round%3B%20stroke-linejoin%3A%20round%3B%22%3E%3C%2Fpath%3E%3C%2Fmarker%3E%3Cmarker%20id%3D%22mermaid-_r_ia__flowchart-v2-pointStart%22%20class%3D%22marker%20flowchart-v2%22%20viewBox%3D%22-5%20-5%2010%2010%22%20refX%3D%220%22%20refY%3D%220%22%20markerUnits%3D%22userSpaceOnUse%22%20markerWidth%3D%2210%22%20markerHeight%3D%2210%22%20orient%3D%22auto%22%3E%3Cpath%20d%3D%22M%200%200%20L%20-4%200%20M%20-0.8180194846605362%20-3.181980515339464%20L%20-4%200%20L%20-0.8180194846605362%203.181980515339464%22%20class%3D%22arrowMarkerPath%22%20style%3D%22stroke-width%3A%201%3B%20stroke-dasharray%3A%20none%3B%20fill%3A%20none%3B%20stroke-linecap%3A%20round%3B%20stroke-linejoin%3A%20round%3B%22%3E%3C%2Fpath%3E%3C%2Fmarker%3E%3Cmarker%20id%3D%22mermaid-_r_ia__flowchart-v2-circleEnd%22%20class%3D%22marker%20flowchart-v2%22%20viewBox%3D%220%200%2010%2010%22%20refX%3D%2211%22%20refY%3D%225%22%20markerUnits%3D%22userSpaceOnUse%22%20markerWidth%3D%2211%22%20markerHeight%3D%2211%22%20orient%3D%22auto%22%3E%3Ccircle%20cx%3D%225%22%20cy%3D%225%22%20r%3D%225%22%20class%3D%22arrowMarkerPath%22%20style%3D%22stroke-width%3A%201%3B%20stroke-dasharray%3A%201%2C%200%3B%22%3E%3C%2Fcircle%3E%3C%2Fmarker%3E%3Cmarker%20id%3D%22mermaid-_r_ia__flowchart-v2-circleStart%22%20class%3D%22marker%20flowchart-v2%22%20viewBox%3D%220%200%2010%2010%22%20refX%3D%22-1%22%20refY%3D%225%22%20markerUnits%3D%22userSpaceOnUse%22%20markerWidth%3D%2211%22%20markerHeight%3D%2211%22%20orient%3D%22auto%22%3E%3Ccircle%20cx%3D%225%22%20cy%3D%225%22%20r%3D%225%22%20class%3D%22arrowMarkerPath%22%20style%3D%22stroke-width%3A%201%3B%20stroke-dasharray%3A%201%2C%200%3B%22%3E%3C%2Fcircle%3E%3C%2Fmarker%3E%3Cmarker%20id%3D%22mermaid-_r_ia__flowchart-v2-crossEnd%22%20class%3D%22marker%20cross%20flowchart-v2%22%20viewBox%3D%220%200%2011%2011%22%20refX%3D%2212%22%20refY%3D%225.2%22%20markerUnits%3D%22userSpaceOnUse%22%20markerWidth%3D%2211%22%20markerHeight%3D%2211%22%20orient%3D%22auto%22%3E%3Cpath%20d%3D%22M%201%2C1%20l%209%2C9%20M%2010%2C1%20l%20-9%2C9%22%20class%3D%22arrowMarkerPath%22%20style%3D%22stroke-width%3A%202%3B%20stroke-dasharray%3A%201%2C%200%3B%22%3E%3C%2Fpath%3E%3C%2Fmarker%3E%3Cmarker%20id%3D%22mermaid-_r_ia__flowchart-v2-crossStart%22%20class%3D%22marker%20cross%20flowchart-v2%22%20viewBox%3D%220%200%2011%2011%22%20refX%3D%22-1%22%20refY%3D%225.2%22%20markerUnits%3D%22userSpaceOnUse%22%20markerWidth%3D%2211%22%20markerHeight%3D%2211%22%20orient%3D%22auto%22%3E%3Cpath%20d%3D%22M%201%2C1%20l%209%2C9%20M%2010%2C1%20l%20-9%2C9%22%20class%3D%22arrowMarkerPath%22%20style%3D%22stroke-width%3A%202%3B%20stroke-dasharray%3A%201%2C%200%3B%22%3E%3C%2Fpath%3E%3C%2Fmarker%3E%3C%2Fg%3E%3Cg%20class%3D%22subgraphs%22%3E%3C%2Fg%3E%3Cg%20class%3D%22nodes%22%3E%3Cg%20class%3D%22node%20default%22%20id%3D%22flowchart-A-0%22%20transform%3D%22translate\(445.3449249267578%2C%20228\)%22%3E%3Crect%20class%3D%22basic%20label-container%22%20style%3D%22%22%20x%3D%22-67.78205108642578%22%20y%3D%22-30%22%20width%3D%22135.56410217285156%22%20height%3D%2260%22%3E%3C%2Frect%3E%3Cg%20class%3D%22label%22%20style%3D%22%22%20transform%3D%22translate\(0%2C%20-10.5\)%22%3E%3Crect%3E%3C%2Frect%3E%3Cg%3E%3Crect%20class%3D%22background%22%20style%3D%22stroke%3A%20none%22%3E%3C%2Frect%3E%3Ctext%20y%3D%22-10.1%22%20style%3D%22%22%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%22-0.1em%22%20dy%3D%221.1em%22%3E%3Ctspan%20font-style%3D%22normal%22%20class%3D%22text-inner-tspan%22%20font-weight%3D%22normal%22%3ETurn%E9%96%8B%E5%A7%8B%3C%2Ftspan%3E%3C%2Ftspan%3E%3C%2Ftext%3E%3C%2Fg%3E%3C%2Fg%3E%3C%2Fg%3E%3Cg%20class%3D%22node%20default%22%20id%3D%22flowchart-B-1%22%20transform%3D%22translate\(297.4611511230469%2C%20328\)%22%3E%3Crect%20class%3D%22basic%20label-container%22%20style%3D%22%22%20x%3D%22-105.7748794555664%22%20y%3D%22-30%22%20width%3D%22211.5497589111328%22%20height%3D%2260%22%3E%3C%2Frect%3E%3Cg%20class%3D%22label%22%20style%3D%22%22%20transform%3D%22translate\(0%2C%20-10.5\)%22%3E%3Crect%3E%3C%2Frect%3E%3Cg%3E%3Crect%20class%3D%22background%22%20style%3D%22stroke%3A%20none%22%3E%3C%2Frect%3E%3Ctext%20y%3D%22-10.1%22%20style%3D%22%22%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%22-0.1em%22%20dy%3D%221.1em%22%3E%3Ctspan%20font-style%3D%22normal%22%20class%3D%22text-inner-tspan%22%20font-weight%3D%22normal%22%3ELLM%E3%82%B9%E3%83%88%E3%83%AA%E3%83%BC%E3%83%9F%E3%83%B3%E3%82%B0%3C%2Ftspan%3E%3C%2Ftspan%3E%3C%2Ftext%3E%3C%2Fg%3E%3C%2Fg%3E%3C%2Fg%3E%3Cg%20class%3D%22node%20default%20%20mermaid-decision%22%20id%3D%22flowchart-C-3%22%20transform%3D%22translate\(152.18400065104166%2C%20428\)%22%3E%3Crect%20class%3D%22basic%20label-container%22%20style%3D%22%22%20x%3D%22-78.70001983642578%22%20y%3D%22-30%22%20width%3D%22157.40003967285156%22%20height%3D%2260%22%3E%3C%2Frect%3E%3Cg%20class%3D%22label%22%20style%3D%22%22%20transform%3D%22translate\(0%2C%20-10.5\)%22%3E%3Crect%3E%3C%2Frect%3E%3Cg%3E%3Crect%20class%3D%22background%22%20style%3D%22stroke%3A%20none%22%3E%3C%2Frect%3E%3Ctext%20y%3D%22-10.1%22%20style%3D%22%22%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%22-0.1em%22%20dy%3D%221.1em%22%3E%3Ctspan%20font-style%3D%22normal%22%20class%3D%22text-inner-tspan%22%20font-weight%3D%22normal%22%3E%E3%82%AD%E3%83%A3%E3%83%B3%E3%82%BB%E3%83%AB%3F%3C%2Ftspan%3E%3C%2Ftspan%3E%3C%2Ftext%3E%3C%2Fg%3E%3C%2Fg%3E%3C%2Fg%3E%3Cg%20class%3D%22node%20default%22%20id%3D%22flowchart-D-5%22%20transform%3D%22translate\(152.18400065104166%2C%20594\)%22%3E%3Crect%20class%3D%22basic%20label-container%22%20style%3D%22%22%20x%3D%22-90.8000259399414%22%20y%3D%22-30%22%20width%3D%22181.6000518798828%22%20height%3D%2260%22%3E%3C%2Frect%3E%3Cg%20class%3D%22label%22%20style%3D%22%22%20transform%3D%22translate\(0%2C%20-10.5\)%22%3E%3Crect%3E%3C%2Frect%3E%3Cg%3E%3Crect%20class%3D%22background%22%20style%3D%22stroke%3A%20none%22%3E%3C%2Frect%3E%3Ctext%20y%3D%22-10.1%22%20style%3D%22%22%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%22-0.1em%22%20dy%3D%221.1em%22%3E%3Ctspan%20font-style%3D%22normal%22%20class%3D%22text-inner-tspan%22%20font-weight%3D%22normal%22%3E%E3%82%B9%E3%83%88%E3%83%AA%E3%83%BC%E3%83%A0%E4%B8%AD%E6%96%AD%3C%2Ftspan%3E%3C%2Ftspan%3E%3C%2Ftext%3E%3C%2Fg%3E%3C%2Fg%3E%3C%2Fg%3E%3Cg%20class%3D%22node%20default%20%20mermaid-decision%22%20id%3D%22flowchart-E-7%22%20transform%3D%22translate\(152.18400065104166%2C%20694\)%22%3E%3Crect%20class%3D%22basic%20label-container%22%20style%3D%22%22%20x%3D%22-100.39190673828125%22%20y%3D%22-30%22%20width%3D%22200.7838134765625%22%20height%3D%2260%22%3E%3C%2Frect%3E%3Cg%20class%3D%22label%22%20style%3D%22%22%20transform%3D%22translate\(0%2C%20-10.5\)%22%3E%3Crect%3E%3C%2Frect%3E%3Cg%3E%3Crect%20class%3D%22background%22%20style%3D%22stroke%3A%20none%22%3E%3C%2Frect%3E%3Ctext%20y%3D%22-10.1%22%20style%3D%22%22%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%22-0.1em%22%20dy%3D%221.1em%22%3E%3Ctspan%20font-style%3D%22normal%22%20class%3D%22text-inner-tspan%22%20font-weight%3D%22normal%22%3ETool%3C%2Ftspan%3E%3Ctspan%20font-style%3D%22normal%22%20class%3D%22text-inner-tspan%22%20font-weight%3D%22normal%22%3E%20Use%E7%A2%BA%E5%AE%9A%E6%B8%88%E3%81%BF%3F%3C%2Ftspan%3E%3C%2Ftspan%3E%3C%2Ftext%3E%3C%2Fg%3E%3C%2Fg%3E%3C%2Fg%3E%3Cg%20class%3D%22node%20default%22%20id%3D%22flowchart-F-9%22%20transform%3D%22translate\(118.72003173828125%2C%20970.5999984741211\)%22%3E%3Crect%20class%3D%22basic%20label-container%22%20style%3D%22%22%20x%3D%22-106.72003173828125%22%20y%3D%22-30%22%20width%3D%22213.4400634765625%22%20height%3D%2260%22%3E%3C%2Frect%3E%3Cg%20class%3D%22label%22%20style%3D%22%22%20transform%3D%22translate\(0%2C%20-10.5\)%22%3E%3Crect%3E%3C%2Frect%3E%3Cg%3E%3Crect%20class%3D%22background%22%20style%3D%22stroke%3A%20none%22%3E%3C%2Frect%3E%3Ctext%20y%3D%22-10.1%22%20style%3D%22%22%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%22-0.1em%22%20dy%3D%221.1em%22%3E%3Ctspan%20font-style%3D%22normal%22%20class%3D%22text-inner-tspan%22%20font-weight%3D%22normal%22%3E%E4%B8%8D%E5%AE%8C%E5%85%A8%E3%81%AA%E5%BF%9C%E7%AD%94%E3%82%92%E7%A0%B4%E6%A3%84%3C%2Ftspan%3E%3C%2Ftspan%3E%3C%2Ftext%3E%3C%2Fg%3E%3C%2Fg%3E%3C%2Fg%3E%3Cg%20class%3D%22node%20default%22%20id%3D%22flowchart-G-11%22%20transform%3D%22translate\(363.2213134765625%2C%20865.2999992370605\)%22%3E%3Crect%20class%3D%22basic%20label-container%22%20style%3D%22%22%20x%3D%22-131.4755401611328%22%20y%3D%22-35.29999923706055%22%20width%3D%22262.9510803222656%22%20height%3D%2270.5999984741211%22%3E%3C%2Frect%3E%3Cg%20class%3D%22label%22%20style%3D%22%22%20transform%3D%22translate\(0%2C%20-19.299999237060547\)%22%3E%3Crect%3E%3C%2Frect%3E%3Cg%3E%3Crect%20class%3D%22background%22%20style%3D%22stroke%3A%20none%22%3E%3C%2Frect%3E%3Ctext%20y%3D%22-10.1%22%20style%3D%22%22%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%22-0.1em%22%20dy%3D%221.1em%22%3E%3Ctspan%20font-style%3D%22normal%22%20class%3D%22text-inner-tspan%22%20font-weight%3D%22normal%22%3E%E6%9C%AA%E5%AE%8C%E4%BA%86Tool%E3%81%ABCancelled%E7%B5%90%E6%9E%9C%3C%2Ftspan%3E%3C%2Ftspan%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%221em%22%20dy%3D%221.1em%22%3E%3Ctspan%20font-style%3D%22normal%22%20class%3D%22text-inner-tspan%22%20font-weight%3D%22normal%22%3E%E3%82%92%E8%A8%98%E9%8C%B2%3C%2Ftspan%3E%3C%2Ftspan%3E%3C%2Ftext%3E%3C%2Fg%3E%3C%2Fg%3E%3C%2Fg%3E%3Cg%20class%3D%22node%20default%22%20id%3D%22flowchart-H-13%22%20transform%3D%22translate\(363.2213134765625%2C%20970.5999984741211\)%22%3E%3Crect%20class%3D%22basic%20label-container%22%20style%3D%22%22%20x%3D%22-97.78125%22%20y%3D%22-30%22%20width%3D%22195.5625%22%20height%3D%2260%22%3E%3C%2Frect%3E%3Cg%20class%3D%22label%22%20style%3D%22%22%20transform%3D%22translate\(0%2C%20-10.5\)%22%3E%3Crect%3E%3C%2Frect%3E%3Cg%3E%3Crect%20class%3D%22background%22%20style%3D%22stroke%3A%20none%22%3E%3C%2Frect%3E%3Ctext%20y%3D%22-10.1%22%20style%3D%22%22%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%22-0.1em%22%20dy%3D%221.1em%22%3E%3Ctspan%20font-style%3D%22normal%22%20class%3D%22text-inner-tspan%22%20font-weight%3D%22normal%22%3E%E8%BF%BD%E5%8A%A0LLM%E5%BE%80%E5%BE%A9%E3%81%AA%E3%81%97%3C%2Ftspan%3E%3C%2Ftspan%3E%3C%2Ftext%3E%3C%2Fg%3E%3C%2Fg%3E%3C%2Fg%3E%3Cg%20class%3D%22node%20default%22%20id%3D%22flowchart-I-15%22%20transform%3D%22translate\(363.2213134765625%2C%201070.599998474121\)%22%3E%3Crect%20class%3D%22basic%20label-container%22%20style%3D%22%22%20x%3D%22-74.8046875%22%20y%3D%22-30%22%20width%3D%22149.609375%22%20height%3D%2260%22%3E%3C%2Frect%3E%3Cg%20class%3D%22label%22%20style%3D%22%22%20transform%3D%22translate\(0%2C%20-10.5\)%22%3E%3Crect%3E%3C%2Frect%3E%3Cg%3E%3Crect%20class%3D%22background%22%20style%3D%22stroke%3A%20none%22%3E%3C%2Frect%3E%3Ctext%20y%3D%22-10.1%22%20style%3D%22%22%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%22-0.1em%22%20dy%3D%221.1em%22%3E%3Ctspan%20font-style%3D%22normal%22%20class%3D%22text-inner-tspan%22%20font-weight%3D%22normal%22%3E%E3%82%BF%E3%83%BC%E3%83%B3%E7%B5%82%E4%BA%86%3C%2Ftspan%3E%3C%2Ftspan%3E%3C%2Ftext%3E%3C%2Fg%3E%3C%2Fg%3E%3C%2Fg%3E%3Cg%20class%3D%22node%20default%20%20mermaid-decision%22%20id%3D%22flowchart-J-19%22%20transform%3D%22translate\(160.3866081237793%2C%2042\)%22%3E%3Crect%20class%3D%22basic%20label-container%22%20style%3D%22%22%20x%3D%22-68.87189483642578%22%20y%3D%22-30%22%20width%3D%22137.74378967285156%22%20height%3D%2260%22%3E%3C%2Frect%3E%3Cg%20class%3D%22label%22%20style%3D%22%22%20transform%3D%22translate\(0%2C%20-10.5\)%22%3E%3Crect%3E%3C%2Frect%3E%3Cg%3E%3Crect%20class%3D%22background%22%20style%3D%22stroke%3A%20none%22%3E%3C%2Frect%3E%3Ctext%20y%3D%22-10.1%22%20style%3D%22%22%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%22-0.1em%22%20dy%3D%221.1em%22%3E%3Ctspan%20font-style%3D%22normal%22%20class%3D%22text-inner-tspan%22%20font-weight%3D%22normal%22%3ETool%3C%2Ftspan%3E%3Ctspan%20font-style%3D%22normal%22%20class%3D%22text-inner-tspan%22%20font-weight%3D%22normal%22%3E%20Call%3F%3C%2Ftspan%3E%3C%2Ftspan%3E%3C%2Ftext%3E%3C%2Fg%3E%3C%2Fg%3E%3C%2Fg%3E%3Cg%20class%3D%22node%20default%22%20id%3D%22flowchart-K-21%22%20transform%3D%22translate\(262.2028579711914%2C%20228\)%22%3E%3Crect%20class%3D%22basic%20label-container%22%20style%3D%22%22%20x%3D%22-75.36001586914062%22%20y%3D%22-30%22%20width%3D%22150.72003173828125%22%20height%3D%2260%22%3E%3C%2Frect%3E%3Cg%20class%3D%22label%22%20style%3D%22%22%20transform%3D%22translate\(0%2C%20-10.5\)%22%3E%3Crect%3E%3C%2Frect%3E%3Cg%3E%3Crect%20class%3D%22background%22%20style%3D%22stroke%3A%20none%22%3E%3C%2Frect%3E%3Ctext%20y%3D%22-10.1%22%20style%3D%22%22%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%22-0.1em%22%20dy%3D%221.1em%22%3E%3Ctspan%20font-style%3D%22normal%22%20class%3D%22text-inner-tspan%22%20font-weight%3D%22normal%22%3E%E3%83%84%E3%83%BC%E3%83%AB%E5%AE%9F%E8%A1%8C%3C%2Ftspan%3E%3C%2Ftspan%3E%3C%2Ftext%3E%3C%2Fg%3E%3C%2Fg%3E%3C%2Fg%3E%3Cg%20class%3D%22node%20default%22%20id%3D%22flowchart-L-25%22%20transform%3D%22translate\(533.1269760131836%2C%20328\)%22%3E%3Crect%20class%3D%22basic%20label-container%22%20style%3D%22%22%20x%3D%22-85.65801239013672%22%20y%3D%22-30%22%20width%3D%22171.31602478027344%22%20height%3D%2260%22%3E%3C%2Frect%3E%3Cg%20class%3D%22label%22%20style%3D%22%22%20transform%3D%22translate\(0%2C%20-10.5\)%22%3E%3Crect%3E%3C%2Frect%3E%3Cg%3E%3Crect%20class%3D%22background%22%20style%3D%22stroke%3A%20none%22%3E%3C%2Frect%3E%3Ctext%20y%3D%22-10.1%22%20style%3D%22%22%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%22-0.1em%22%20dy%3D%221.1em%22%3E%3Ctspan%20font-style%3D%22normal%22%20class%3D%22text-inner-tspan%22%20font-weight%3D%22normal%22%3ETurnComplete%3C%2Ftspan%3E%3C%2Ftspan%3E%3C%2Ftext%3E%3C%2Fg%3E%3C%2Fg%3E%3C%2Fg%3E%3C%2Fg%3E%3Cg%20class%3D%22edges%20edgePaths%22%3E%3Cpath%20d%3D%22M445.3449249267578%2C258L445.3449249267578%2C271.21704397470535Q445.3449249267578%2C273%20444.2591384891309%2C274.4142135623731L444.2591384891309%2C274.4142135623731Q443.173352051504%2C275.8284271247462%20441.7591384891309%2C276.9142135623731L441.7591384891309%2C276.9142135623731Q440.3449249267578%2C278%20438.56196890146316%2C278L339.502400300197%2C278Q337.71944427490234%2C278%20336.30523071252924%2C279.0857864376269L336.30523071252924%2C279.0857864376269Q334.89101715015613%2C280.1715728752538%20333.80523071252924%2C281.5857864376269L333.80523071252924%2C281.5857864376269Q332.71944427490234%2C283%20332.71944427490234%2C284.78295602529465L332.71944427490234%2C288%22%20id%3D%22L_A_B_0%22%20class%3D%22edge-thickness-normal%20edge-pattern-solid%20edge-thickness-normal%20edge-pattern-solid%20flowchart-link%22%20style%3D%22%3B%22%20data-edge%3D%22true%22%20data-et%3D%22edge%22%20data-id%3D%22L_A_B_0%22%20data-points%3D%22W3sieCI6NDQ1LjM0NDkyNDkyNjc1NzgsInkiOjI1OH0seyJ4Ijo0NDUuMzQ0OTI0OTI2NzU3OCwieSI6Mjc4fSx7IngiOjMzMi43MTk0NDQyNzQ5MDIzNCwieSI6Mjc4fSx7IngiOjMzMi43MTk0NDQyNzQ5MDIzNCwieSI6MjkyfV0%3D%22%20marker-end%3D%22url\(%23mermaid-_r_ia__flowchart-v2-pointEnd\)%22%3E%3C%2Fpath%3E%3Cpath%20d%3D%22M297.4611511230469%2C358L297.4611511230469%2C371.21704397470535Q297.4611511230469%2C373%20296.37536468542%2C374.4142135623731L296.37536468542%2C374.4142135623731Q295.2895782477931%2C375.8284271247462%20293.87536468542%2C376.9142135623731L293.87536468542%2C376.9142135623731Q292.4611511230469%2C378%20290.6781950977522%2C378L185.2002966218116%2C378Q183.41734059651694%2C378%20182.00312703414386%2C379.0857864376269L182.00312703414386%2C379.0857864376269Q180.58891347177075%2C380.1715728752538%20179.50312703414386%2C381.5857864376269L179.50312703414386%2C381.5857864376269Q178.41734059651694%2C383%20178.41734059651694%2C384.78295602529465L178.41734059651694%2C388%22%20id%3D%22L_B_C_0%22%20class%3D%22edge-thickness-normal%20edge-pattern-solid%20edge-thickness-normal%20edge-pattern-solid%20flowchart-link%22%20style%3D%22%3B%22%20data-edge%3D%22true%22%20data-et%3D%22edge%22%20data-id%3D%22L_B_C_0%22%20data-points%3D%22W3sieCI6Mjk3LjQ2MTE1MTEyMzA0NjksInkiOjM1OH0seyJ4IjoyOTcuNDYxMTUxMTIzMDQ2OSwieSI6Mzc4fSx7IngiOjE3OC40MTczNDA1OTY1MTY5NCwieSI6Mzc4fSx7IngiOjE3OC40MTczNDA1OTY1MTY5NCwieSI6MzkyfV0%3D%22%20marker-end%3D%22url\(%23mermaid-_r_ia__flowchart-v2-pointEnd\)%22%3E%3C%2Fpath%3E%3Cpath%20d%3D%22M152.18400065104166%2C458L152.18400065104166%2C552%22%20id%3D%22L_C_D_0%22%20class%3D%22edge-thickness-normal%20edge-pattern-solid%20edge-thickness-normal%20edge-pattern-solid%20flowchart-link%22%20style%3D%22%3B%22%20data-edge%3D%22true%22%20data-et%3D%22edge%22%20data-id%3D%22L_C_D_0%22%20data-points%3D%22W3sieCI6MTUyLjE4NDAwMDY1MTA0MTY2LCJ5Ijo0NTh9LHsieCI6MTUyLjE4NDAwMDY1MTA0MTY2LCJ5Ijo1NTZ9XQ%3D%3D%22%20marker-end%3D%22url\(%23mermaid-_r_ia__flowchart-v2-pointEnd\)%22%3E%3C%2Fpath%3E%3Cpath%20d%3D%22M152.18400065104166%2C624L152.18400065104166%2C652%22%20id%3D%22L_D_E_0%22%20class%3D%22edge-thickness-normal%20edge-pattern-solid%20edge-thickness-normal%20edge-pattern-solid%20flowchart-link%22%20style%3D%22%3B%22%20data-edge%3D%22true%22%20data-et%3D%22edge%22%20data-id%3D%22L_D_E_0%22%20data-points%3D%22W3sieCI6MTUyLjE4NDAwMDY1MTA0MTY2LCJ5Ijo2MjR9LHsieCI6MTUyLjE4NDAwMDY1MTA0MTY2LCJ5Ijo2NTZ9XQ%3D%3D%22%20marker-end%3D%22url\(%23mermaid-_r_ia__flowchart-v2-pointEnd\)%22%3E%3C%2Fpath%3E%3Cpath%20d%3D%22M118.72003173828122%2C724L118.72003173828125%2C865.2999992370605L118.72003173828125%2C928.5999984741211%22%20id%3D%22L_E_F_0%22%20class%3D%22edge-thickness-normal%20edge-pattern-solid%20edge-thickness-normal%20edge-pattern-solid%20flowchart-link%22%20style%3D%22%3B%22%20data-edge%3D%22true%22%20data-et%3D%22edge%22%20data-id%3D%22L_E_F_0%22%20data-points%3D%22W3sieCI6MTE4LjcyMDAzMTczODI4MTIyLCJ5Ijo3MjR9LHsieCI6MTE4LjcyMDAzMTczODI4MTI1LCJ5Ijo4NjUuMjk5OTk5MjM3MDYwNX0seyJ4IjoxMTguNzIwMDMxNzM4MjgxMjUsInkiOjkzMi41OTk5OTg0NzQxMjExfV0%3D%22%20marker-end%3D%22url\(%23mermaid-_r_ia__flowchart-v2-pointEnd\)%22%3E%3C%2Fpath%3E%3Cpath%20d%3D%22M185.64796956380212%2C724L185.6479695638021%2C736.9289321881346Q185.6479695638021%2C744%20192.71903737566757%2C744L356.43835745126785%2C744Q358.2213134765625%2C744%20359.6355270389356%2C745.0857864376269L359.6355270389356%2C745.0857864376269Q361.0497406013087%2C746.1715728752538%20362.1355270389356%2C747.5857864376269L362.1355270389356%2C747.5857864376269Q363.2213134765625%2C749%20363.2213134765625%2C750.7829560252947L363.2213134765625%2C818%22%20id%3D%22L_E_G_0%22%20class%3D%22edge-thickness-normal%20edge-pattern-solid%20edge-thickness-normal%20edge-pattern-solid%20flowchart-link%22%20style%3D%22%3B%22%20data-edge%3D%22true%22%20data-et%3D%22edge%22%20data-id%3D%22L_E_G_0%22%20data-points%3D%22W3sieCI6MTg1LjY0Nzk2OTU2MzgwMjEyLCJ5Ijo3MjR9LHsieCI6MTg1LjY0Nzk2OTU2MzgwMjEsInkiOjc0NH0seyJ4IjozNjMuMjIxMzEzNDc2NTYyNSwieSI6NzQ0fSx7IngiOjM2My4yMjEzMTM0NzY1NjI1LCJ5Ijo4MjJ9XQ%3D%3D%22%20marker-end%3D%22url\(%23mermaid-_r_ia__flowchart-v2-pointEnd\)%22%3E%3C%2Fpath%3E%3Cpath%20d%3D%22M363.2213134765625%2C900.5999984741211L363.2213134765625%2C928.5999984741211%22%20id%3D%22L_G_H_0%22%20class%3D%22edge-thickness-normal%20edge-pattern-solid%20edge-thickness-normal%20edge-pattern-solid%20flowchart-link%22%20style%3D%22%3B%22%20data-edge%3D%22true%22%20data-et%3D%22edge%22%20data-id%3D%22L_G_H_0%22%20data-points%3D%22W3sieCI6MzYzLjIyMTMxMzQ3NjU2MjUsInkiOjkwMC41OTk5OTg0NzQxMjExfSx7IngiOjM2My4yMjEzMTM0NzY1NjI1LCJ5Ijo5MzIuNTk5OTk4NDc0MTIxMX1d%22%20marker-end%3D%22url\(%23mermaid-_r_ia__flowchart-v2-pointEnd\)%22%3E%3C%2Fpath%3E%3Cpath%20d%3D%22M118.72003173828125%2C1000.5999984741211L118.72003173828125%2C1013.8170424488264Q118.72003173828125%2C1015.5999984741211%20119.80581817590816%2C1017.0142120364942L119.80581817590817%2C1017.0142120364942Q120.89160461353507%2C1018.4284255988673%20122.30581817590816%2C1019.5142120364942L122.30581817590816%2C1019.5142120364942Q123.72003173828125%2C1020.5999984741211%20125.5029877635759%2C1020.5999984741211L319.03601370126785%2C1020.5999984741211Q320.8189697265625%2C1020.5999984741211%20322.2331832889356%2C1021.685784911748L322.2331832889356%2C1021.685784911748Q323.6473968513087%2C1022.7715713493749%20324.7331832889356%2C1024.185784911748L324.7331832889356%2C1024.185784911748Q325.8189697265625%2C1025.599998474121%20325.8189697265625%2C1027.3829544994157L325.8189697265625%2C1030.599998474121%22%20id%3D%22L_F_I_0%22%20class%3D%22edge-thickness-normal%20edge-pattern-solid%20edge-thickness-normal%20edge-pattern-solid%20flowchart-link%22%20style%3D%22%3B%22%20data-edge%3D%22true%22%20data-et%3D%22edge%22%20data-id%3D%22L_F_I_0%22%20data-points%3D%22W3sieCI6MTE4LjcyMDAzMTczODI4MTI1LCJ5IjoxMDAwLjU5OTk5ODQ3NDEyMTF9LHsieCI6MTE4LjcyMDAzMTczODI4MTI1LCJ5IjoxMDIwLjU5OTk5ODQ3NDEyMTF9LHsieCI6MzI1LjgxODk2OTcyNjU2MjUsInkiOjEwMjAuNTk5OTk4NDc0MTIxMX0seyJ4IjozMjUuODE4OTY5NzI2NTYyNSwieSI6MTAzNC41OTk5OTg0NzQxMjF9XQ%3D%3D%22%20marker-end%3D%22url\(%23mermaid-_r_ia__flowchart-v2-pointEnd\)%22%3E%3C%2Fpath%3E%3Cpath%20d%3D%22M363.2213134765625%2C1000.5999984741211L363.2213134765625%2C1028.599998474121%22%20id%3D%22L_H_I_0%22%20class%3D%22edge-thickness-normal%20edge-pattern-solid%20edge-thickness-normal%20edge-pattern-solid%20flowchart-link%22%20style%3D%22%3B%22%20data-edge%3D%22true%22%20data-et%3D%22edge%22%20data-id%3D%22L_H_I_0%22%20data-points%3D%22W3sieCI6MzYzLjIyMTMxMzQ3NjU2MjUsInkiOjEwMDAuNTk5OTk4NDc0MTIxMX0seyJ4IjozNjMuMjIxMzEzNDc2NTYyNSwieSI6MTAzMi41OTk5OTg0NzQxMjF9XQ%3D%3D%22%20marker-end%3D%22url\(%23mermaid-_r_ia__flowchart-v2-pointEnd\)%22%3E%3C%2Fpath%3E%3Cpath%20d%3D%22M125.95066070556639%2C398L125.9506607055664%2C328L125.9506607055664%2C145L125.9506607055664%2C84%22%20id%3D%22L_C_J_0%22%20class%3D%22edge-thickness-normal%20edge-pattern-solid%20edge-thickness-normal%20edge-pattern-solid%20flowchart-link%22%20style%3D%22%3B%22%20data-edge%3D%22true%22%20data-et%3D%22edge%22%20data-id%3D%22L_C_J_0%22%20data-points%3D%22W3sieCI6MTI1Ljk1MDY2MDcwNTU2NjM5LCJ5IjozOTh9LHsieCI6MTI1Ljk1MDY2MDcwNTU2NjQsInkiOjMyOH0seyJ4IjoxMjUuOTUwNjYwNzA1NTY2NCwieSI6MTQ1fSx7IngiOjEyNS45NTA2NjA3MDU1NjY0LCJ5Ijo4MH1d%22%20marker-end%3D%22url\(%23mermaid-_r_ia__flowchart-v2-pointEnd\)%22%3E%3C%2Fpath%3E%3Cpath%20d%3D%22M160.3866081237793%2C72L160.3866081237793%2C105.21704397470535Q160.3866081237793%2C107%20161.4723945614062%2C108.41421356237309L161.4723945614062%2C108.41421356237309Q162.5581809990331%2C109.82842712474618%20163.9723945614062%2C110.91421356237309L163.9723945614062%2C110.91421356237309Q165.3866081237793%2C112%20167.16956414907395%2C112L255.41990194589673%2C112Q257.2028579711914%2C112%20258.6170715335645%2C113.08578643762691L258.6170715335645%2C113.08578643762692Q260.0312850959376%2C114.17157287525382%20261.1170715335645%2C115.58578643762691L261.1170715335645%2C115.58578643762691Q262.2028579711914%2C117%20262.2028579711914%2C118.78295602529465L262.2028579711914%2C186%22%20id%3D%22L_J_K_0%22%20class%3D%22edge-thickness-normal%20edge-pattern-solid%20edge-thickness-normal%20edge-pattern-solid%20flowchart-link%22%20style%3D%22%3B%22%20data-edge%3D%22true%22%20data-et%3D%22edge%22%20data-id%3D%22L_J_K_0%22%20data-points%3D%22W3sieCI6MTYwLjM4NjYwODEyMzc3OTMsInkiOjcyfSx7IngiOjE2MC4zODY2MDgxMjM3NzkzLCJ5IjoxMTJ9LHsieCI6MjYyLjIwMjg1Nzk3MTE5MTQsInkiOjExMn0seyJ4IjoyNjIuMjAyODU3OTcxMTkxNCwieSI6MTkwfV0%3D%22%20marker-end%3D%22url\(%23mermaid-_r_ia__flowchart-v2-pointEnd\)%22%3E%3C%2Fpath%3E%3Cpath%20d%3D%22M262.2028579711914%2C258L262.2028579711914%2C286%22%20id%3D%22L_K_B_0%22%20class%3D%22edge-thickness-normal%20edge-pattern-solid%20edge-thickness-normal%20edge-pattern-solid%20flowchart-link%22%20style%3D%22%3B%22%20data-edge%3D%22true%22%20data-et%3D%22edge%22%20data-id%3D%22L_K_B_0%22%20data-points%3D%22W3sieCI6MjYyLjIwMjg1Nzk3MTE5MTQsInkiOjI1OH0seyJ4IjoyNjIuMjAyODU3OTcxMTkxNCwieSI6MjkwfV0%3D%22%20marker-end%3D%22url\(%23mermaid-_r_ia__flowchart-v2-pointEnd\)%22%3E%3C%2Fpath%3E%3Cpath%20d%3D%22M194.8225555419922%2C72L194.8225555419922%2C85.21704397470535Q194.8225555419922%2C87%20195.90834197961908%2C88.41421356237309L195.90834197961908%2C88.41421356237309Q196.994128417246%2C89.82842712474618%20198.40834197961908%2C90.91421356237309L198.40834197961908%2C90.91421356237309Q199.8225555419922%2C92%20201.60551156728684%2C92L526.3440199878889%2C92Q528.1269760131836%2C92%20529.5411895755567%2C93.08578643762691L529.5411895755567%2C93.08578643762692Q530.9554031379298%2C94.17157287525382%20532.0411895755567%2C95.58578643762691L532.0411895755567%2C95.58578643762691Q533.1269760131836%2C97%20533.1269760131836%2C98.78295602529465L533.1269760131836%2C228L533.1269760131836%2C286%22%20id%3D%22L_J_L_0%22%20class%3D%22edge-thickness-normal%20edge-pattern-solid%20edge-thickness-normal%20edge-pattern-solid%20flowchart-link%22%20style%3D%22%3B%22%20data-edge%3D%22true%22%20data-et%3D%22edge%22%20data-id%3D%22L_J_L_0%22%20data-points%3D%22W3sieCI6MTk0LjgyMjU1NTU0MTk5MjIsInkiOjcyfSx7IngiOjE5NC44MjI1NTU1NDE5OTIyLCJ5Ijo5Mn0seyJ4Ijo1MzMuMTI2OTc2MDEzMTgzNiwieSI6OTJ9LHsieCI6NTMzLjEyNjk3NjAxMzE4MzYsInkiOjIyOH0seyJ4Ijo1MzMuMTI2OTc2MDEzMTgzNiwieSI6MjkwfV0%3D%22%20marker-end%3D%22url\(%23mermaid-_r_ia__flowchart-v2-pointEnd\)%22%3E%3C%2Fpath%3E%3Cpath%20d%3D%22M533.1269760131836%2C358L533.1269760131836%2C428L533.1269760131836%2C511L533.1269760131836%2C594L533.1269760131836%2C694L533.1269760131836%2C777L533.1269760131836%2C865.2999992370605L533.1269760131836%2C970.5999984741211L533.1269760131836%2C1013.8170424488264Q533.1269760131836%2C1015.5999984741211%20532.0411895755567%2C1017.0142120364942L532.0411895755567%2C1017.0142120364942Q530.9554031379298%2C1018.4284255988673%20529.5411895755567%2C1019.5142120364942L529.5411895755567%2C1019.5142120364942Q528.1269760131836%2C1020.5999984741211%20526.3440199878889%2C1020.5999984741211L407.40661325185715%2C1020.5999984741211Q405.6236572265625%2C1020.5999984741211%20404.2094436641894%2C1021.685784911748L404.2094436641894%2C1021.685784911748Q402.7952301018163%2C1022.7715713493749%20401.7094436641894%2C1024.185784911748L401.7094436641894%2C1024.185784911748Q400.6236572265625%2C1025.599998474121%20400.6236572265625%2C1027.3829544994157L400.6236572265625%2C1030.599998474121%22%20id%3D%22L_L_I_0%22%20class%3D%22edge-thickness-normal%20edge-pattern-solid%20edge-thickness-normal%20edge-pattern-solid%20flowchart-link%22%20style%3D%22%3B%22%20data-edge%3D%22true%22%20data-et%3D%22edge%22%20data-id%3D%22L_L_I_0%22%20data-points%3D%22W3sieCI6NTMzLjEyNjk3NjAxMzE4MzYsInkiOjM1OH0seyJ4Ijo1MzMuMTI2OTc2MDEzMTgzNiwieSI6NDI4fSx7IngiOjUzMy4xMjY5NzYwMTMxODM2LCJ5Ijo1MTF9LHsieCI6NTMzLjEyNjk3NjAxMzE4MzYsInkiOjU5NH0seyJ4Ijo1MzMuMTI2OTc2MDEzMTgzNiwieSI6Njk0fSx7IngiOjUzMy4xMjY5NzYwMTMxODM2LCJ5Ijo3Nzd9LHsieCI6NTMzLjEyNjk3NjAxMzE4MzYsInkiOjg2NS4yOTk5OTkyMzcwNjA1fSx7IngiOjUzMy4xMjY5NzYwMTMxODM2LCJ5Ijo5NzAuNTk5OTk4NDc0MTIxMX0seyJ4Ijo1MzMuMTI2OTc2MDEzMTgzNiwieSI6MTAyMC41OTk5OTg0NzQxMjExfSx7IngiOjQwMC42MjM2NTcyMjY1NjI1LCJ5IjoxMDIwLjU5OTk5ODQ3NDEyMTF9LHsieCI6NDAwLjYyMzY1NzIyNjU2MjUsInkiOjEwMzQuNTk5OTk4NDc0MTIxfV0%3D%22%20marker-end%3D%22url\(%23mermaid-_r_ia__flowchart-v2-pointEnd\)%22%3E%3C%2Fpath%3E%3C%2Fg%3E%3Cg%20class%3D%22edgeLabels%22%3E%3Cg%3E%3Crect%20class%3D%22background%22%20style%3D%22stroke%3A%20none%22%3E%3C%2Frect%3E%3C%2Fg%3E%3Cg%3E%3Crect%20class%3D%22background%22%20style%3D%22stroke%3A%20none%22%3E%3C%2Frect%3E%3C%2Fg%3E%3Cg%3E%3Crect%20class%3D%22background%22%20style%3D%22stroke%3A%20none%22%3E%3C%2Frect%3E%3C%2Fg%3E%3Cg%3E%3Crect%20class%3D%22background%22%20style%3D%22stroke%3A%20none%22%3E%3C%2Frect%3E%3C%2Fg%3E%3Cg%3E%3Crect%20class%3D%22background%22%20style%3D%22stroke%3A%20none%22%3E%3C%2Frect%3E%3C%2Fg%3E%3Cg%3E%3Crect%20class%3D%22background%22%20style%3D%22stroke%3A%20none%22%3E%3C%2Frect%3E%3C%2Fg%3E%3Cg%3E%3Crect%20class%3D%22background%22%20style%3D%22stroke%3A%20none%22%3E%3C%2Frect%3E%3C%2Fg%3E%3Cg%3E%3Crect%20class%3D%22background%22%20style%3D%22stroke%3A%20none%22%3E%3C%2Frect%3E%3C%2Fg%3E%3Cg%20class%3D%22edgeLabel%22%3E%3Cg%20class%3D%22label%22%20data-id%3D%22L_A_B_0%22%20transform%3D%22translate\(0%2C%200\)%22%3E%3Ctext%20y%3D%22-10.1%22%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%22-0.1em%22%20dy%3D%221.1em%22%3E%3C%2Ftspan%3E%3C%2Ftext%3E%3C%2Fg%3E%3C%2Fg%3E%3Cg%20class%3D%22edgeLabel%22%3E%3Cg%20class%3D%22label%22%20data-id%3D%22L_B_C_0%22%20transform%3D%22translate\(0%2C%200\)%22%3E%3Ctext%20y%3D%22-10.1%22%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%22-0.1em%22%20dy%3D%221.1em%22%3E%3C%2Ftspan%3E%3C%2Ftext%3E%3C%2Fg%3E%3C%2Fg%3E%3Cg%20class%3D%22edgeLabel%22%20transform%3D%22translate\(151.73096720377603%2C%20511\)%22%3E%3Cg%20class%3D%22label%22%20data-id%3D%22L_C_D_0%22%20transform%3D%22translate\(-9.046966552734375%2C-7.5\)%22%3E%3Cg%3E%3Crect%20class%3D%22background%22%20style%3D%22%22%20x%3D%22-13%22%20y%3D%22-5.5%22%20width%3D%2244.09393310546875%22%20height%3D%2226%22%3E%3C%2Frect%3E%3Ctext%20y%3D%22-10.1%22%20style%3D%22%22%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%22-0.1em%22%20dy%3D%221.1em%22%3E%3Ctspan%20font-style%3D%22normal%22%20class%3D%22text-inner-tspan%22%20font-weight%3D%22normal%22%3EYes%3C%2Ftspan%3E%3C%2Ftspan%3E%3C%2Ftext%3E%3C%2Fg%3E%3C%2Fg%3E%3C%2Fg%3E%3Cg%20class%3D%22edgeLabel%22%3E%3Cg%20class%3D%22label%22%20data-id%3D%22L_D_E_0%22%20transform%3D%22translate\(0%2C%200\)%22%3E%3Ctext%20y%3D%22-10.1%22%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%22-0.1em%22%20dy%3D%221.1em%22%3E%3C%2Ftspan%3E%3C%2Ftext%3E%3C%2Fg%3E%3C%2Fg%3E%3Cg%20class%3D%22edgeLabel%22%20transform%3D%22translate\(118.66612243652344%2C%20777\)%22%3E%3Cg%20class%3D%22label%22%20data-id%3D%22L_E_F_0%22%20transform%3D%22translate\(-8.946090698242188%2C-7.5\)%22%3E%3Cg%3E%3Crect%20class%3D%22background%22%20style%3D%22%22%20x%3D%22-12%22%20y%3D%22-5.5%22%20width%3D%2241.892181396484375%22%20height%3D%2226%22%3E%3C%2Frect%3E%3Ctext%20y%3D%22-10.1%22%20style%3D%22%22%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%22-0.1em%22%20dy%3D%221.1em%22%3E%3Ctspan%20font-style%3D%22normal%22%20class%3D%22text-inner-tspan%22%20font-weight%3D%22normal%22%3ENo%3C%2Ftspan%3E%3C%2Ftspan%3E%3C%2Ftext%3E%3C%2Fg%3E%3C%2Fg%3E%3C%2Fg%3E%3Cg%20class%3D%22edgeLabel%22%20transform%3D%22translate\(362.7682800292969%2C%20777\)%22%3E%3Cg%20class%3D%22label%22%20data-id%3D%22L_E_G_0%22%20transform%3D%22translate\(-9.046966552734375%2C-7.5\)%22%3E%3Cg%3E%3Crect%20class%3D%22background%22%20style%3D%22%22%20x%3D%22-13%22%20y%3D%22-5.5%22%20width%3D%2244.09393310546875%22%20height%3D%2226%22%3E%3C%2Frect%3E%3Ctext%20y%3D%22-10.1%22%20style%3D%22%22%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%22-0.1em%22%20dy%3D%221.1em%22%3E%3Ctspan%20font-style%3D%22normal%22%20class%3D%22text-inner-tspan%22%20font-weight%3D%22normal%22%3EYes%3C%2Ftspan%3E%3C%2Ftspan%3E%3C%2Ftext%3E%3C%2Fg%3E%3C%2Fg%3E%3C%2Fg%3E%3Cg%20class%3D%22edgeLabel%22%3E%3Cg%20class%3D%22label%22%20data-id%3D%22L_G_H_0%22%20transform%3D%22translate\(0%2C%200\)%22%3E%3Ctext%20y%3D%22-10.1%22%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%22-0.1em%22%20dy%3D%221.1em%22%3E%3C%2Ftspan%3E%3C%2Ftext%3E%3C%2Fg%3E%3C%2Fg%3E%3Cg%20class%3D%22edgeLabel%22%3E%3Cg%20class%3D%22label%22%20data-id%3D%22L_F_I_0%22%20transform%3D%22translate\(0%2C%200\)%22%3E%3Ctext%20y%3D%22-10.1%22%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%22-0.1em%22%20dy%3D%221.1em%22%3E%3C%2Ftspan%3E%3C%2Ftext%3E%3C%2Fg%3E%3C%2Fg%3E%3Cg%20class%3D%22edgeLabel%22%3E%3Cg%20class%3D%22label%22%20data-id%3D%22L_H_I_0%22%20transform%3D%22translate\(0%2C%200\)%22%3E%3Ctext%20y%3D%22-10.1%22%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%22-0.1em%22%20dy%3D%221.1em%22%3E%3C%2Ftspan%3E%3C%2Ftext%3E%3C%2Fg%3E%3C%2Fg%3E%3Cg%20class%3D%22edgeLabel%22%20transform%3D%22translate\(125.8967514038086%2C%20228\)%22%3E%3Cg%20class%3D%22label%22%20data-id%3D%22L_C_J_0%22%20transform%3D%22translate\(-8.946090698242188%2C-7.5\)%22%3E%3Cg%3E%3Crect%20class%3D%22background%22%20style%3D%22%22%20x%3D%22-12%22%20y%3D%22-5.5%22%20width%3D%2241.892181396484375%22%20height%3D%2226%22%3E%3C%2Frect%3E%3Ctext%20y%3D%22-10.1%22%20style%3D%22%22%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%22-0.1em%22%20dy%3D%221.1em%22%3E%3Ctspan%20font-style%3D%22normal%22%20class%3D%22text-inner-tspan%22%20font-weight%3D%22normal%22%3ENo%3C%2Ftspan%3E%3C%2Ftspan%3E%3C%2Ftext%3E%3C%2Fg%3E%3C%2Fg%3E%3C%2Fg%3E%3Cg%20class%3D%22edgeLabel%22%20transform%3D%22translate\(261.7498245239258%2C%20145\)%22%3E%3Cg%20class%3D%22label%22%20data-id%3D%22L_J_K_0%22%20transform%3D%22translate\(-9.046966552734375%2C-7.5\)%22%3E%3Cg%3E%3Crect%20class%3D%22background%22%20style%3D%22%22%20x%3D%22-13%22%20y%3D%22-5.5%22%20width%3D%2244.09393310546875%22%20height%3D%2226%22%3E%3C%2Frect%3E%3Ctext%20y%3D%22-10.1%22%20style%3D%22%22%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%22-0.1em%22%20dy%3D%221.1em%22%3E%3Ctspan%20font-style%3D%22normal%22%20class%3D%22text-inner-tspan%22%20font-weight%3D%22normal%22%3EYes%3C%2Ftspan%3E%3C%2Ftspan%3E%3C%2Ftext%3E%3C%2Fg%3E%3C%2Fg%3E%3C%2Fg%3E%3Cg%20class%3D%22edgeLabel%22%3E%3Cg%20class%3D%22label%22%20data-id%3D%22L_K_B_0%22%20transform%3D%22translate\(0%2C%200\)%22%3E%3Ctext%20y%3D%22-10.1%22%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%22-0.1em%22%20dy%3D%221.1em%22%3E%3C%2Ftspan%3E%3C%2Ftext%3E%3C%2Fg%3E%3C%2Fg%3E%3Cg%20class%3D%22edgeLabel%22%20transform%3D%22translate\(533.0730667114258%2C%20145\)%22%3E%3Cg%20class%3D%22label%22%20data-id%3D%22L_J_L_0%22%20transform%3D%22translate\(-8.946090698242188%2C-7.5\)%22%3E%3Cg%3E%3Crect%20class%3D%22background%22%20style%3D%22%22%20x%3D%22-12%22%20y%3D%22-5.5%22%20width%3D%2241.892181396484375%22%20height%3D%2226%22%3E%3C%2Frect%3E%3Ctext%20y%3D%22-10.1%22%20style%3D%22%22%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%22-0.1em%22%20dy%3D%221.1em%22%3E%3Ctspan%20font-style%3D%22normal%22%20class%3D%22text-inner-tspan%22%20font-weight%3D%22normal%22%3ENo%3C%2Ftspan%3E%3C%2Ftspan%3E%3C%2Ftext%3E%3C%2Fg%3E%3C%2Fg%3E%3C%2Fg%3E%3Cg%20class%3D%22edgeLabel%22%3E%3Cg%20class%3D%22label%22%20data-id%3D%22L_L_I_0%22%20transform%3D%22translate\(0%2C%200\)%22%3E%3Ctext%20y%3D%22-10.1%22%3E%3Ctspan%20class%3D%22text-outer-tspan%22%20x%3D%220%22%20y%3D%22-0.1em%22%20dy%3D%221.1em%22%3E%3C%2Ftspan%3E%3C%2Ftext%3E%3C%2Fg%3E%3C%2Fg%3E%3C%2Fg%3E%3C%2Fsvg%3E)

この状態遷移を仕様書に追加すると、実装とテストの両方がやりやすくなります。

## 7.2 タイムアウトはP0寄りのP1

設計書では、ツール単位のタイムアウトはP1・推奨としています。

agentbridge-design.md

しかし、デスクトップアプリに組み込むなら、タイムアウトはかなり重要です。

例えば、

```
LLM
  → ツール実行
      → ファイルI/O待ち
      → ネットワーク待ち
      → UI待ち
```

のように、ツールが永遠に戻らない可能性があります。

MVPでは、最低限、

C#

```
public sealed record ToolExecutionOptions(
    TimeSpan? Timeout = null);
```

のような設定を用意しておくとよいです。

ただし、強制Abortは行わず、キャンセル要求を出した後に待つという設計は維持すべきです。

# 8. バージョニングと拡張方針

## 8.1 DIMによるインターフェース拡張

C#のデフォルトインターフェースメソッド（DIM）を使う方針は理解できます。

agentbridge-design.md

ただし、DIMには注意点があります。

C#

```
public interface IToolHandler
{
    string ToolName { get; }

    string Category => "General";
}
```

既存実装にコンパイルエラーを起こさず追加できる一方で、以下の点に注意が必要です。

* インターフェースのメンバー追加で、実装クラスの挙動が意図せず変わる可能性。

* DIMの実装が、クラス側の継承構造と絡む場合の挙動。

* AOTやトリミング環境での互換性確認。

特に、将来的に.NET NativeAOTなどで利用する可能性があるなら、公開APIの変更方針をもう少し慎重に決めてもよいと思います。

### 推奨

MVPでは、

* 変更頻度の高い機能はインターフェースではなくオプション型にする。

* 重要な公開インターフェースは、初期段階でできるだけ固定する。

* DIMに頼りすぎない。

という方針を推奨します。

# 9. 設計書に追加したい項目

現状の設計書は、コンポーネント単位の説明は十分です。

一方で、実装に進む前に、以下の章を追加すると完成度がかなり上がります。

## 9.1 追加推奨章

|
優先度

|

章

|

内容

|
| --- | --- | --- |
|

P0

|

会話履歴のデータモデル

|

メッセージ構造、Tool Use、Tool Result

|
|

P0

|

会話ループの状態遷移

|

正常系、エラー、キャンセル

|
|

P0

|

プロバイダ契約

|

イベント、変換、エラー

|
|

P1

|

ツール実行契約

|

並行性、再実行、安全性

|
|

P1

|

エラーコード一覧

|

Core標準コードと利用側拡張

|
|

P1

|

設定モデル

|

MaxHops、Timeout、Retry

|
|

P1

|

テスト戦略

|

単体、統合、プロバイダ別

|
|

P2

|

ログ・診断

|

イベントログ、トレース、機密情報の扱い

|

# 10. MVPの実装順序

AgentBridgeを実際に作るなら、以下の順序がよいと思います。

1. Coreのデータモデルを固定する

   `ChatMessage`、`ContentPart`、`ToolDefinition`、`ToolUsePart`、`ToolResult`、`ConversationState` を実装。

2. 会話履歴とターンロックを実装する

   1つのStateに同時ターンが1つだけ存在することをテスト。

3. FakeProviderでConversationLoopを実装する

   実際のLLM APIに接続せず、固定イベントを返すFakeProviderで正常系・複数ツール・エラー・キャンセルを検証。

4. ToolDispatcherを実装する

   Non-UI並列、UI順次、結果順序、例外変換を検証。

5. Anthropicアダプタを実装する

   ストリーミングイベントと履歴変換、Tool Result変換を実装。

6. OpenAIアダプタを実装する

   Anthropicとの共通部分と差異を確認。

7. WPFアダプタを実装する

   `DispatcherMarshaller` の例外・キャンセル・同一スレッド最適化をテスト。

# 11. 最終判定

## 設計方針は採用してよい

AgentBridgeの方向性は良いです。特に、UIフレームワークやLLM SDKからCoreを切り離す設計、ToolDispatcherの並行制御、キャンセルの明示的な契約は、実用的なライブラリに向いています。

agentbridge-design.md

ただし、次の3つは実装前に仕様を固めるべきです。

1. ConversationStateの履歴所有権・ターンロック。

2. ProviderEventから完全なアシスタントメッセージを再構築する仕組み。

3. ToolResultのプロバイダ変換と、キャンセル・再試行時の整合性。

## 私なら次に修正する箇所

設計書を次のバージョンに更新するなら、最初に 「4.2 会話ループ制御」 と 「4.1 プロバイダ抽象化」 を補強します。

現在の設計はコンポーネントの責務はよく整理されていますが、実装で最もバグが出やすい「ストリーミング → 履歴 → Tool Result → 次のLLM呼び出し」というデータフローがまだ抽象的です。

ここを具体化すれば、AgentBridgeは実装に進めるレベルへかなり近づくと思います。
