全体として非常によく練られた実践的な設計仕様書です。特に以下の点は、LLM を組み込むデスクトップアプリケーション（WPF 等）の現場の課題を深く理解して設計されています。

- **UI スレッド分離の規約化**: `RequiresUiThread` とフェーズ分離（Non-UI 並列 → UI 順次）によるデッドロック・STA 競合の抑止
- **堅牢な排他制御**: `ConversationState` と `TurnLease` によるステートフル履歴・ステートレス実行の分離
- **厳密なキャンセル・エラー境界**: 未完了アシスタントメッセージを履歴に残さない契約や、`ToolResult` の UI 向け/LLM 向け（秘密情報・スタックトレース分離）の切り分け
- **無限ループ抑止の二重防御**: メタ指示だけに依存せず、`IRetryPolicy` 超過時にハンドラを実行させない水際ガード

この完成度を前提とした上で、**実装フェーズでトラブルになりやすい境界条件や API の詰めが必要なポイント**をレビューします。

---

## 1. 重点検討事項（アーキテクチャ・契約の明確化）

### ① `JsonElement` のライフタイムと所有権
- **懸念**:
  `IToolHandler.ExecuteAsync(JsonElement input, ...)` に渡す `JsonElement` は struct であり、そのバッキングストアである `JsonDocument` に依存します。
  もしプロバイダアダプタが `using var doc = JsonDocument.Parse(...)` で生成した `RootElement` をそのまま渡すと、ハンドラ内で非同期処理（`await`）を跨いだ際に `JsonDocument` が既に Dispose され、`ObjectDisposedException` が発生するリスクがあります。
- **推奨策**:
  - `JsonElement.Clone()` を呼んでメモリをハンドラ用に独立させる（プロバイダまたは `ToolDispatcher` 側の責務とする）。
  - または、契約として「`ExecuteAsync` に渡される `JsonElement` は呼び出し元のスコープ終了後も安全に読み取り可能である（クローン済み）」ことを仕様書に明記する。

### ② `ToolDispatcher` と `ConversationLoop` の責務境界（リトライ状態の引き渡し）
- **懸念**:
  §3.3 では「カウント主体は `ConversationLoop`（`ToolDispatcher` ではない）」と規定されていますが、§4.3 では `ToolDispatcher.ExecuteAllAsync` の第 1 ステップとして「リトライ上限到達ツールはハンドラ未実行で RETRY_LIMIT_EXCEEDED を返す」と書かれています。
  `ToolDispatcher` 自体がステートレスである場合、当該ターンでどのツールが上限に達しているかをどうやって知るかが曖昧です。
- **推奨策**:
  以下のいずれかに責務を統一することを推奨します。
  - **案 A（Loop 側で選別）**: `ConversationLoop` 側で `calls` を走査し、超過しているものは即座に `ToolResult.Failed(..., "RETRY_LIMIT_EXCEEDED")` を生成し、未超過分のみを `ToolDispatcher` に渡す。
  - **案 B（Dispatcher に状態注入）**: `ToolDispatcher.ExecuteAllAsync(calls, IReadOnlySet<string> blockedToolNames, ct)` のように、ブロック対象のツール名セットを引数で渡す。

### ③ フェーズ分離（Non-UI → UI）による順序逆転のトレードオフ
- **懸念**:
  LLM が 1 ターンで `[ ToolA(UI), ToolB(Non-UI) ]` の順序で tool_use を出力した場合、フェーズ分離ルール（Phase 1: Non-UI → Phase 2: UI）を機械的に適用すると、**実行順が ToolB → ToolA に逆転**します。
  並列 tool_use は本来「独立」している前提ですが、LLM が「UI で選択中のテキストを取得し、その結果を使ってバックグラウンド処理する」ような依存関係を意図して同時に出してきた場合、順序逆転によって想定外の挙動になる可能性があります。
- **推奨策**:
  - 「フェーズ分離により、LLM のレスポンス順序に関わらず Non-UI ツールが UI ツールより先に実行される」という仕様上のトレードオフを明記する。
  - 順序依存がある操作はハンドラ側で安易に並行ツールとせず、1 往復ごとに実行させるプロンプト設計を推奨する旨をガイドラインに記載する。

### ④ 先行実行（投機実行）の有無の明文化
- **現状**:
  §4.1 では `ToolCallRequested` でイベント通知し、§4.2 のフロー図では `TurnComplete` 受信後に `lease.AppendAssistantMessage` → `ToolDispatcher` となっています。
- **確認・推奨**:
  ストリーミング中に `ToolCallRequested` が届いた時点でバックグラウンド実行を先行開始（Speculative Execution）するのか、それとも **`TurnComplete` を受信してアシスタント応答が完全に確定してから実行を開始するのか** を明確にしてください。
  デスクトップアプリの操作は副作用（ファイル更新、UI変更等）を伴うため、**「TurnComplete 受信後に一括ディスパッチ（投機実行は行わない）」** とする現状のフローが安全です。この方針を明記しておくことを推奨します。

---

## 2. UI / WPF スレッドマーシャリングの考慮点

