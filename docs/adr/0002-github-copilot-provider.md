# ADR 0002: GitHub Copilot SDK を追加の選択肢として認める

- Status: Accepted
- Date: 2026-09-23
- Issue: #26
- 部分的に置き換える: [ADR 0001](0001-agent-framework-host.md) §6「A は本命にしない」

契約の正本は [`docs/design.md`](../design.md)。本 ADR は選定の経緯を残す。

## 1. 背景と目的

ADR 0001 は Microsoft Agent Framework（AF）がツール呼び出しループを持つ構成を本線とし、Copilot SDK は採用しないとした。その後、利用者が GitHub Copilot の契約（組織のライセンス、利用できるモデル）で AgentBridge を使いたいという要望が出た。

本線を変えずに、Copilot を **アプリが選べる追加の実行経路** として足せるかを検討した。

## 2. 前提の確認（2026-09 時点、一次情報）

- AF 公式の `Microsoft.Agents.AI.GitHub.Copilot` がある。Core と同じ 1.20.0 系で、`GitHubCopilotAgent : AIAgent` と `CopilotClient.AsAIAgent(...)` を提供する
- `GitHub.Copilot.SDK` は **ビルド時に Copilot CLI 本体を npm レジストリから取得し、出力の `runtimes/<rid>/native/` に同梱する**（`build/GitHub.Copilot.SDK.targets`）。利用者の PC へ CLI を別途入れる必要はない
  - 閉域網では `CopilotNpmRegistryUrl`（ミラー）または `CopilotCliBinaryPath`（取得済みバイナリ）で差し替える
  - 同梱するのは RID ごとのネイティブ実行ファイル。配布物のサイズが増える
- 認証は `CopilotClientOptions.UseLoggedInUser`（既定。CLI のログイン情報）または `GitHubToken`。利用者に Copilot の利用権が要る
- `CopilotClientMode.Empty` を選ぶと、組み込みのファイル／シェル系ツールを既定で出さない。`BaseDirectory`（`~/.copilot` の代わり）と、セッションごとの `AvailableTools` が必須になる
- `OnPermissionRequest` を渡さないと、ツール実行の許可が保留のまま止まる

## 3. 決定

**Copilot を追加の選択肢として認める。本線は ADR 0001 のまま変えない。**

- 新パッケージ `AgentBridge.GitHubCopilot` を置く。`AgentBridge.Core` は Copilot SDK を参照しない
- アプリは `AgentBridgeHost.Create`（`IChatClient` 経路）か `AgentBridge.GitHubCopilot`（Copilot 経路）のどちらかで `AIAgent` を得る。`AgentBridge.Wpf` の `AgentRunController` は `AIAgent` を受けるので両方で使える
- ツールは同じ `AIFunction` を渡す。UI スレッドへ載せる包み（`UiThreadFunctions.Bind`）も共通
- Copilot 経路の既定は安全側に倒す
  - `CopilotClientMode.Empty` 相当で、CLI 組み込みのファイル／シェル／MCP を出さない
  - 許可の判定は、アプリが登録したツールだけを許可する
  - セッションの保存先はアプリが決める（`BaseDirectory`）

## 4. 本線と違う点（受け入れる）

| 項目 | 本線（AF + `IChatClient`） | Copilot 経路 |
| --- | --- | --- |
| ループの主導権 | AF の `FunctionInvokingChatClient` | Copilot CLI |
| 反復上限 | `MaximumIterationsPerRequest`（既定 10） | CLI 任せ。AgentBridge からは保証しない |
| 同一応答内のツール直列 | 保証する | CLI 任せ。UI 包みしたツールは UI スレッド上で順に走るが、非 UI ツールの直列は保証しない |
| キャンセル後の部分状態 | AF が履歴に載せない | 対応する結果のない tool_use が残り、次の送信が 400 になる報告がある（copilot-cli #3366, #3183）。回避はアプリの責務とし、新しいセッションへ切り替える手順を samples で示す |
| 会話の保存 | しない（メモリ） | CLI が `BaseDirectory` 配下へ独自形式で保存する |
| 外部プロセス | なし | 同梱の CLI を子プロセスで起動する |

## 5. 検討して捨てた案

- **Copilot を本線に替える:** ADR 0001 の理由（コーディング向けのエンジン、CLI プロセスとセッションファイル、orphan バグ）は今も当てはまる。捨てる
- **Core に Copilot 経路を入れる:** Core が特定クラウドの SDK を必須参照しないという契約（design §3.4）に反する。捨てる
- **samples だけで示す:** 安全側の既定（Empty、許可の判定）を各アプリが書き直すことになる。パッケージにまとめる

## 6. 結果

- `docs/design.md` の §1 / §2 / §3.7 を更新し、§3.9 に Copilot 経路の契約を置く
- 実装は #27
- #27 で確かめたこと（SDK 1.0.5 / CLI 1.0.67）
  - ライセンス: CLI 同梱の `LICENSE.md`（GitHub Copilot CLI License）は、改変なしで、アプリの一部として再配布することを認める。単体での配布は不可。配布物にはライセンスの写しを含める
  - 配布サイズ: linux-x64 の CLI 実行ファイルは約 147 MB（取得する tgz は約 122 MB）。RID ごとに 1 つ
  - 取得: `AgentBridge.GitHubCopilot` を参照するだけでビルド時に取得され、参照元の出力へ流れる。閉域網では `CopilotNpmRegistryUrl` または `CopilotCliBinaryPath` を使う。CLI を起動しないテストでは `CopilotSkipCliDownload=true`
  - `GitHubCopilotAgent` は CT の発火で Copilot 側のセッションを破棄し、ツールのハンドラも外す。実行中のツールは協調キャンセルに留まる
  - 許可の判定の戻り値 `PermissionDecision` は SDK 側で評価段階の API（GHCP001）。SDK 更新時に見直す
