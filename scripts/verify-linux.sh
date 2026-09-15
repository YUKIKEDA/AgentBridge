#!/usr/bin/env bash
# Linux 用の非 WPF 検証
# 正本ゲートは Windows の ./build.ps1（AgentBridge.slnx 全体）。こちらは Core / Anthropic / OpenAI のみ
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

if ! command -v dotnet >/dev/null 2>&1; then
  echo "verify-linux: dotnet が無い。先に ./scripts/bootstrap-ubuntu.sh を実行する" >&2
  exit 1
fi

projects=(
  src/AgentBridge.Core/AgentBridge.Core.csproj
  src/AgentBridge.Anthropic/AgentBridge.Anthropic.csproj
  src/AgentBridge.OpenAI/AgentBridge.OpenAI.csproj
  tests/AgentBridge.Core.Tests/AgentBridge.Core.Tests.csproj
  tests/AgentBridge.Anthropic.Tests/AgentBridge.Anthropic.Tests.csproj
  tests/AgentBridge.OpenAI.Tests/AgentBridge.OpenAI.Tests.csproj
)

tests=(
  tests/AgentBridge.Core.Tests/AgentBridge.Core.Tests.csproj
  tests/AgentBridge.Anthropic.Tests/AgentBridge.Anthropic.Tests.csproj
  tests/AgentBridge.OpenAI.Tests/AgentBridge.OpenAI.Tests.csproj
)

echo "verify-linux: restore"
for project in "${projects[@]}"; do
  dotnet restore "$project"
done

echo "verify-linux: format --verify-no-changes"
for project in "${projects[@]}"; do
  dotnet format "$project" --verify-no-changes
done

echo "verify-linux: build Release"
for project in "${projects[@]}"; do
  dotnet build "$project" --no-restore -c Release
done

echo "verify-linux: test Release"
for project in "${tests[@]}"; do
  dotnet test "$project" --no-build -c Release
done

echo "verify-linux: completed successfully (WPF skipped)."
echo "verify-linux: full merge gate remains ./build.ps1 on Windows."
