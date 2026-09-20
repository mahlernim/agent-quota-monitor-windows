#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$runtime = Join-Path $env:LOCALAPPDATA 'QuotaDashboard/copilot-runtime'
$cli = Get-ChildItem "$env:LOCALAPPDATA/Microsoft/WinGet/Packages/GitHub.Copilot_*/copilot.exe" -ErrorAction SilentlyContinue
if (-not $cli -and -not (Get-Command copilot.exe -ErrorAction SilentlyContinue)) {
    winget install --id GitHub.Copilot --exact --source winget --silent
    if ($LASTEXITCODE) { throw 'Official Copilot CLI installation failed.' }
}
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
npm install --prefix $runtime --no-audit --no-fund @github/copilot-sdk@1.0.14
if ($LASTEXITCODE) { throw 'Official Copilot SDK installation failed.' }
Push-Location $PSScriptRoot
try {
    python -m quota.copilot
    if ($LASTEXITCODE) { throw 'Copilot quota verification failed. Check the existing GitHub CLI sign-in.' }
} finally { Pop-Location }
