param(
    [Parameter(Mandatory=$true)][string]$BundleDirectory,
    [string]$Version = '0.2.0-beta.6'
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.]+)?$') { throw 'Use a semantic version.' }
$bundle = (Resolve-Path -LiteralPath $BundleDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $bundle 'agent-quota-monitor-windows.exe'))) { throw 'Portable build is missing.' }
$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) { $iscc = Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe' }
if (-not (Test-Path -LiteralPath $iscc)) { $iscc = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' }
if (-not (Test-Path -LiteralPath $iscc)) { throw 'Install Inno Setup 6 to build the installer.' }
$productVersion = (Get-Item -LiteralPath (Join-Path $bundle 'agent-quota-monitor-windows.exe')).VersionInfo.ProductVersion.Split('+')[0]
if ($productVersion -ne $Version) { throw 'Installer and packaged app versions do not match.' }
$output = Split-Path $bundle
& $iscc "/DAppVersion=$Version" "/DBundleDir=$bundle" "/DOutputDir=$output" (Join-Path $PSScriptRoot 'packaging/windows-installer.iss')
if ($LASTEXITCODE) { throw 'Installer build failed.' }
$installer = Join-Path $output "agent-quota-monitor-windows-$Version-setup-win-x64.exe"
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($installer))" | Set-Content "$installer.sha256" -Encoding ascii
Write-Output $installer
