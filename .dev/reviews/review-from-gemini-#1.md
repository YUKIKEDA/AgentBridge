『AgentBridge 設計仕様書』に対するアーキテクチャ・設計レビューを提示します。

---

# AgentBridge 設計レビュー報告書

## 1. 総括（Executive Summary）

デスクトップアプリ（特に WPF 等の STA スレッドモデルを持つ環境）と LLM の Tool Use を統合するライブラリとして、**課題のツボを的確に押さえた非常に筋の良い設計**です。

特に以下の点は現場での知見が強く反映されており高く評価できます。
- **UI スレッド境界の明示化 (`RequiresUiThread` / `IUiThreadMarshaller`)**
- **ステートレスな `ConversationLoop` によるタブ・並行会話の容易なサポート**
- **ストリーミング時のツール引数バッファリング（UI のちらつき防止）**
- **ツール失敗を例外で落とさず、LLM に状況をフィードバックして自己修復を促す設計**

一方で、実際の運用・実装フェーズで**デッドロック、データの不整合、無限ループ**に繋がりかねないエッジケースや、API 設計の曖昧さがいくつか見受けられます。以下に改善提案とリスク対策をまとめます。

---

## 2. 重要な設計課題と改善提案（Major Findings）

### ① UIツールと Non-UIツールの並行実行によるレースコンディション
**【現状の設計】** (4.3 節)
```csharp
var nonUiTask = Task.WhenAll(nonUiCalls.Select(c => ExecuteOneAsync(c, ct)));
foreach (var c in uiCalls)
    uiResults.Add(await ExecuteOneAsync(c, ct));
var nonUiResults = await nonUiTask;
```

**【懸念点】**
`nonUiCalls`（並列）と `uiCalls`（直列）が**同時に走る**設計になっています。デスクトップアプリにおいて、Non-UI ツールがバックグラウンドでドキュメントやメモリ上のデータモデルを読み書きし、同時に UI ツールが同一のオブジェクトを描画・更新した場合、**オブジェクトの競合・不整合・`InvalidOperationException`（コレクション変更中の列挙など）** が発生します。

**【改善策】**
並行ポリシーを選べるようにするか、デフォルトでは安全側に倒すことを推奨します。
1. **フェーズ分離（推奨）**:
   - 原則として「Non-UI ツール群の完了を待ってから UI ツール群を実行する」、あるいはその逆のパイプラインにする。
2. **完全直列化モード**:
   - `ToolDispatcherOptions.ExecutionMode` などを設け、安全重視（完全直列）とスループット重視（現在のUI/Non-UI並行）を切り替え可能にする。

---

### ② リトライポリシー（`IRetryPolicy`）の適用レイヤーと打ち切り挙動の曖昧さ
**【現状の設計】** (3.3 節, 4.4 節)
- 「同一ターン・同一ツール 3 回失敗で打ち切り」
- 用語「ターン」が「ユーザー入力から最終応答までの一連の流れ」と定義されている。

**【懸念点】**
1. **どのレイヤーでリトライするのか**:
   - *パターン A (Dispatcher 内部)*: LLM を介さず、ハンドラを即座に再実行する（ネットワーク瞬断など）。
   - *パターン B (ConversationLoop 内部)*: 失敗結果（`is_error: true`）を LLM に戻し、LLM が「再度そのツールを呼んできた」回数をカウントする。
   仕様書 4.4 節の「候補の再検索など自己修正を促す」という記述からパターン B を意図していると推測されますが、この場合 `attemptCount` のカウント主体は `ToolDispatcher` ではなく `ConversationLoop` になります。
2. **打ち切り時の挙動**:
   - 3 回失敗して `ShouldRetry == false` となったとき、システムはどう振る舞うべきか明記されていません。
     - (a) そのツールを LLM への提示ツール一覧（`tools`）から一時的に除外する？
     - (b) 「このツールは上限に達したため使用できません」というシステムメッセージを注入して LLM に最終回答を促す？
     - (c) ループを中断して例外をスローする？
   - (b) のように LLM へコンテキストとして制限を伝えないと、LLM は 4 回目も同じツールを呼び続け、永久に脱出できなくなります。

**【改善策】**
- `ConversationLoop` の「最大往復回数（MaxHops/MaxSteps）」の安全弁（例: 最大 10 往復）を明記する。
- リトライ上限到達時は、エラー結果とともに「*このツールは試行上限に達しました。他の手段を試すか、ユーザーに状況を説明してください*」というメタ指示をプロバイダ層で付与する仕様にすることを推奨します。

---

### ③ 巻き添えキャンセル抑止と未捕捉例外のフェイルセーフ
**【現状の設計】** (4.3 節 2)
- 「あるツールの実行が失敗しても、並行して走っている他のツール呼び出しは中断しません」

**【懸念点】**
ハンドラ実装者が `Task<ToolResult>` の中で予期せぬ例外（`NullReferenceException` や OOM、WPF の UI 要素アクセス拒否など）をスローした場合、`Task.WhenAll` が Faulted になり、他のタスクが未完了のまま全体のディスパッチが吹き飛ぶリスクがあります。

