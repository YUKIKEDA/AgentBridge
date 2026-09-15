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

## Layout (after M0)

```text
src/AgentBridge.*/
tests/AgentBridge.*.Tests/
samples/
```
