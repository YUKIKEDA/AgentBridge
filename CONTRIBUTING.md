# Contributing to AgentBridge

この文書は開発規約の**人間向け正本**です。エージェントのエントリポイントは [`AGENTS.md`](AGENTS.md)、強制ルールは `.cursor/rules/` です。設計の正本は [`docs/design.md`](docs/design.md)、実装順序は [`docs/roadmap.md`](docs/roadmap.md) です。

`.dev/` はレビュー原稿などの**一時資料**置き場です。決定事項を `.dev/` に残さないでください。

## マイルストーン

実装は `docs/roadmap.md` の **M0〜M5** に従います。

| ID  | 概要                                                                  |
| --- | --------------------------------------------------------------------- |
| M0  | リポジトリ基盤（sln / 8 プロジェクト / 規約 / analyzers / build.ps1） |
| M1  | Core データモデル + TurnLease                                         |
| M2  | ConversationLoop + FakeProvider + AssistantTurnBuilder                |
| M3  | ToolDispatcher                                                        |
| M4  | Anthropic → OpenAI（同一 M 内で直列）                                 |
| M5  | WPF（Marshaller 優先）                                                |

## Issue と PR

- **1 Issue ≈ 1 PR**（M0 のみ複数作業をまとめてよい）
- Issue タイプ:
  - **feat** — 利用者に見える能力追加
  - **bug** — 契約違反・不具合修正
  - **task** — 基盤・CI・規約・リファクタ・ドキュメント整備
  - **spike** — 時間boxed の調査（本番コード必須にしない）
- ブランチ名: `type/<issue号>-<slug>`（例: `feat/12-assistant-turn-builder`）
- コミット / PR タイトル: [Conventional Commits](.cursor/rules/conventional-commits.mdc)（type/scope は英語、subject は日本語可）
- 推奨 scope: `core`, `loop`, `dispatcher`, `anthropic`, `openai`, `wpf`, `build`, `ci`, `docs`, `test`
- PR 本文は [`.github/pull_request_template.md`](.github/pull_request_template.md) の見出しを厳密に使用する

### ラベル（推奨）

- `M0` … `M5`
- `type:feat` / `type:bug` / `type:task` / `type:spike`

## 設計変更プロセス

- **契約・公開 API・状態機械・キャンセル／リトライ等**に触れる変更は、先に `docs/design.md` を更新する PR をマージしてから実装 Issue を進める。
- **誤字・表現の明確化・例示のみ**なら、実装 PR に設計 diff を含めてよい。
- 「実装してから設計を後追い」は禁止。

## リポジトリ構成

```text
src/AgentBridge.{Core,Anthropic,OpenAI,Wpf}/
tests/AgentBridge.{Core,Anthropic,OpenAI,Wpf}.Tests/   # 実装と 1:1
samples/                                                 # M5 以降
docs/design.md
docs/roadmap.md
```

- ターゲット: **`net10.0`**（WPF / Wpf.Tests は **`net10.0-windows`**）
- テスト: **xUnit**
- M0 で上記 8 プロジェクトをすべて作成する（中身は空でも可）

## コーディング規約

- `Nullable` enable、`TreatWarningsAsErrors`
- **StyleCop.Analyzers** + 組み込みコード分析
- フォーマット: **`dotnet format`**（`.editorconfig` 準拠）
- XML ドキュメントコメント厳格適用は **public API** 向け。テスト・internal で止めない
- 設定は `Directory.Build.props` と `.editorconfig` に集約し、各 csproj に散らさない

## ローカル検証（正本）

GitHub Actions の workflow はリポジトリに置くが、**利用制限により CI が動かないことがある**。マージ前のゲートはローカルの `build.ps1` とする。

```powershell
./build.ps1
```

想定内容: `dotnet restore` → `dotnet format --verify-no-changes` → `dotnet build` → `dotnet test`（失敗時は非ゼロ終了）。

PR の Verification には、上記を実行した旨を書く。

## エージェント向け

詳細な強制事項は次を参照:

- `.cursor/rules/conventional-commits.mdc`
- `.cursor/rules/pull-requests.mdc`
- `.cursor/rules/engineering.mdc`
- `.cursor/rules/design-docs.mdc`
