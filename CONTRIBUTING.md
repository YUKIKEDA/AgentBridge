# Contributing to AgentBridge

この文書は開発規約の**人間向け正本**です。エージェントのエントリポイントは [`AGENTS.md`](AGENTS.md)、強制ルールは `.cursor/rules/` です。設計の正本は [`docs/design.md`](docs/design.md)、実装順序は [`docs/roadmap.md`](docs/roadmap.md) です。環境構築は人間向け [`docs/setup.md`](docs/setup.md)、エージェント向け [`docs/setup-agent.md`](docs/setup-agent.md) です。

`.dev/` はレビュー原稿などの**一時資料**置き場です。決定事項を `.dev/` に残さないでください。

## 必須ワークフロー（実装の進め方）

コードや規約に手を入れる作業は、**必ず次の順**で進める。Issue なし・ブランチなし・PR なしでの実装着手は禁止。

```text
1. Issue 作成（roadmap の想定タイトルを起点にしてよい）
2. 必要ならタスク分解 → 同じ Issue を更新、または子 Issue を切る
3. ブランチ作成: type/<issue号>-<slug>（Issue 番号必須）
4. 作業（Windows では ./build.ps1。Linux エージェントは docs/setup-agent.md）
5. PR 作成（テンプレ厳守、Related に Closes #N を書く — URL のみは不可）
6. 人間レビュー → マージ
7. 次の Issue へ（繰り返し）
```

- **エージェントは Issue / ブランチ / PR を飛ばして作業ディレクトリに実装を書き始めてはならない。**
- **PR を Issue に GitHub 上で Linked せずに出してはならない**（`Closes #N` / `Fixes #N` / `Resolves #N` を本文に含める。URL だけは不十分）。
- M0 のように複数作業を 1 Issue にまとめる例外は、**先にその旨の Issue を切ったうえで**まとめる。
- ロードマップの「予定 Issue」はバックログ候補であり、**GitHub Issue 化されるまで作業開始シグナルではない。**

## マイルストーン

実装は `docs/roadmap.md` の **M0〜M5** に従います。

| ID | 概要 |
| ---- | ---- |
| M0 | リポジトリ基盤（slnx / 8 プロジェクト / 規約 / analyzers / build.ps1） |
| M1 | Core データモデル + TurnLease |
| M2 | ConversationLoop + FakeProvider + AssistantTurnBuilder |
| M3 | ToolDispatcher |
| M4 | Anthropic → OpenAI（同一 M 内で直列） |
| M5 | WPF（Marshaller 優先） |

## Issue と PR

- **1 Issue ≈ 1 PR**（M0 のみ複数作業をまとめてよい。その場合も Issue は先に作成）
- Issue タイプ:
  - **feat** — 利用者に見える能力追加
  - **bug** — 契約違反・不具合修正
  - **task** — 基盤・CI・規約・リファクタ・ドキュメント整備
  - **spike** — 時間boxed の調査（本番コード必須にしない）
- ブランチ名: `type/<issue号>-<slug>`（例: `feat/12-assistant-turn-builder`）
- コミット / PR タイトル: [Conventional Commits](.cursor/rules/conventional-commits.mdc)（type/scope は英語、subject は日本語可）
- 推奨 scope: `core`, `loop`, `dispatcher`, `anthropic`, `openai`, `wpf`, `build`, `ci`, `docs`, `test`
- PR 本文は [`.github/pull_request_template.md`](.github/pull_request_template.md) の見出しを厳密に使用する
- **Issue の関連付け（必須）:** PR 本文の `## Related` に、GitHub が認識する Closing キーワードを書く。
  - 推奨: **単独行**で `Closes #12`（または `Fixes #12` / `Resolves #12`）
  - 箇条書きの `- Closes #12` や URL だけは**関連付けに失敗することがある**
  - PR 作成後、GitHub UI で Development / Linked issues に Issue が出ていることを確認する（`gh pr view --json closingIssuesReferences` でも可）

### ラベル（推奨）

- `M0` … `M5`
- `type:feat` / `type:bug` / `type:task` / `type:spike`

## 設計変更プロセス

- **契約・公開 API・状態機械・キャンセル／リトライ等**に触れる変更は、先に `docs/design.md` を更新する PR をマージしてから実装 Issue を進める。
- **誤字・表現の明確化・例示のみ**なら、実装 PR に設計 diff を含めてよい。
- 「実装してから設計を後追い」は禁止。

## リポジトリ構成

```text
AgentBridge.slnx
src/AgentBridge.{Core,Anthropic,OpenAI,Wpf}/
tests/AgentBridge.{Core,Anthropic,OpenAI,Wpf}.Tests/   # 実装と 1:1
samples/                                                 # M5 以降
docs/design.md
docs/roadmap.md
```

- ソリューション形式: **`.slnx` のみ**（`.sln` は使わない・置かない）
- ターゲット: **`net10.0`**（WPF / Wpf.Tests は **`net10.0-windows`**）
- テスト: **xUnit**

## コーディング規約

- `Nullable` enable、`TreatWarningsAsErrors`
- **StyleCop.Analyzers** + 組み込みコード分析
- フォーマット: **`dotnet format`**（`.editorconfig` 準拠）
- XML ドキュメントコメント厳格適用は **public API** 向け。テスト・internal で止めない
- 設定は `Directory.Build.props` と `.editorconfig` に集約し、各 csproj に散らさない
- **コメント言語:** XML ドキュメントコメントおよび通常コメントは **日本語で統一**する（識別子・型名・公開 API 名は英語のまま）
- **コメントの句点:** XML ドキュメントコメントおよび通常コメントの文末に **「。」や英語のピリオド `.` を付けない**（例外メッセージなどユーザー向け文言は対象外）。これに合わせ **SA1629 は無効化**する（`.editorconfig`）
- **テストメソッド名:** 日本語で、読みやすい自然文にする。推奨形式は `{対象}_〜すると／したとき〜こと`
  - 例: `FromUser_文字列を指定するとUserロールのテキストメッセージが生成されること`
  - 例: `FromAssistant_複数のパートを指定したとき順序を保持してメッセージが生成されること`
  - 機械的な `前提条件_操作_期待` の羅列は避け、主語・条件・結果が文として通じる名前にする

## ローカル検証（正本）

GitHub Actions の workflow はリポジトリに置くが、**利用制限により CI が動かないことがある**。マージ前のゲートは Windows 上の `build.ps1` とする。

```powershell
./build.ps1
```

想定内容: `dotnet restore` → `dotnet format --verify-no-changes` → `dotnet build` → `dotnet test`（失敗時は非ゼロ終了）。対象は **`AgentBridge.slnx`**。

WPF（`net10.0-windows`）を含むため、このスクリプトは **Windows 専用** です。Linux のエージェントは `./build.ps1` を実行せず、[`docs/setup-agent.md`](docs/setup-agent.md) の `./scripts/verify-linux.sh`（Core / Anthropic / OpenAI のみ）を使います。Linux 検証が通っても、マージ前の正本ゲートは Windows の `./build.ps1` のままです。

PR の Verification には、上記を実行した旨を書く。

## エージェント向け

環境が無い・Linux である場合は、実装の前に [`docs/setup-agent.md`](docs/setup-agent.md) を実行する。セットアップ失敗を理由にセッションを終えてはならない。

詳細な強制事項は次を参照:

- `.cursor/rules/conventional-commits.mdc`
- `.cursor/rules/pull-requests.mdc`
- `.cursor/rules/engineering.mdc`
- `.cursor/rules/design-docs.mdc`
- `.cursor/rules/workflow.mdc`
- `.cursor/rules/setup.mdc`
