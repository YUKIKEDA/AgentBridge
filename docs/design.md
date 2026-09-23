# AgentBridge 設計仕様書

選定の経緯は [ADR 0001](adr/0001-agent-framework-host.md) と [ADR 0002](adr/0002-github-copilot-provider.md)。本ファイルが契約の正本。

## 1. 概要とスコープ

`AgentBridge` は、クラウド LLM の Tool Use を既存のデスクトップアプリケーション（想定: CAE のプリ・ジョブ投入・ポスト）へ組み込むための、軽量な .NET インプロセスライブラリです。

**エージェントのツール呼び出しループは Microsoft Agent Framework（`AIAgent` / `ChatClientAgent`）が持つ。** AgentBridge はループを再実装しない。公開する会話・ツールの型は Microsoft.Extensions.AI（MEAI）をそのまま使う。

### 提供する機能

- **AF ホストの定型:** `IChatClient` から `ChatClientAgent` を組み立てる（ツール直列実行、マーシャラ接続）
- **UI スレッド:** `IUiThreadMarshaller` と、`AIFunction` を UI スレッド上で実行する包み
- **WPF:** Dispatcher 実装と、1 セッション 1 実行の busy / キャンセル
- **ストリーミングの受け渡し:** `RunStreamingAsync` の `AgentResponseUpdate` をアプリへそのまま出す
- **GitHub Copilot（任意）:** Copilot SDK を `AIAgent` として得る追加の経路（§3.9）

### スコープ外

- 自前の `ConversationLoop` / `ILlmProvider` / `ToolDispatcher` / `IToolHandler` / `ToolRegistry`
- 独自 `ChatMessage` / `ToolResult` / `ProviderEvent` を公開契約にすること
- Copilot SDK を本線（既定のランタイム）にすること。追加の選択肢としては §3.9 で認める
- ライブラリ内のチャット見た目（samples で示す）
- マルチエージェント協調、長期記憶、プランニング基盤
- ローカル LLM の推論ホスティング
- 必須の承認ダイアログ、Undo / トランザクション、会話の永続化、コンテキスト自動要約
- 製品 Python API を唯一のツールとして渡すこと
- ソルバー完了までツールがブロックすること

### 移行

M1 の独自データモデルと `AgentBridge.Anthropic` / `AgentBridge.OpenAI` は削除済み。Core のホスト定型は `AgentBridgeHost` / `UiThreadFunctions`。

---

## 2. パッケージ構成

```
AgentBridge.Core                 AF ホスト定型、IUiThreadMarshaller、AIFunction の UI 包み
AgentBridge.Wpf                  DispatcherMarshaller、実行状態（busy / キャンセル）
AgentBridge.GitHubCopilot        Copilot SDK を AIAgent として組み立てる任意の経路（§3.9）
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

`UiThreadFunctions.Bind` が `AIFunctionFactory.Create` で作った関数を `IUiThreadMarshaller` 付きで包み、`Invoke` が UI スレッド上で走ることを保証する。包んでいない関数は任意スレッドで走ってよい（ジョブ状態照会など）。

同一応答内の複数ツールは **直列**（`AllowConcurrentInvocation = false`）。CAE のドキュメント操作を並列にしない。オプトイン並列は将来の拡張であり、MVP の公開契約にしない。

### 3.4 AF ホストの定型（Core）

`AgentBridgeHost.Create(IChatClient, tools, marshaller?, options?)` が `ChatClientAgent`（`AIAgent`）を返す。`options.MaximumIterationsPerRequest` の既定は 10。

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
- **GitHub Copilot:** 本線にはしない。アプリが選べる追加の経路として `AgentBridge.GitHubCopilot` を置く（§3.9、[ADR 0002](adr/0002-github-copilot-provider.md)）

### 3.8 会話の永続化

Core は永続化しない。起動中は AF の `AgentSession`（メモリ）で足りる。プロジェクトファイルへ会話を残す要件は今はない。

### 3.9 GitHub Copilot 経路（`AgentBridge.GitHubCopilot`、任意）

アプリが GitHub Copilot の契約でモデルを使いたいときの追加の経路。本線（§3.4）は変えない。

- 依存は `Microsoft.Agents.AI.GitHub.Copilot`（AF 公式、Core と同じ AF 版）。Core はこのパッケージも Copilot SDK も参照しない
- 返す型は `AIAgent`（`GitHubCopilotAgent`）。Wpf の `AgentRunController` と `RunStreamingAsync` / CT の扱いは §3.5 と同じ
- ツールは本線と同じ `AIFunction`。`marshaller` 指定時は `UiThreadFunctions.Bind` で包んでから渡す
- Copilot CLI は `GitHub.Copilot.SDK` がビルド時に取得して出力へ同梱する。アプリの利用者が別途入れる必要はない。閉域網ではアプリが取得元を差し替える
- 認証はアプリが選ぶ（ログイン済みユーザー、または GitHub トークン）。AgentBridge は資格情報を保存しない

公開 API:

- `GitHubCopilotHost.CreateClientOptions(baseDirectory)` が `CopilotClientMode.Empty` と保存先を設定した `CopilotClientOptions` を返す。アプリは認証などを足して `CopilotClient` を作る
- `GitHubCopilotHost.Create(client, tools, marshaller?, options?)` が `GitHubCopilotAgent` を返す。`options` は名前、説明、指示（Copilot 既定のシステムメッセージへ追記）、モデル、クライアントの所有
- `GitHubCopilotHost.CreateSessionConfig(tools, marshaller?, options?)` は `Create` が使うセッション設定。アプリが設定を足して `GitHubCopilotAgent` を直接作るときに使ってよい
- ツールは `AIFunction` に限る。`AIFunction` でないもの、承認付き（`ApprovalRequiredAIFunction`）、名前の重複は `ArgumentException`。承認 UI が無いまま許可の判定で素通しにしないため

必ず行うこと（既定）:

1. CLI 組み込みのファイル／シェル／MCP ツールを出さない（`CopilotClientMode.Empty` 相当）。モデルに見えるのはアプリが渡したツールだけ
2. ツール実行の許可は、アプリが渡したツール名だけを許可し、それ以外は拒否する
3. セッションの保存先（`BaseDirectory`）はアプリが指定する

保証しないこと（本線との差。[ADR 0002](adr/0002-github-copilot-provider.md) §4）:

- 反復上限（`MaximumIterationsPerRequest`）と、非 UI ツールの直列実行。ループは Copilot CLI が持つ
- キャンセル後に次の送信が通ること。失敗したら新しいセッションへ切り替える手順を samples で示す
- 会話保存の形式。CLI の内部形式であり公開契約にしない

---

## 4. WPF

- `DispatcherMarshaller` が `IUiThreadMarshaller` を実装する（§3.2）
- `AgentRunController` が開始、`IsBusy`、キャンセル、`RunStreamingAsync` の列挙口を提供する。同一 `AgentSession` の同時実行は拒否する。チャットの見た目（ListBox、Markdown 描画等）は samples または製品側

---

## 5. バージョニング

- 残すパッケージ（Core / Wpf / GitHubCopilot）はロックステップ版付け
- M1 独自型の削除は本設計に従う破壊的整理であり、初の実装系メジャーとして扱ってよい
- 公開拡張ポイントへのメンバー追加は DIM を優先

### 5.1 将来課題（MVP 必須ではない）

| 項目 | 方針 |
| :-- | :-- |
| Claude `IChatClient` | 後続 Issue |
| GitHub Copilot 経路 | §3.9。任意パッケージ |
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