### ① `SynchronizationContext` と `await` 後の継続コンテキスト
- **懸念**:
  §3.1 に「`await` 後の継続が UI に戻るかは `SynchronizationContext` に依存する」とあります。
  `IUiThreadMarshaller.InvokeAsync(Func<Task<T>>)` を `_dispatcher.InvokeAsync(asyncAction).Task.Unwrap()` 等で実装した場合、ハンドラ内の最初の `await` までは UI スレッドで動きますが、ハンドラ作成者が `await SomeWorkAsync().ConfigureAwait(false)` と書くと、その後の処理はスレッドプールで実行され、後続の UI 操作で `InvalidOperationException` が発生します。
- **推奨策**:
  ハンドラ実装者向けの規約として以下をドキュメント化することを推奨します。
  - 「`RequiresUiThread == true` のハンドラ内では、原則として `.ConfigureAwait(false)` を使用してはならない（UI スレッドへの継続復帰を維持するため）」
  - 「または、重い処理のみ `Task.Run` で明示的にスレッドプールへオフロードし、UI コントロールの更新直前でマーシャラを明示的に呼ぶ」

### ② `IUiThreadMarshaller` の UI スレッド同一性判定
- `_dispatcher.CheckAccess()` による最適化を行う際、再帰呼び出しや例外発生時のアンラップ漏れを防ぐため、以下のような単体テストケースを網羅しておくことが重要です。
  - 同一スレッドからの同期例外スロー
  - 同一スレッドからの非同期例外スロー（`Task.FromException` / 非同期メソッド内例外）
  - キャンセル済み CT を渡した際の `TaskCanceledException` の確実な伝播

---

## 3. LLM プロバイダ連携・耐障害性の観点

### ① 長大 `ToolResult` に対するトークン爆発ガード
- **懸念**:
  デスクトップアプリのツール（例: ドキュメント検索、ツリー走査、ログダンプ等）は、意図せず巨大なテキスト（数万行〜数十万文字）を返すことがあります。これをそのまま `LlmContent` に詰めると、次ターンの LLM 呼び出しで即座に Context Window 上限エラーになるか、莫大なトークン課金が発生します。
- **推奨策**:
  - `ToolResult` の生成ヘルパーまたは `ConversationLoop` に、`MaxLlmContentLength`（例: 30,000 文字などの上限）を設定可能にする。
  - 超過した場合は末尾を切り詰め、「`[Truncated: output exceeded limit, remaining N characters omitted]`」といった警告文を自動付与する保護ロジックを Core ユーティリティとして設ける。

### ② `StopReason.MaxTokens`（途中で切れた場合）の取り扱い
- **懸念**:
  LLM のトークン上限到達（`MaxTokens` / `length`）によりツール呼び出しの引数 JSON が途中で切れた場合、プロバイダ側で `ToolCallParseFailed` が発火します。
- **推奨策**:
  - `ToolCallParseFailed` が発生した tool_use については、ハンドラを実行せず `ToolResult.Failed(toolUseId, "JSON_PARSE_ERROR", "Tool call arguments were truncated or invalid JSON.")` を生成して履歴にコミットし、LLM に再試行させる契約を §4.1 に明記する。

### ③ 履歴（`ConversationState`）のシリアライズ・永続化の考慮
- **観点**:
  仕様書では MVP のスコープをインプロセスとしていますが、デスクトップアプリでは「アプリ再起動時のチャット履歴の復元」や「クラッシュレポートへの会話ログ添付」が必ず求められます。
- **推奨策**:
  `ChatMessage`、`ContentPart`（および派生レコード）、`ToolResult` 等に `System.Text.Json` のポリモーフィックシリアライズ属性（`[JsonPolymorphic]`, `[JsonDerivedType]`）を付与できる設計（または純粋な DTO 構造）にしておくと、Core の独立性を保ったまま利用側での永続化が容易になります。

---

## 4. 設計仕様書への反映用差分チェックリスト

| セクション | 追加・修正の推奨内容 |
| :--- | :--- |
| **§3.1** | `JsonElement input` はクローン済みであり、非同期ハンドラ内で安全に参照可能である契約を追記 |
| **§3.1** | `RequiresUiThread == true` ハンドラ内での `.ConfigureAwait(false)` 禁止・注意事項を追記 |
| **§3.3 / §4.3** | リトライ上限判定の呼び出し箇所を一本化（`ConversationLoop` 側で超過を判定し、`ToolDispatcher` へは超過フラグを渡すか未超過分のみ渡す） |
| **§4.1** | `StopReason == MaxTokens` 時の不完全な tool_use に対する `ToolCallParseFailed` → `ToolResult.Failed` 変換契約を追記 |
| **§4.2** | 投機実行（早期実行）は行わず、`TurnComplete` 受信後に一括ディスパッチする方針を明記 |
| **§4.3** | フェーズ分離（Non-UI → UI）によるツール実行順序逆転のトレードオフを明記 |
| **§4.4** | 長大 `ToolResult` に対する文字数上限・自動切り詰め（トークン爆発ガード）を追記 |

---

## 5. 総合結論

設計の方向性、責務の分割、WPF デスクトップ特有の考慮（STA スレッド、例外伝播、排他制御）は非常に堅牢であり、このまま実装に着手できる高いレベルにあります。上記の「JsonElement ライフタイム」「リトライ判定の受け渡し」「フェーズ順序逆転の仕様合意」の 3 点を詰めることで、実装時の手戻りなく高品質なライブラリが完成すると考えます。