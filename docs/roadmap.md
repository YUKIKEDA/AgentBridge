# AgentBridge ロードマップ

正本。実装の順序と Done 条件をここに置く。詳細な受け入れ条件は GitHub Issue 本文に書く。

関連: [design.md](design.md) / [CONTRIBUTING.md](../CONTRIBUTING.md)

## 進め方

- 1 Issue ≈ 1 PR（M0 のみまとめ可）
- Issue タイプ: `feat` / `bug` / `task` / `spike`
- ブランチ: `type/<issue号>-<slug>`
- ローカル検証の正本: ルートの `build.ps1`（GHA は定義済みだが利用制限中のため実行前提にしない）

## M0 — リポジトリ基盤

**Done:** 空の 8 プロジェクトがビルドでき、`build.ps1`（format verify → build → test）がローカルで通る。規約・テンプレ・analyzers が入っている。

**Issues（予定）:**

- `task(build):` ソリューションと src/tests 対称の 8 プロジェクトを作成（net10.0 / Wpf は net10.0-windows）
- `task(build):` Directory.Build.props + .editorconfig + StyleCop + TreatWarningsAsErrors
- `task(ci):` build.ps1 と GitHub Actions workflow 定義（実行は制限解除後）
- `task(docs):` CONTRIBUTING / Issue・PR テンプレの最終調整（必要なら）

## M1 — Core データモデル + TurnLease

**Done:** メッセージモデル・ToolResult・ConversationState/TurnLease の単体テストが緑（排他・履歴書き換え不可）。

**Issues（予定）:**

- `feat(core):` ChatMessage / ContentPart / ToolDefinition / ToolUsePart
- `feat(core):` ToolResult（Status 契約・LlmContent 切り詰め）
- `feat(core):` ConversationState + ConversationTurnLease
- `test(core):` ターン排他と lease 解放

## M2 — ConversationLoop + FakeProvider + AssistantTurnBuilder

**Done:** FakeProvider で正常系・MaxLlmCalls・キャンセル・リトライガード・ParseFailed のテストが緑。

**Issues（予定）:**

- `feat(loop):` AssistantTurnBuilder（TurnComplete でのみコミット）
- `feat(loop):` ConversationLoop 本体と ConversationTurnResult
- `feat(loop):` MaxLlmCalls 打ち切り
- `feat(loop):` リトライ上限ガード（Dispatcher 非経由）
- `test(loop):` キャンセル契約（複数ツール・UI TurnCancelled）

## M3 — ToolDispatcher

**Done:** フェーズ分離・並列度・JsonElement Clone・例外変換のテストが緑。

**Issues（予定）:**

- `feat(dispatcher):` Phase1 Non-UI / Phase2 UI
- `feat(dispatcher):` MaxDegreeOfParallelism と input.Clone()
- `feat(dispatcher):` 未捕捉例外 → UNHANDLED_EXCEPTION
- `test(dispatcher):` 結果順序と失敗の独立性

## M4 — プロバイダアダプタ（Anthropic → OpenAI）

**Done:** 両アダプタで終端プロトコル・ToolResult 変換の契約テスト（または同等の検証）が緑。同一マイルストーン内で **Anthropic 完了後に OpenAI** を直列で進める。

**Issues（予定）:**

- `feat(anthropic):` ストリーミング → ProviderEvent / AssistantTurn 連携
- `feat(anthropic):` tool_result 一括変換
- `feat(openai):` ストリーミングと role:tool 変換
- `test(anthropic):` / `test(openai):` 終端イベントと異常順序

## M5 — WPF アダプタ

**Done:** DispatcherMarshaller の例外・キャンセル・同一スレッド・Unwrap のテストが緑。チャット UI は最小で可。

**Issues（予定）:**

- `feat(wpf):` DispatcherMarshaller（IUiThreadMarshaller）
- `test(wpf):` マーシャラ契約テスト
- `feat(wpf):` 最小チャット UI（任意・薄い）

## スコープ外（このロードマップではやらない）

- 必須のツール承認 UI、Undo 基盤、Context pruning、動的ツールロード（設計 §5.1）
