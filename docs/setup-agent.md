# Agent environment setup

Read this **before writing product code**. Environment setup is part of the task. Do **not** stop, do **not** ask the user to install tools, and do **not** treat a missing SDK as the end of the session.

Human-oriented guide: [`setup.md`](setup.md). Process: [`AGENTS.md`](../AGENTS.md).

## Hard rules

1. On Linux, **never** run `./build.ps1`. It restores `AgentBridge.slnx`, which includes WPF (`net10.0-windows`). That failure is expected and is **not** a reason to abort.
2. On Ubuntu 24.04 (Noble), install .NET **only** from Ubuntu’s own apt feed: `dotnet-sdk-10.0`. **Do not** add `packages.microsoft.com` for .NET. Microsoft no longer publishes .NET packages for Ubuntu 24.04+.
3. **Do not unset** `HTTP_PROXY` / `HTTPS_PROXY` / `http_proxy` / `https_proxy`. Network already works via proxy. Leave apt proxy config as-is.
4. Use non-interactive apt: `DEBIAN_FRONTEND=noninteractive` and `apt-get -y`.
5. After bootstrap, **continue the original task**. Bootstrap is a prerequisite, not a deliverable by itself.
6. WPF work (`src/AgentBridge.Wpf`, `tests/AgentBridge.Wpf.Tests`, `samples/AgentBridge.WpfChat`) requires Windows. On Linux, skip it and verify Core only.
7. Do not create `.sln` files. The solution is `AgentBridge.slnx` only.

## 0. Decide the path

```bash
uname -s
dotnet --list-sdks 2>/dev/null || true
```

| Host | What to run |
| ---- | ----------- |
| Linux (Ubuntu 24.04 / Claude Code) | `./scripts/bootstrap-ubuntu.sh` then `./scripts/verify-linux.sh` |
| Windows | If SDK 10.x is missing, install it; then `./build.ps1` from repo root |
| Already have `dotnet` 10.x on Linux | Skip bootstrap packages; still run `./scripts/verify-linux.sh` instead of `./build.ps1` |

If you are root, omit `sudo`. If you are not root, use `sudo` (this environment has it).

## 1. Ubuntu 24.04 bootstrap (Claude Code)

From the **repository root**:

```bash
chmod +x scripts/bootstrap-ubuntu.sh scripts/verify-linux.sh
./scripts/bootstrap-ubuntu.sh
./scripts/verify-linux.sh
```

If the scripts are not in the tree yet, run the inline equivalent below.

### Inline bootstrap (idempotent)

```bash
export DEBIAN_FRONTEND=noninteractive
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

if [ "$(id -u)" -eq 0 ]; then SUDO=""; else SUDO="sudo"; fi

$SUDO apt-get update -y
$SUDO apt-get install -y git ca-certificates curl dotnet-sdk-10.0

# gh is optional (Issue/PR). Do not abort if this fails.
$SUDO apt-get install -y gh || true

dotnet --list-sdks
```

Expected: at least one SDK line starting with `10.`. `git` on PATH. `gh` is nice-to-have.

### Inline Linux verify (non-WPF)

Do **not** pass `AgentBridge.slnx` to `dotnet build` / `dotnet test` on Linux.

```bash
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

projects="
src/AgentBridge.Core/AgentBridge.Core.csproj
tests/AgentBridge.Core.Tests/AgentBridge.Core.Tests.csproj
"

for p in $projects; do dotnet restore "$p"; done
for p in $projects; do dotnet format "$p" --verify-no-changes; done
for p in $projects; do dotnet build "$p" --no-restore -c Release; done
for p in tests/AgentBridge.Core.Tests/AgentBridge.Core.Tests.csproj; do
  dotnet test "$p" --no-build -c Release
done
```

If this succeeds, the Linux environment is good enough to implement Core (and docs). Full `./build.ps1` remains the Windows merge gate; mention that in the PR Verification section.

## 2. What “failure” means

| Symptom | Action |
| ------- | ------ |
| `NETSDK1100` / Windows targeting / `UseWPF` while building the `.slnx` | Expected on Linux. Stop using the `.slnx` / `build.ps1`. Use `verify-linux.sh` |
| `dotnet: command not found` after apt | Recheck `dotnet-sdk-10.0` installed; do **not** switch to Microsoft’s package repo |
| apt / nuget timeout | Keep proxy env vars. Retry once. Do not disable the proxy |
| `gh` missing or unauthenticated | Continue coding and local verify. Issue/PR can wait; do not abort the implementation |
| PowerShell / `pwsh` missing | Irrelevant on Linux. Use the bash scripts. Do not spend the session installing PowerShell |

## 3. After the environment is ready

Follow [`AGENTS.md`](../AGENTS.md): grill if needed → Issue → branch `type/<issue-number>-<slug>` → implement → verify → PR with `Closes #N`.

Do not start milestone implementation without an Issue number and branch.
