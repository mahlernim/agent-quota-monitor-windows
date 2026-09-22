param([string]$Version)
$ErrorActionPreference = 'Stop'
if (-not $PSBoundParameters.ContainsKey('Version')) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'native/AgentQuotaMonitor.csproj') -Raw
    $Version = [string]$project.Project.PropertyGroup.Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.]+)?$') { throw 'Use a simple semantic version.' }
Push-Location $PSScriptRoot
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $sdk = Join-Path $PSScriptRoot '.dotnet/dotnet.exe'
    if (-not (Test-Path $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
    $python = Join-Path $PSScriptRoot '.venv-build/Scripts/python.exe'
    if (-not (Test-Path $python)) {
        python -m venv .venv-build
        if ($LASTEXITCODE) { throw 'Build environment creation failed.' }
    }
    & $python -m pip install --disable-pip-version-check -r requirements-build.txt
    if ($LASTEXITCODE) { throw 'Build dependencies failed.' }
    & $python packaging/make_icon.py
    if ($LASTEXITCODE) { throw 'Icon generation failed.' }
    $destination = Join-Path $PSScriptRoot "dist/wpf-$Version-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    $bundle = Join-Path $destination 'agent-quota-monitor-windows'
    & $sdk publish native/AgentQuotaMonitor.csproj -c Release -r win-x64 --self-contained true -p:Version=$Version -o $bundle --nologo
    if ($LASTEXITCODE) { throw 'WPF build failed.' }
    & $python -m PyInstaller --clean --windowed --onedir --noupx --name quota-backend `
        --paths $PSScriptRoot --distpath (Join-Path $destination 'reader') --workpath build/backend --specpath build `
        --add-data "${PSScriptRoot}/web:web" --add-data "${PSScriptRoot}/quota/copilot_bridge.mjs:quota" `
        --add-data "${PSScriptRoot}/quota/copilot_bridge_data.mjs:quota" (Join-Path $PSScriptRoot 'packaging/backend_entry.py')
    if ($LASTEXITCODE) { throw 'Reader build failed.' }
    Copy-Item -LiteralPath (Join-Path $destination 'reader/quota-backend') -Destination (Join-Path $bundle 'backend') -Recurse
    Copy-Item -LiteralPath LICENSE, THIRD-PARTY-NOTICES.md -Destination $bundle
    Copy-Item -LiteralPath docs/windows.md -Destination (Join-Path $bundle 'README.md')
    & $python packaging/collect_notices.py $bundle
    if ($LASTEXITCODE) { throw 'License collection failed.' }
    $dotnetNotices = Join-Path $bundle 'licenses/dotnet'
    New-Item -ItemType Directory -Path $dotnetNotices -Force | Out-Null
    $sdkRoot = Split-Path $sdk
    foreach ($notice in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
        $source = Join-Path $sdkRoot $notice
        if (-not (Test-Path $source)) { throw "Missing .NET notice $source" }
        Copy-Item -LiteralPath $source -Destination $dotnetNotices
    }
    $report = Join-Path $destination 'self-test.json'
    $smoke = Start-Process -FilePath (Join-Path $bundle 'agent-quota-monitor-windows.exe') `
        -ArgumentList '--self-test-output', ('"' + $report + '"') -WorkingDirectory $env:TEMP -WindowStyle Hidden -PassThru
    if (-not $smoke.WaitForExit(30000)) { throw 'WPF smoke check timed out.' }
    if ($smoke.ExitCode -ne 0 -or -not (Test-Path $report)) { throw 'WPF smoke check failed.' }
    if (-not (Get-Content $report -Raw | ConvertFrom-Json).passed) { throw 'WPF smoke report did not pass.' }
    $archive = Join-Path $destination "agent-quota-monitor-windows-$Version-win-x64.zip"
    Compress-Archive -LiteralPath $bundle -DestinationPath $archive
    $hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($archive))" | Set-Content "$archive.sha256" -Encoding ascii
    Write-Output $archive
} finally { Pop-Location }
