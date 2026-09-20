param([int]$Port = 8765)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

$venv = Join-Path $PSScriptRoot '.venv-desktop'
if (-not (Test-Path -LiteralPath $venv)) {
    python -m venv $venv
    if ($LASTEXITCODE) { throw 'Could not create the desktop environment.' }
}
$python = Join-Path $venv 'Scripts\python.exe'
& $python -m pip install --disable-pip-version-check --requirement requirements-desktop.txt
if ($LASTEXITCODE) { throw 'Desktop dependency installation failed.' }
& $python -m quota.desktop --port $Port
