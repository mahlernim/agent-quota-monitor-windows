param([string]$Version = '0.1.0-beta.3')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.]+)?$') { throw 'Use a simple semantic version.' }
Push-Location $PSScriptRoot
try {
    $buildEnv = Join-Path $PSScriptRoot '.venv-build'
    if (-not (Test-Path -LiteralPath $buildEnv)) {
        python -m venv $buildEnv
        if ($LASTEXITCODE) { throw 'Could not create the build environment.' }
    }
    $buildPython = Join-Path $buildEnv 'Scripts/python.exe'
    & $buildPython -m pip install --disable-pip-version-check -r requirements-build.txt
    if ($LASTEXITCODE) { throw 'Build dependency installation failed.' }
    & $buildPython -c "import runpy, struct; assert struct.calcsize('P') == 8; runpy.run_path('packaging/make_icon.py')"
    if ($LASTEXITCODE) { throw 'Icon generation failed.' }
    $buildTag = "$Version-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    $destination = Join-Path $PSScriptRoot "dist/$buildTag"
    & $buildPython -m PyInstaller --clean --windowed --onedir --noupx `
        --name agent-quota-monitor-windows --paths $PSScriptRoot `
        --distpath $destination --workpath build/pyinstaller --specpath build `
        --icon (Join-Path $PSScriptRoot 'build/robot-ring.ico') --hidden-import pystray._win32 `
        --add-data "${PSScriptRoot}/web:web" --add-data "${PSScriptRoot}/quota/copilot_bridge.mjs:quota" `
        --add-data "${PSScriptRoot}/quota/copilot_bridge_data.mjs:quota" (Join-Path $PSScriptRoot 'packaging/desktop_entry.py')
    if ($LASTEXITCODE) { throw 'Portable build failed.' }
    $bundle = Join-Path $destination 'agent-quota-monitor-windows'
    Copy-Item -LiteralPath LICENSE, THIRD-PARTY-NOTICES.md -Destination $bundle
    Copy-Item -LiteralPath docs/portable.md -Destination (Join-Path $bundle 'README.md')
    & $buildPython packaging/collect_notices.py $bundle
    if ($LASTEXITCODE) { throw 'Dependency notices could not be collected.' }
    $report = Join-Path $destination 'self-test.json'
    $smoke = Start-Process -FilePath (Join-Path $bundle 'agent-quota-monitor-windows.exe') `
        -ArgumentList '--self-test-output', ('"' + $report + '"') -WorkingDirectory $env:TEMP -WindowStyle Hidden -PassThru
    if (-not $smoke.WaitForExit(30000)) { throw 'Portable self-test did not finish within 30 seconds.' }
    if ($smoke.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report)) { throw 'Portable self-test failed.' }
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if (-not $result.passed -or -not $result.frozen) { throw 'Packaged runtime was not verified.' }
    $archive = Join-Path $destination "agent-quota-monitor-windows-$Version-win-x64.zip"
    Compress-Archive -LiteralPath $bundle -DestinationPath $archive
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
    Write-Output $archive
} finally { Pop-Location }
