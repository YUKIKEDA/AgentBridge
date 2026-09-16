# AgentBridge 設計仕様書

選定の経緯は [ADR 0001](adr/0001-agent-framework-host.md)。本ファイルが契約の正本。

## 1. 概要とスコープ

`AgentBridge` は、クラウド LLM の Tool Use を既存のデスクトップアプリケーション（想定: CAE のプリ・ジョブ投入・ポスト）へ組み込むための、軽量な .NET インプロセスライブラリです。

**エージェントのツール呼び出しループは Microsoft Agent Framework（`AIAgent` / `ChatClientAgent`）が持つ。** AgentBridge はループを再実装しない。公開する会話・ツールの型は Microsoft.Extensions.AI（MEAI）をそのまま使う。

### 提供する機能

- **AF ホストの定型:** `IChatClient` から `ChatClientAgent` を組み立てる（ツール直列実行、マーシャラ接続）
- **UI スレッド:** `IUiThreadMarshaller` と、`AIFunction` を UI スレッド上で実行する包み
- **WPF:** Dispatcher 実装と、1 セッション 1 実行の busy / キャンセル
- **ストリーミングの受け渡し:** `RunStreamingAsync` の `AgentResponseUpdate` をアプリへそのまま出す

### スコープ外

- 自前の `ConversationLoop` / `ILlmProvider` / `ToolDispatcher` / `IToolHandler` / `ToolRegistry`
- 独自 `ChatMessage` / `ToolResult` / `ProviderEvent` を公開契約にすること
- Copilot SDK をランタイムにすること
- ライブラリ内のチャット見た目（samples で示す）
- マルチエージェント協調、長期記憶、プランニング基盤
- ローカル LLM の推論ホスティング
- 必須の承認ダイアログ、Undo / トランザクション、会話の永続化、コンテキスト自動要約
- 製品 Python API を唯一のツールとして渡すこと
- ソルバー完了までツールがブロックすること

### 移行

M1 の独自データモデルと `AgentBridge.Anthropic` / `AgentBridge.OpenAI` は削除済み。Core は AF ホスト実装まで `AssemblyMarker` のみの過渡期とする。

---

## 2. パッケージ構成

```
AgentBridge.Core                 AF ホスト定型、IUiThreadMarshaller、AIFunction の UI 包み
AgentBridge.Wpf                  DispatcherMarshaller、実行状態（busy / キャンセル）
samples/                         最小チャット（ライブラリ本体には含めない）
```

MVP では `AgentBridge.Anthropic` / `AgentBridge.OpenAI` は置かない。プロバイダはアプリが MEAI の `IChatClient` を渡す。Claude は後続で Anthropic SDK を `IChatClient` に適応する。

> アプリ固有のツール（境界条件、ジョブ投入、コンター等）は AgentBridge に含めず、利用側プロジェクトに置く。

---

## 3. 公開 API

識別子は実装時に微調整してよいが、責務と契約は変えない。

### 3.1 型の所有

| 領域 | 使う型 |
| :-- | :-- |
| メッセージ・ツール | MEAI（`ChatMessage`, `AIFunction`, `AIFunctionFactory`, `AITool`, `FunctionCallContent` 等） |
| エージェント実行 | Agent Framework（`AIAgent`, `ChatClientAgent`, `AgentSession`, `AgentResponseUpdate`, `AgentRunOptions`） |
| AgentBridge 独自 | `IUiThreadMarshaller`、ホスト組み立て、`AIFunction` の UI 包み、WPF の実行状態 |

アプリが MEAI / AF の型を直接参照してよい。ファサードで隠さない。

### 3.2 UI スレッドマーシャラ（`IUiThreadMarshaller`）

```csharp
public interface IUiThreadMarshaller
{
    bool IsOnUiThread { get; }

    Task<T> InvokeAsync<T>(Func<T> action, CancellationToken ct = default);
    Task<T> InvokeAsync<T>(Func<Task<T>> asyncAction, CancellationToken ct = default);
    Task InvokeAsync(Action action, CancellationToken ct = default);
}
```

- `InvokeAsync<T>(Func<Task<T>>)` は内側の `Task<T>` をアンラップする
- 例外は呼び出し元へ再スローする
- 既に UI スレッド上ならマーシャリングをバイパスする
- Dispatcher 投入 **前** に CT が発火していれば投入しない。実行開始後は CT を伝播するのみ（強制 Abort しない）

プリ／ポストなどドキュメントを触るツールは、このマーシャラ経由で UI スレッドに載せる。ループは UI を知らない。

### 3.3 `AIFunction` の UI 包み

`AIFunctionFactory.Create` で作った関数を、`IUiThreadMarshaller` 付きで包み、`Invoke` が UI スレッド上で走ることを保証する。包んでいない関数は任意スレッドで走ってよい（ジョブ状態照会など）。