**【改善策】**
`ToolDispatcher.ExecuteOneAsync` の内部でハンドラ実行を必ず try-catch で保護し、未捕捉例外を安全に `ToolResult.Failed(code: "UNHANDLED_EXCEPTION", message: ex.Message)` に変換する規約を明文化してください。

---

### ④ `JsonSchema` の型定義と開発者体験（DX）
**【現状の設計】** (3.1 節)
```csharp
JsonSchema InputSchema { get; }
Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct);
```

**【懸念点】**
1. **型の出所**: .NET 標準には単体の `JsonSchema` クラスは（現行のメジャーバージョンでは）標準提供されておらず、サードパーティライブラリ（`JsonSchema.Net` など）に依存するか、独自クラスか、`JsonObject` で代用するかが問われます。
2. **生の `JsonElement` 操作の煩雑さ**:
   利用側アプリ開発者がツールを作るたびに、`input.GetProperty("id").GetString()` のようなパースと検証のボイラープレートを書く必要があり、バグの温床になります。

**【改善策】**
型安全なジェネリック基底クラスを提供することを強く推奨します。
```csharp
// 利用側開発者が実装しやすい基底クラス
public abstract class ToolHandlerBase<TInput> : IToolHandler where TInput : class
{
    public abstract string ToolName { get; }
    public abstract string Description { get; }

    // リフレクション/ソースジェネレータで TInput から自動生成可能にする
    public virtual JsonSchema InputSchema => JsonSchemaGenerator.FromType<TInput>();

    public async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        TInput? parsed;
        try
        {
            parsed = input.Deserialize<TInput>(AgentBridgeJsonOptions.Default);
            if (parsed == null) return ToolResult.InvalidInput("Input deserialized to null.");
        }
        catch (JsonException ex)
        {
            return ToolResult.InvalidInput($"JSON parse error: {ex.Message}");
        }
        return await ExecuteAsync(parsed, ct);
    }

    protected abstract Task<ToolResult> ExecuteAsync(TInput input, CancellationToken ct);
}
```

---

## 3. 実装・運用上の推奨事項（Minor Recommendations）

### 1. `IUiThreadMarshaller` のアンラップ実装
WPF の `Dispatcher.InvokeAsync` に非同期ラムダ（`Func<Task<T>>`）を渡すと、戻り値が `DispatcherOperation<Task<T>>` になり、二重の await またはアンラップ（`.Unwrap()`）が必要です。
4.5 節で `TaskCompletionSource<T>` を使う方針が示されているのは適切ですが、以下の実装パターンを意識しておくと安全です。

```csharp
// 実装例イメージ
public async Task<T> InvokeAsync<T>(Func<Task<T>> asyncAction, CancellationToken ct = default)
{
    if (IsOnUiThread) return await asyncAction();

    var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

    await _dispatcher.InvokeAsync(async () =>
    {
        try { tcs.TrySetResult(await asyncAction()); }
        catch (Exception ex) { tcs.TrySetException(ex); }
    });

    return await tcs.Task;
}
```

### 2. キャンセル時のドキュメント破損防止（Graceful Rollback）
4.2 節で「処理を中断するか安全に完了させるかは各ツールの実装判断に委ねる」とありますが、デスクトップアプリの場合、中途半端な状態で処理が中断されるとドキュメントの Undo スタックやローカル DB が不整合を起こします。
- ハンドラ向けに `IUndoableToolHandler` などのインターフェース、またはトランザクションスコープを意識したガイドラインをドキュメント化することを推奨します。

### 3. トークン上限・コンテキスト溢れ（Context Overflow）の考慮
会話履歴（`ConversationState`）が肥大化し、LLM の Context Window を超えた場合の戦略がスコープ外となっています。
- 包括的な長期記憶機能はスコープ外で良いですが、「古い `tool_result` の画像や長大テキストを要約/切り詰める（Pruning）」フックが `ConversationState` に備わっていないと、デスクトップアプリを数時間起動し続けた際に容易に API エラー（400 Bad Request）で停止します。

---

## 4. レビューまとめ

| 評価項目 | 評価 | コメント |
| :--- | :---: | :--- |
| **責務の分離 (Separation of Concerns)** | **優** | UI / コア / SDK 実装がクリーンに分離されている |
| **スレッドモデルの考慮** | **良** | WPF/STA の特性を考慮しているが、UI/Non-UI 並行実行時の競合リスクの対策が必要 |
| **エラーハンドリング設計** | **良** | ツールエラーを LLM に戻すアプローチは優秀。リトライ打ち切り時の動作定義を補強したい |
| **開発者体験 (DX)** | **可** | `JsonElement` 直扱いはボイラープレートが増えるため、型安全な抽象クラスを提供すべき |

本設計仕様書は、上述した**「UI/Non-UI 並行実行時の分離ポリシー」「リトライ上限時の挙動定義」「型安全なハンドラ基底クラスの提供」**の 3 点を仕様書に追記・修正することで、本番プロダクトに耐えうる極めて堅牢な設計になると判断します。