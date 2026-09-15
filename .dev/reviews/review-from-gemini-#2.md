ご提示いただいた `AgentBridge` の設計仕様書（`agentbridge-design.md`）をレビューしました。

デスクトップアプリ特有の制約（UIスレッド調停、ステートフルなドキュメント、LLMの並列呼び出し）に正面から向き合っており、**フレームワークを肥大化させずインプロセス統合に徹するスコープの切り方が非常に秀逸**です。
設計の完成度はすでに高いですが、実装フェーズでの手戻りや本番運用時のトラブルを防ぐため、**「LLM API 仕様との整合性」「非同期・スレッド制御の落とし穴」「エラー・キャンセル時の状態整合性」**の観点からレビューコメントをまとめました。

---

## 1. 重大な検討事項（P0：整合性・安定性に関わる点）

### ① メタ指示を無視する LLM に対する「ディスパッチャ側の防波堤」
§4.4 では、リトライ上限超過時に `tool_result` へ「このツールは試行上限に達した」というメタ指示を付与する（または一時除外する）とされています。
- **懸念**: 小〜中規模モデルや特定のプロンプト状況において、LLM はメタ指示を無視して**次のターンでも同じツールを同一または不正な引数で即座に呼び出す**ケースが頻繁に発生します。
- **対策案**: `IRetryPolicy.ShouldRetry` が `false` になったツールについて、LLM が再度それを呼び出した場合は **`ToolDispatcher` / `ConversationLoop` の水際でハンドラを実行させずに即座に `ToolResult.Failed("RETRY_LIMIT_EXCEEDED", ...)` を LLM に返す**ガード処理を契約として明記することを推奨します。

### ② キャンセル時の「UI 表示」と「`ConversationState` 履歴」の乖離
§4.2 で「ストリーミング途中で `tool_use` が未確定のままキャンセルされた場合、不完全なアシスタントメッセージは履歴に残さない」とあります。
- **懸念**: `TextDelta` が UI に順次ストリーミング表示された後でキャンセルされた場合、UI のチャット画面にはテキストが表示されているのに、`ConversationState`（履歴）からはそれが破棄されるという**画面表示とメモリ状態の不整合**が起こり得ます。
- **対策案**:
  - `ConversationLoop` はキャンセル時に `TurnCancelled` や `TurnRolledBack` イベントを発火し、UI 側（ViewModel）が「キャンセルされたメッセージ」を取り消し線にするか、UI 表示上も削除・破棄できるようにイベント設計を統一すると安全です。

### ③ `IUiThreadMarshaller` と WPF `Dispatcher` の同期コンテキスト
§3.2 / §4.5 の `InvokeAsync<T>(Func<Task<T>> asyncAction)` に関する補足です。
- **懸念**: WPF の `Dispatcher.InvokeAsync(Func<Task<T>>)` に非同期メソッドを渡した場合、最初の `await` より前は UI スレッドで動きますが、`await` 以降の継続処理が UI スレッドに戻るかはハンドラ側の `SynchronizationContext` の設定に依存します。
- **対策案**:
  - UI ツールハンドラ作成者への規約として、「重い処理はハンドラ内で別スレッドに逃がし、UI 操作のみを UI スレッドで行う」または「UI スレッドを長時間占有（ブロッキング）しない」旨をガイドラインとして明記してください。長時間ブロッキングがあるとデスクトップアプリの UI が完全にフリーズします。

---

## 2. 実装・インターフェースの改善提案（P1）

### ④ Anthropic / OpenAI の `tool_result` 構造の差異の吸収
プロバイダ抽象化（§4.1）において、各プロバイダの API 制約を `ILlmProvider` がどう満たすかを明確にしておく必要があります。
- **Anthropic の制約**:
  - アシスタントが 3 つのツールを呼んだ場合、次のユーザーメッセージ（1 つの `ChatMessage`）の `content` 内に、3 つすべての `tool_result` を過不足なく含めなければ 400 エラーになります。
- **OpenAI の制約**:
  - 各ツール結果はそれぞれ独立した `role: "tool"` メッセージとして送信する必要があります。
- **推奨**:
  - `ChatMessage` のデータ構造において、「複数の ContentPart（テキスト、ToolUse、ToolResult）を柔軟に内包できる」ようにし、プロバイダ実装側でそれらを各 SDK のリクエスト型に変換する責務を明記してください。

