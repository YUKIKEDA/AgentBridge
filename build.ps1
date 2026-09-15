# Local verification gate (source of truth while GitHub Actions may be restricted).
# Usage: ./build.ps1
# Exits non-zero on failure.

$ErrorActionPreference = "Stop"

$sln = Get-ChildItem -Path . -Filter *.sln -File -ErrorAction SilentlyContinue |
    Select-Object -First 1

if (-not $sln) {
    Write-Host "No .sln found yet (expected after M0). Nothing to build."
    Write-Host "After the solution exists, this script will run: restore → format verify → build → test."
    exit 0
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
