# Local verification gate (source of truth while GitHub Actions may be restricted).
# Usage: ./build.ps1  (Windows only — solution includes WPF / net10.0-windows)
# On Linux, use ./scripts/verify-linux.sh instead. Do not run this script there.
# Exits non-zero on failure.

$ErrorActionPreference = "Stop"

$sln = Get-ChildItem -Path . -Filter *.slnx -File -ErrorAction SilentlyContinue |
    Select-Object -First 1

if (-not $sln) {
    Write-Error "No .slnx found. This repo uses AgentBridge.slnx only (no .sln)."
    exit 1
}

$slnPath = $sln.FullName
Write-Host "Using solution: $slnPath"

dotnet restore $slnPath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet format $slnPath --verify-no-changes
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build $slnPath --no-restore -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet test $slnPath --no-build -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "build.ps1 completed successfully."
