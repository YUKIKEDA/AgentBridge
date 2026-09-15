#!/usr/bin/env bash
# Ubuntu 24.04 向けの非対話ブートストラップ
# エージェントはユーザーに質問せず、このスクリプトをリポジトリルート相当から実行する
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

export DEBIAN_FRONTEND=noninteractive
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

if [ "$(id -u)" -eq 0 ]; then
  SUDO=""
else
  SUDO="sudo"
fi

log() {
  printf 'bootstrap: %s\n' "$*"
}

log "cwd=$ROOT"
log "proxy HTTP_PROXY=${HTTP_PROXY-} HTTPS_PROXY=${HTTPS_PROXY-} http_proxy=${http_proxy-} https_proxy=${https_proxy-}"

need_sdk=1
if command -v dotnet >/dev/null 2>&1; then
  if dotnet --list-sdks 2>/dev/null | grep -E '^10\.' >/dev/null; then
    need_sdk=0
  fi
fi

packages=()
if ! command -v git >/dev/null 2>&1; then
  packages+=(git)
fi
if ! command -v curl >/dev/null 2>&1; then
  packages+=(curl)
fi
packages+=(ca-certificates)
if [ "$need_sdk" -eq 1 ]; then
  packages+=(dotnet-sdk-10.0)
fi

log "apt-get update"
$SUDO apt-get update -y

log "apt-get install: ${packages[*]}"
$SUDO apt-get install -y "${packages[@]}"

if ! command -v gh >/dev/null 2>&1; then
  log "optional: apt-get install gh"
  if ! $SUDO apt-get install -y gh; then
    log "WARN: gh の導入に失敗した。Issue/PR は後回しにして作業を継続する"
  fi
fi

if ! command -v dotnet >/dev/null 2>&1; then
  log "ERROR: dotnet が PATH に無い。Ubuntu 24.04 では apt の dotnet-sdk-10.0 を使う（packages.microsoft.com は追加しない）"
  exit 1
fi

if ! dotnet --list-sdks | grep -E '^10\.' >/dev/null; then
  log "ERROR: .NET SDK 10.x が必要。見つかった SDK:"
  dotnet --list-sdks || true
  exit 1
fi

log "BOOTSTRAP OK"
dotnet --list-sdks
git --version || true
if command -v gh >/dev/null 2>&1; then
  gh --version | head -n 1 || true
else
  log "gh は未導入"
fi

log "次は ./scripts/verify-linux.sh を実行する"
log "Linux で ./build.ps1 は実行しない（WPF / net10.0-windows が失敗する）"