### ⑤ .NET 9 `JsonSchemaExporter` の活用とスキーマ設計
`IToolHandler.InputSchema` が `JsonElement` となっています。
- コア層の軽量性を保つ意味で `JsonElement`（または `JsonObject`）は適切ですが、アプリ開発者が手作業で JSON Schema 文字列を書くのは非常に DX が悪く、バグの温床になります。
- **提案**:
  - .NET 8 / 9 をターゲットとする場合、.NET 9 標準の `System.Text.Json.Schema.JsonSchemaExporter` や、`System.Text.Json.Nodes.JsonNode` を用いて、C# の型情報（DTO）からスキーマを自動生成するヘルパー（または拡張メソッド）を `AgentBridge.Core` のユーティリティとして提供すると、アプリ開発の敷居が大幅に下がります。

### ⑥ `ToolResult` の型定義の具体化（LLM 向け vs UI/ログ向け）
§4.4 に「LLM 向け / UI 向けメッセージの分離（P1）」とありますが、型定義（コード片）がありません。
- **提案**: 以下のような分離プロパティを持つ設計が推奨されます。
  ```csharp
  public readonly record struct ToolResult
  {
      public bool IsSuccess { get; init; }
      /// <summary>LLM に送信する結果テキスト（機密情報やスタックトレースを含めない）</summary>
      public string LlmContent { get; init; }
      /// <summary>UI やログにのみ表示する詳細（例外メッセージ、絶対パス等）</summary>
      public string? DiagnosticDetails { get; init; }
      public string? ErrorCode { get; init; }
      
      public static ToolResult Success(string content) => ...;
      public static ToolResult Failed(string errorCode, string userFriendlyMessage, string? diagnosticDetails = null) => ...;
  }
  ```

### ⑦ Non-UI ツールの並列度制御（Throttling）
§4.3 の Phase 1 では `nonUiCalls` を `Task.WhenAll` で一斉実行しています。
- **懸念**: LLM が一度に数十個のツール呼び出しを生成した場合（大量ファイルの読み込み等）、スレッドプール枯渇や外部 API のレートリミットに抵触する恐れがあります。
- **提案**: 必要に応じて並列度（Concurrency Limit）を制限できるよう、`ToolDispatcherOptions` 等で `MaxDegreeOfParallelism`（デフォルト: `Environment.ProcessorCount` など）を挟める構造にしておくと堅牢です。

---

## 3. 細部の確認・ブラッシュアップ（P2）

| 項目 | 現状 | 改善の検討 |
| :--- | :--- | :--- |
| **ターン排他制御** | §4.2: 未完了ターンへの再入は `InvalidOperationException` | `ConversationState` に `SemaphoreSlim(1, 1)` または `Interlocked.CompareExchange` によるアトミックなターン開始トークン（`TurnScope : IDisposable` 等）を導入し、`try-finally` で確実に解放するイディオムにすると安全です。 |
| **Reasoning / Thinking Delta** | §5.1: 段階導入 | Claude 3.7 Sonnet や DeepSeek、OpenAI o3 等、近年の推論モデルはツール呼び出し前に Reasoning（思考プロセス）をストリーミングします。将来の破壊的変更を避けるため、`ProviderEvent` に `public record ReasoningDelta(string Text) : ProviderEvent;` だけあらかじめ空定義で加えておくことを推奨します。 |
| **ツールの動的除外** | §4.4: 一時除外（オプション） | `IRetryPolicy` で上限に達したツールをターン内一時除外する場合、`SendAsync` に渡す `IReadOnlyList<ToolDefinition>` をフィルタリングするだけで済むため、実装コストは低く効果的です。 |

---

## 総評
「何をやらないか（マルチエージェントや重厚なフレームワーク化の排除）」が明確でありながら、デスクトップアプリ開発者が実際に LLM Tool Use を組み込む際に必ず直面する「STA スレッド」「未捕捉例外」「LLM のツール呼び出しループ」「API ごとの差異」をきれいに解決できる非常に優れた設計です。

上記 P0/P1 の数点を設計仕様に補足・反映いただければ、実装フェーズへ極めてスムーズに移行できると考えます。