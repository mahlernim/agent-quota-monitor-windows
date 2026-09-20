$ErrorActionPreference = 'Stop'
$statePath = Join-Path $PSScriptRoot 'work/server-process.json'
if (-not (Test-Path -LiteralPath $statePath)) {
    Write-Output 'No task-launched preview is recorded.'
    exit 0
}
$state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
$preview = Get-Process -Id $state.ProcessId -ErrorAction SilentlyContinue
if (-not $preview) {
    Write-Output 'The recorded preview is already stopped.'
    exit 0
}
if ($preview.StartTime.ToUniversalTime().Ticks -ne ([datetime]$state.StartTime).ToUniversalTime().Ticks) {
    throw 'The recorded process ID has been reused. No process was stopped.'
}
$details = Get-CimInstance Win32_Process -Filter "ProcessId=$($preview.Id)"
if ($details.CommandLine -notmatch '-m quota.server') {
    throw 'The recorded process is not the quota dashboard. No process was stopped.'
}
Stop-Process -Id $preview.Id
Write-Output 'Quota dashboard preview stopped.'
