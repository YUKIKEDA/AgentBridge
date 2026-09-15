# AgentBridge — agent entry point

Start here when working in this repository as an agent.

## Environment first (do not abort)

If `dotnet --list-sdks` does not show **10.x**, or you are on Linux, follow [`docs/setup-agent.md`](docs/setup-agent.md) **immediately**. Install tools yourself. Do not stop the session to ask the user.

| Host | Verify |
| ---- | ------ |
| Windows | `./build.ps1` (full solution, including WPF) |
| Linux / Ubuntu 24.04 | `./scripts/bootstrap-ubuntu.sh` then `./scripts/verify-linux.sh` |

On Linux, **do not run `./build.ps1`**. WPF (`net10.0-windows`) cannot build there; that error is expected. Continue with Core / Anthropic / OpenAI.

Human setup: [`docs/setup.md`](docs/setup.md).

## Canonical docs (do not use `.dev/` for lasting decisions)

| Doc | Path | Role |
| ----- | ----- | ----- |
| Design | [`docs/design.md`](docs/design.md) | Product/architecture contracts |
| Roadmap | [`docs/roadmap.md`](docs/roadmap.md) | Milestones M0–M5 and planned issues |
| Contributing | [`CONTRIBUTING.md`](CONTRIBUTING.md) | Human-readable process & standards |
| Human setup | [`docs/setup.md`](docs/setup.md) | Windows 開発環境 |
| Agent setup | [`docs/setup-agent.md`](docs/setup-agent.md) | Bootstrap / Linux verify (do not abort) |
| Cursor rules | [`.cursor/rules/`](.cursor/rules/) | Always-applied enforcement |

`.dev/` is **temporary scratch** (e.g. review drafts under `.dev/reviews/`). Do not leave permanent decisions only there.

## Always-apply rules

- [`.cursor/rules/workflow.mdc`](.cursor/rules/workflow.mdc) — **Issue → branch → work → PR → human merge**
- [`.cursor/rules/conventional-commits.mdc`](.cursor/rules/conventional-commits.mdc)
- [`.cursor/rules/pull-requests.mdc`](.cursor/rules/pull-requests.mdc)
- [`.cursor/rules/engineering.mdc`](.cursor/rules/engineering.mdc)
- [`.cursor/rules/design-docs.mdc`](.cursor/rules/design-docs.mdc)
- [`.cursor/rules/setup.mdc`](.cursor/rules/setup.mdc) — bootstrap / Linux verify; do not abort

## Working agreements (summary)

- **Do not start coding a milestone without a GitHub Issue and branch.** Roadmap text is not a start signal.
- Follow `docs/design.md` contracts; design-changing work needs a design PR first (docs-only clarifications may ship with code).
- Implement in roadmap order; **1 issue ≈ 1 PR** (M0 may batch only with an Issue that says so).
- Branch: `type/<issue-number>-<slug>`.
- Commits / PR titles: Conventional Commits (Japanese subject OK).
- Local verification gate: `./build.ps1` against **`AgentBridge.slnx`** (no `.sln`). On Linux, use `./scripts/verify-linux.sh` instead (see [`docs/setup-agent.md`](docs/setup-agent.md)); do not abort on WPF.
- Layout: `src/AgentBridge.*` ↔ `tests/AgentBridge.*.Tests` (1:1), TFM `net10.0` (`net10.0-windows` for WPF).
- Comments (XML docs / inline) in **Japanese**; do not end them with `。` or `.` (exception/user-facing strings OK). Test methods use natural Japanese names like `FromUser_文字列を指定するとUserロールのテキストメッセージが生成されること`.

## Current backlog pointer

See [`docs/roadmap.md`](docs/roadmap.md). Next coding work requires creating the corresponding GitHub Issue first, then a branch, then a PR for human review.
