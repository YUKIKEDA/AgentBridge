# AgentBridge ロードマップ

正本。実装の順序と Done 条件をここに置く。詳細な受け入れ条件は GitHub Issue 本文に書く。

関連: [design.md](design.md) / [ADR 0001](adr/0001-agent-framework-host.md) / [CONTRIBUTING.md](../CONTRIBUTING.md)

## 進め方

作業順序（必須）:

```text
（必要なら grill）→ Issue 作成 →（必要なら分解して Issue 更新、必要なら grill）→ ブランチ作成 → 作業 → PR → 人間レビュー・マージ → 繰り返し
```

契約・技術選定など判断が分かれるときは、最初の Issue の前または後で [grill-me](../.cursor/skills/grill-me/SKILL.md) を入れる。詳細は [`CONTRIBUTING.md`](../CONTRIBUTING.md)。ロードマップの箇条書きはバックログ候補であり、**Issue 化されるまで着手しない**。

- 1 Issue ≈ 1 PR（M0 のみまとめ可。その場合も Issue を先に切る）
- Issue タイプ: `feat` / `bug` / `task` / `spike`
- ブランチ: `type/<issue号>-<slug>`
- ローカル検証の正本: ルートの `build.ps1`（対象は `AgentBridge.slnx`。GHA は制限中のため実行前提にしない）。Linux では WPF をビルドできないため `./scripts/verify-linux.sh` を使う（手順は [`setup-agent.md`](setup-agent.md)）

設計契約の変更は実装 Issue より先にマージする。本ロードマップの M2 以降は [ADR 0001](adr/0001-agent-framework-host.md) マージ後の話である。

## M0 — リポジトリ基盤

**Status:** Done（https://github.com/YUKIKEDA/AgentBridge/pull/2 マージ済み）

**Done:** 空の 8 プロジェクトがビルドでき、`build.ps1` がローカルで通る。規約・テンプレ・analyzers。ソリューションは **`.slnx` のみ**。

## M1 — Core データモデル + TurnLease（旧契約。削除済み）

**Status:** 独自型はリポジトリから削除済み。履歴として残すのみ。

## M2 — Agent Framework ホスト（Core）

**Done:** Core が `IChatClient` から直列ツール実行の `ChatClientAgent` を組み立て、UI 包み済み `AIFunction` を渡せる。偽 `IChatClient` でストリーム・ツール直列・キャンセルのテストが緑。

**Issues:**

- https://github.com/YUKIKEDA/AgentBridge/issues/17 — ホスト組み立て、UI 包み、偽クライアント試験（本マイルストーンを 1 Issue にまとめる）

## M3 — WPF マーシャラと実行状態

**Done:** `DispatcherMarshaller` の例外・キャンセル・同一スレッド・Unwrap のテストが緑。busy / キャンセルで `RunStreamingAsync` を 1 本に制限できる。

**Issues:**

- https://github.com/YUKIKEDA/AgentBridge/issues/19 — DispatcherMarshaller、実行状態、契約テスト（本マイルストーンを 1 Issue にまとめる）

## M4 — samples（最小チャット）

**Done:** サンプルが OpenAI 互換 `IChatClient` とダミー／少数ツールでストリーム表示と Stop ができる。見た目はライブラリに含めない。

**Issues:**

- https://github.com/YUKIKEDA/AgentBridge/issues/23 — 最小 WPF チャットと実行手順（本マイルストーンを 1 Issue にまとめる）

## 後続（このロードマップの M 番号は付けない）

- Claude: Anthropic SDK の `IChatClient` アダプタ
- Steer ヘルパー、会話永続、承認 UI、`run_python`（設計 §5.1）

## スコープ外（やらない）

- Copilot SDK をランタイムにすること
- 自前 `ConversationLoop` / `ILlmProvider` / `ToolDispatcher`
- ライブラリ本体の本格チャット UI
- 必須の Undo、コンテキスト要約、動的ツールロード
