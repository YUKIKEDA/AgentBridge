# AgentBridge

クラウド LLM の Tool Use をデスクトップアプリへ組み込むための .NET インプロセス統合ライブラリです。

## Docs

- エージェント入口: [`AGENTS.md`](AGENTS.md)
- 設計: [`docs/design.md`](docs/design.md)
- ロードマップ: [`docs/roadmap.md`](docs/roadmap.md)
- 開発規約: [`CONTRIBUTING.md`](CONTRIBUTING.md)

## Verify locally

GitHub Actions が使えない場合があるため、検証の正本はローカルです。

```powershell
./build.ps1
```

## Process

See [`CONTRIBUTING.md`](CONTRIBUTING.md): **Issue → branch → work → PR → human merge**. Do not start milestone work from the roadmap text alone.

## Layout

```text
AgentBridge.slnx
src/AgentBridge.*/
tests/AgentBridge.*.Tests/
samples/
```
