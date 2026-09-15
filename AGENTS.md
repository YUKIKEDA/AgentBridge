# AgentBridge — agent entry point

Start here when working in this repository as an agent.

## Canonical docs (do not use `.dev/` for lasting decisions)

| Doc          | Path                                 | Role                                |
| ------------ | ------------------------------------ | ----------------------------------- |
| Design       | [`docs/design.md`](docs/design.md)   | Product/architecture contracts      |
| Roadmap      | [`docs/roadmap.md`](docs/roadmap.md) | Milestones M0–M5 and planned issues |
| Contributing | [`CONTRIBUTING.md`](CONTRIBUTING.md) | Human-readable process & standards  |
| Cursor rules | [`.cursor/rules/`](.cursor/rules/)   | Always-applied enforcement          |

`.dev/` is **temporary scratch** (e.g. review drafts under `.dev/reviews/`). Do not leave permanent decisions only there.

## Always-apply rules

- [`.cursor/rules/conventional-commits.mdc`](.cursor/rules/conventional-commits.mdc)
- [`.cursor/rules/pull-requests.mdc`](.cursor/rules/pull-requests.mdc)
- [`.cursor/rules/engineering.mdc`](.cursor/rules/engineering.mdc)
- [`.cursor/rules/design-docs.mdc`](.cursor/rules/design-docs.mdc)

## Working agreements (summary)

- Follow `docs/design.md` contracts; design-changing work needs a design PR first (docs-only clarifications may ship with code).
- Implement in roadmap order; **1 issue ≈ 1 PR** (M0 may batch).
- Branch: `type/<issue-number>-<slug>`.
- Commits / PR titles: Conventional Commits (Japanese subject OK).
- Local verification gate: `./build.ps1` (GitHub Actions may be unavailable — do not assume CI green on GitHub).
- Layout after M0: `src/AgentBridge.*` ↔ `tests/AgentBridge.*.Tests` (1:1), TFM `net10.0` (`net10.0-windows` for WPF).

## First implementation target

See **M0** in [`docs/roadmap.md`](docs/roadmap.md): solution + eight empty projects + analyzers + `build.ps1` green locally.
