# ADR 0001: エージェントループを Agent Framework に任せ、AgentBridge は薄いホストにする

- Status: Accepted
- Date: 2026-09-16
- Issue: #13

契約の正本は [`docs/design.md`](../design.md)。本 ADR は技術選定の比較・誤りの訂正・議論の経緯を残す。

## 1. 背景と目的

自前のデスクトップアプリ（想定は CAE。プリ、ソルバー投入、ポスト）に、クラウド LLM のチャット UI と、名前付き tools による製品操作を組み込みたい。既存の AgentBridge（.NET インプロセス統合、Anthropic/OpenAI SDK 直叩き、`ConversationLoop` 自前）を続けるか、GitHub Copilot SDK や Microsoft Agent Framework などに乗り換えるかを検討した。

当時は Core データモデル（M1）以外のループ実装は未着手で、根本設計の変更が可能な段階だった。

## 2. 検討した選択肢

| # | 選択肢 | 概要 |
| --- | --- | --- |
| A | **Copilot SDK** | GitHub Copilot CLI と同じエージェントランタイムをアプリに組み込む公式 SDK（2026年6月 GA） |
| B | **Microsoft Agent Framework（`AIAgent`）** | Semantic Kernel と AutoGen の後継。既定でツール呼び出しループを内部実行する |
| C | **MEAI `IChatClient`（装飾なし）** | ツールを自動実行しない 1 往復 API。自前ループの下地 |
| D | **当時の AgentBridge 設計** | `ILlmProvider` を各社 SDK 直叩き、`ConversationLoop` / `ToolDispatcher` を Core が持つ |

## 3. 各選択肢（訂正済み）

### 3.1 Copilot SDK

- **アーキテクチャ:** `アプリ → SDK クライアント → JSON-RPC → Copilot CLI（サーバーモード）`。インプロセスの LLM ループではない。
- **公式の想定用途:** VS Code を他エディタへ移植することではない。Copilot **CLI** と同じ実行エンジンを、デスクトップ・SaaS・バックエンド等へ載せる SDK。`mode: "empty"` でファイル系ツールを出さない運用も文書化されている。
- **ストリーミング:** `Streaming = true` で `AssistantMessageDeltaEvent` / `AssistantReasoningDeltaEvent` 等を出す。思考過程の表示は可能。
- **キャンセル:** `AbortAsync()`。`session.idle` の `aborted` でターン中断を表す。セッションは次の `send` に使える、と文書化されている。
- **ループの主導権:** ツール実行後の LLM 再送は CLI が行う。ホストは観察・abort・フック・カスタムツールハンドラを持つ。
- **永続化:** `sessionId` 指定で `~/.copilot/session-state/{sessionId}/` に CLI 内部フォーマットで保存。公開スキーマとしては弱い。resume 時は BYOK 資格情報の再提示が必要。
- **既知の穴:** abort / ハードキル後に orphan の `tool_use`（対応する result なし）が残ると、次の送信が Anthropic 400 でセッションが死ぬ報告がある（copilot-cli #3366, #3183）。
- **製品 DNA:** 計画、ファイル編集、シェル、`workingDirectory`、MCP。CAE の名前付き製品コマンドが主戦場なら過剰。

### 3.2 Microsoft Agent Framework（`AIAgent`）

- **ループ:** 既定の `ChatClientAgent` は `FunctionInvokingChatClient` でツールを内部実行し、最終応答まで回す。
- **ストリーミング:** `RunStreamingAsync()` が `AgentResponseUpdate` を返す（テキスト、推論、`FunctionCallContent` / `FunctionResultContent`）。`RunAsync()` は最終応答を返す API であり、「製品として途中が見えない」ではない。
- **キャンセル:** `CancellationToken`。メンテナ見解（agent-framework #2889）では、キャンセル／失敗時に部分状態をセッションへ書かないのは意図的。`FunctionCall` だけ残すと次が HTTP 400 になるため。
- **並列:** `AllowConcurrentInvocation` の既定は **false**（直列）。並列はオプトイン。
- **承認:** `FunctionApprovalRequestContent` 等。全ツールで必ず発火するかは未検証のまま（必須要件ではない）。
- **永続化:** 既定はラン終了時。オプトインの per-service-call 永続化は対のない `FunctionCall` が残る可能性を文書化している。
- **Anthropic:** MEAI 経由。公式の成熟した Anthropic `IChatClient` は（検討時点）コミュニティ実装が中心で、Azure AI Foundry 経由には既知の不具合報告があった。

### 3.3 MEAI `IChatClient`（装飾なし）

- 生の `GetResponseAsync()` は `FunctionCallContent` を呼び出し元に返すだけで実行しない。`.UseFunctionInvocation()` を挟まなければ、旧 `ILlmProvider` と近い 1 往復になる。
- Agent Framework も Copilot SDK（C#）もこの層の上に乗る。
- 装飾なしで使うことは、自前 `ConversationLoop`（選択肢 D）を MEAI 型で書き直すことに等しい。

### 3.4 当時の AgentBridge 設計（D）

- `ILlmProvider.SendAsync` → `IAsyncEnumerable<ProviderEvent>` の 1 往復。
- `ConversationLoop` が `MaxLlmCalls`、コミット順、リトライガード、キャンセル時の `Cancelled` 合成を規定。
- `ToolDispatcher` が Non-UI 並列 → UI 順次。
- 実装は M1 データモデルまで。ループ本体は未着手だった。

