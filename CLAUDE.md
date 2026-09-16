# Claude Code

This repository’s agent entry point is [`AGENTS.md`](AGENTS.md). Read it.

## Before any product work

1. Follow [`docs/setup-agent.md`](docs/setup-agent.md). On Ubuntu 24.04 run `./scripts/bootstrap-ubuntu.sh` then `./scripts/verify-linux.sh`.
2. Do **not** run `./build.ps1` on Linux. WPF (`net10.0-windows`) cannot build. That is expected; continue with Core（M2 以降。移行中は Anthropic / OpenAI プロジェクトが残っている場合がある）。
3. Do **not** add `packages.microsoft.com` for .NET. Install `dotnet-sdk-10.0` from Ubuntu apt.
4. Do **not** unset proxy environment variables.
5. Missing tools are not a reason to stop. Install them (root / apt are available) and continue the original task.

Human setup: [`docs/setup.md`](docs/setup.md).
