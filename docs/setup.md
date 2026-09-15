# 開発環境のセットアップ（人間向け）

この文書は **Windows 上で開発する人** 向けです。エージェント（Claude Code 等）は [`setup-agent.md`](setup-agent.md) を先に実行してください。

関連: [`CONTRIBUTING.md`](../CONTRIBUTING.md) / [`AGENTS.md`](../AGENTS.md)

## 必要なもの

| もの | 用途 | 備考 |
| ---- | ---- | ---- |
| Windows 10 / 11 | フル検証 | WPF プロジェクトは `net10.0-windows` のため、**マージ前の正本ゲート `./build.ps1` は Windows 専用** |
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | ビルド / テスト | Runtime だけでは足りない。SDK 10.x が必要 |
| Git | clone / ブランチ | |
| [GitHub CLI (`gh`)](https://cli.github.com/) | Issue / PR | このリポジトリの必須ワークフローで使う。推奨 |
| PowerShell | `./build.ps1` | Windows 付属の PowerShell 5.1 で足りる |

IDE は任意です。Cursor、Visual Studio 2022（.NET デスクトップ開発）、または C# 拡張入り VS Code/Cursor のいずれでも構いません。

Linux / WSL / Ubuntu だけでは **ソリューション全体はビルドできません**（WPF）。ライブラリ本体（Core / Anthropic / OpenAI）の作業なら Linux でも進められますが、手順は [`setup-agent.md`](setup-agent.md) を見てください。

## 1. SDK を入れる

例（winget）:

```powershell
winget install Microsoft.DotNet.SDK.10
winget install Git.Git
winget install GitHub.cli
```

入れたら **新しいターミナル** を開き、次で確認します。

```powershell
dotnet --list-sdks
```

`10.` で始まる SDK が一覧にあれば十分です。パッチ番号（例: `10.0.100` と `10.0.400`）は揃っていなくて構いません。

## 2. リポジトリを取得する

```powershell
git clone https://github.com/YUKIKEDA/AgentBridge.git
cd AgentBridge
```

すでに clone 済みなら `git pull` で `main` を更新してください。

## 3. 検証する（正本）

リポジトリルートで:

```powershell
./build.ps1
```

内容は `dotnet restore` → `dotnet format --verify-no-changes` → `dotnet build` → `dotnet test` です。対象は **`AgentBridge.slnx` のみ**（`.sln` は使いません）。

成功すると `build.ps1 completed successfully.` と出ます。これがマージ前のローカルゲートです。GitHub Actions はアカウント制限で動かないことがあるため、Actions の緑を Done 条件にしないでください。

## 4. 作業の進め方

実装に入る前に [`CONTRIBUTING.md`](../CONTRIBUTING.md) の順を守ります。

1. GitHub Issue を作る
2. ブランチ `type/<issue号>-<slug>` を切る
3. 実装する
4. `./build.ps1` が通ることを確認する
5. PR を出し、人間レビュー後にマージする

ロードマップの箇条書きだけを見て実装を始めてはいけません。

## 困ったとき

- **`dotnet` が認識されない:** インストール後にターミナルを開き直す。`dotnet --list-sdks` に 10.x が無いなら SDK が入っていない（Runtime のみのことが多い）
- **NuGet の restore が失敗する:** 社内プロキシ配下なら `HTTP_PROXY` / `HTTPS_PROXY` がターミナルに渡っているか確認する。プロキシを外して直そうとしない
- **`No .slnx found`:** カレントディレクトリがリポジトリルートではない
- **WPF だけ失敗する:** Linux / WSL で `./build.ps1` を実行している。フルゲートは Windows で回す