同一応答内の複数ツールは **直列**（`AllowConcurrentInvocation = false`）。CAE のドキュメント操作を並列にしない。オプトイン並列は将来の拡張であり、MVP の公開契約にしない。

### 3.4 AF ホストの定型（Core）

`IChatClient` とツール一覧から `ChatClientAgent`（`AIAgent`）を返す。

必ず行うこと:

1. Agent Framework 既定どおり function invocation を有効にする（ツールループは AF）
2. 同一応答内のツールは直列
3. 指定があれば、UI 包み済みツールを渡す
4. 反復上限は AF の `MaximumIterationsPerRequest`（または同等）へマップする。既定は設計上 **10** 相当。`0` 以下は不正

行わないこと:

- 独自の履歴コミット状態機械
- `Cancelled` / `RETRY_LIMIT_EXCEEDED` / `JSON_PARSE_ERROR` の合成 tool_result
- Non-UI 並列フェーズ

プロバイダ実装（OpenAI、Azure OpenAI 等）はアプリが `IChatClient` として渡す。Core は特定クラウド SDK を必須参照にしない（MEAI / AF 抽象と、テスト用の偽 `IChatClient` のみ）。

### 3.5 実行とキャンセル（Wpf + アプリ）

- 実行は `AIAgent.RunStreamingAsync(..., cancellationToken)` を正とする
- Stop は CT を発火する。実行中ツールは協調キャンセル（トークンを無視した処理は完了し得る）
- 同一 `AgentSession` に対する同時実行は 1 つ。Wpf は `IsBusy` とキャンセル手段を提供する
- 中断後の続きは、新しいユーザー `ChatMessage` を同じセッションへ送る（VS Code の Stop and Send に相当）
- Steer（今のツールだけ終えてから次指示）は MVP の必須 API にしない。アプリが「完了を待つ」か「すぐ CT」かを選べばよい
- 画面上の途中テキストを残すかは View の責務。Core は吹き出しを持たない

AF 既定では、キャンセル／失敗したランの部分状態をセッションへ書かない。次の送信が対のない `FunctionCall` で 400 にならないことを、その方針に委ねる。ホストが副作用ログを次メッセージへ注記することはアプリの任意。

### 3.6 ツール設計（アプリ契約。Core 型は増やさない）

- **粒度:** 製品コマンド単位（境界条件、メッシュ操作、ジョブ投入、コンター等）
- **ソルバー:** `submit_*` はジョブ ID をすぐ返す。完了待ちしない。状態照会とキャンセルは別ツール（またはアプリ UI）
- **失敗:** 短い結果文字列で LLM の自己修正を促す（例: 要素が見つからない）。秘密情報・スタックは載せない
- **Python 実行:** MVP の主ツールにしない。後で承認付きの 1 本として足してよい

### 3.7 プロバイダ

- **MVP:** OpenAI 互換（Azure OpenAI を含む）の公式 MEAI `IChatClient`
- **Claude:** 第一プロバイダにしない。必要になったら Anthropic 公式 SDK を `IChatClient` で包む Issue を切る
- Copilot SDK は採用しない

### 3.8 会話の永続化

Core は永続化しない。起動中は AF の `AgentSession`（メモリ）で足りる。プロジェクトファイルへ会話を残す要件は今はない。

---

## 4. WPF

- `DispatcherMarshaller` が `IUiThreadMarshaller` を実装する（§3.2）
- 実行状態ヘルパー: 開始、`IsBusy`、キャンセル、ストリーム列挙の購読口。チャットの見た目（ListBox、Markdown 描画等）は samples または製品側

---

## 5. バージョニング

- 残すパッケージ（Core / Wpf）はロックステップ版付け
- M1 独自型の削除は本設計に従う破壊的整理であり、初の実装系メジャーとして扱ってよい
- 公開拡張ポイントへのメンバー追加は DIM を優先

### 5.1 将来課題（MVP 必須ではない）

| 項目 | 方針 |
| :-- | :-- |
| Claude `IChatClient` | 後続 Issue |
| Steer（ツール完了待ちのあと次指示） | アプリまたは薄いヘルパー |
| 会話の DB / ファイル保存 | アプリ。`ChatHistoryProvider` を使うならアプリが実装 |
| ツール承認 UI | 必須にしない |
| Undo | Core 化しない |
| `run_python` | 承認付きの逃げ道として後続 |
| 並列ツール | オプトイン。ドキュメント操作は直列のまま |

---

## 6. 実行の流れ（契約まとめ）

```
アプリが IChatClient と AIFunction[] を用意
  → Core が ChatClientAgent を組み立て（直列 invocation、UI 包み）
  → Wpf が CT 付きで RunStreamingAsync
  → AgentResponseUpdate をサンプル / 製品 UI が描画
  → ツールは AF が実行（UI ツールはマーシャラ経由）
  → Stop: CT
  → 続き: 同じ AgentSession へ次のユーザーメッセージ
```