## 4. 当初メモの誤り・過不足（訂正）

検討草稿の比較表について、一次情報で直した点。

1. **「Copilot / AF は会話を保存できない」**  
   誤り。両方とも永続化を持つ。論点は保存フォーマットの所有だった。その後の議論で、アプリが履歴正本を持つ用途自体が無いと判明した。

2. **「最終応答だけ返すので途中表示もキャンセルもできない」**  
   誤り。Copilot SDK は delta / reasoning / abort を持つ。AF は `RunStreamingAsync` と CT を持つ。VS Code エージェントで思考と Stop が見えることと矛盾しない。  
   文書が触れていた `AIAgent.RunAsync()` は非ストリーミング API の説明としては正しいが、製品能力の比較としては不十分だった。

3. **ループ主導権**  
   「途中が見えるか」ではなく、「ツール実行のあと、いつ LLM に戻すかを誰が決めるか」。Copilot は CLI、AF 既定は FICC、D は `ConversationLoop`。

4. **並列度・UI フェーズ分離を自前ループの必須理由にしたこと**  
   過大評価。AF / MEAI の既定は直列。CAE のプリ／ポストは直列の方が安全。UI スレッドへ載せるのは関数呼び出しのデコレータで足り、ループ所有とは独立。

5. **ツール別リトライを業務必須としたこと**  
   過大評価。`MaximumIterationsPerRequest` 等のグローバル上限で最悪は防げる。

6. **Copilot SDK = 他エディタ向け VS Code Copilot**  
   誤り。任意アプリへの CLI エンジン埋め込み。業務 GUI に載せることは可能だが、エンジンの既定世界はコーディングエージェント。

7. **キャンセル時に自前で tool_result 対を揃えないと次の API が壊れる**  
   AF 既定は中断ターンを履歴に載せないことで 400 を避ける。Copilot はセッション継続が建前だが orphan バグがある。VS Code 的 UX は「同じスレッドで次の一文」が主経路であり、`Cancelled` 合成は必須ではない。

## 5. 議論で固定した前提

| 項目 | 決定 |
| --- | --- |
| 対象アプリ | CAE。プリ・ジョブ投入・ポストの **名前付き製品コマンド**。Python API 全面はしない（学習データが無い） |
| ソルバー | 完了待ちしない。`submit` + `status` / `cancel` |
| 操作感 | ストリーム、Stop、同じスレッドで続き。VS Code の Queue / Steer / Stop and Send に近い。チェックポイントやファイル／シェルランタイムは真似ない |
| キャンセル後 | 依頼破棄ではなく続き可。モデルへは完了済みツールとユーザーの次文（必要ならホストが短い注記）。Core は `Cancelled` tool_result を合成しない |
| UI スレッド | 共通ヘルパーで載せる。`RequiresUiThread` 付き自前 `ToolDispatcher` は本線にしない |
| Claude | MVP の第一プロバイダにしない。後で Anthropic SDK を `IChatClient` で包む |
| 履歴正本 | アプリ（プロジェクトファイル等）が持つ用途は今はない。永続契約は置かない |
| 公開型 | MEAI（`ChatMessage`, `AIFunction` 等）をそのまま出す。独自 `ChatMessage` / `IToolHandler` / `ToolRegistry` は捨てる |
| ライブラリ | 残す。ループは AF。Core に `IChatClient` → `ChatClientAgent` の定型（直列、マーシャラ接続）と `AIFunction` 包み |
| チャット UI | ライブラリに含めない。samples で足りる。Wpf はマーシャラと実行状態（busy / キャンセル） |

自前ループに残るメリットは、依存回避、Steer の命令しやすさ、JSON 切断時の合成結果程度で、CAE 本線のコストに見合わない、と判断した。

## 6. 決定

**選択肢 B を採用する。** Microsoft Agent Framework がツール呼び出しループを持つ。AgentBridge はインプロセスの薄いホストライブラリとして、CAE / WPF 向けの定型とマーシャリングだけを提供する。

- **A は本命にしない。** コーディングエンジンであり、CLI プロセスとセッションファイルに引きずられる。CAE コマンドの主戦場と合わない。
- **C 単体は採用しない。** 装飾なし `IChatClient` は、根拠を失った自前ループを自分で書くことになる。MEAI 型は B の公開面として使う。
- **D は廃棄する。** `ConversationLoop` / `ILlmProvider` / `ToolDispatcher` / 独自メッセージモデルは実装しない（M1 の既存型は後続実装で削除）。

## 7. 結果（ Consequences ）

- 実装の前に `docs/design.md` / `docs/roadmap.md` をこの決定へ合わせる（本設計 PR）。
- Core は `Microsoft.Agents.AI` と `Microsoft.Extensions.AI` に依存する。
- `AgentBridge.Anthropic` / `AgentBridge.OpenAI` は MVP で不要。プロバイダは MEAI 公式クライアント。Claude は後続のアダプタ Issue。
- SK → AF のようなエコシステム入れ替えリスクは受け入れる。
- 本 ADR が技術選定の正本である。決定を `.dev/` に置かない。

## 8. 未検証のまま残したもの

必須要件ではないため、決定をブロックしない。

- AF の承認フローが全ツールで必ず発火するか、監査ログとして十分か
- Copilot SDK .NET の pending tool-call を本製品で使う必要（使わない）
- MEAI Anthropic コミュニティ実装の 2026-09 時点のバグ状況（Claude は後続）
