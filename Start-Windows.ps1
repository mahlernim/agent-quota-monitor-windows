$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $sdk = Join-Path $PSScriptRoot '.dotnet/dotnet.exe'
    if (-not (Test-Path $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
    & $sdk build native/AgentQuotaMonitor.csproj -c Release --nologo
    if ($LASTEXITCODE) { throw 'WPF build failed.' }
    Start-Process -FilePath (Join-Path $PSScriptRoot 'native/bin/Release/net10.0-windows/agent-quota-monitor-windows.exe') -WorkingDirectory $PSScriptRoot -WindowStyle Hidden
} finally { Pop-Location }
