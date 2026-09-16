# サンプルの実行

最小 WPF チャットです。ライブラリ本体にはチャット見た目を含めません。

## 必要なもの

- Windows と .NET 10 SDK（[`setup.md`](setup.md)）
- OpenAI 互換の API キー

## 環境変数

| 変数 | 必須 | 説明 |
| ---- | ---- | ---- |
| `OPENAI_API_KEY` | はい | API キー |
| `OPENAI_MODEL` | いいえ | 既定 `gpt-4o-mini` |
| `OPENAI_ENDPOINT` | いいえ | 互換エンドポイントの URL。未設定なら OpenAI 公式 |

PowerShell の例:

```powershell
$env:OPENAI_API_KEY = "sk-..."
# $env:OPENAI_MODEL = "gpt-4o-mini"
# $env:OPENAI_ENDPOINT = "https://example.openai.azure.com/openai/v1/"
dotnet run --project samples/AgentBridge.WpfChat/AgentBridge.WpfChat.csproj -c Release
```

キーが無いときはウィンドウが開き、設定手順を出して送信は無効のままです。

## できること

- ストリーミング表示
- Stop（`AgentRunController.Cancel`）
- ダミーツール `get_local_time` と、UI スレッド経由の `set_status`

ソリューション `AgentBridge.slnx` に含まれます。Linux の `verify-linux.sh` は WPF と同様にこの sample をビルドしません。
