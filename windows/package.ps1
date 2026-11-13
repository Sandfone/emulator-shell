# Builds the release installer: windows\dist\EmulatorShell-<version>-<platform>.msi. The app and the SDK forwarder
# are published self-contained, so the installer needs neither .NET nor the Windows App SDK on the target PC; it
# installs per user (no administrator) into %LOCALAPPDATA%\Programs\Emulator Shell, with a Start menu shortcut,
# and uninstalls from Settings › Apps. Needs the WiX tool: `dotnet tool install --global wix --version 5.0.2`.
# Usage: powershell -ExecutionPolicy Bypass -File windows\package.ps1 [-Version 1.0.12] [-Platform x64|ARM64]
param(
    # By default 1.0.<number of commits>, which grows with every change.
    [string]$Version = "1.0.$(git -C $PSScriptRoot rev-list --count HEAD)",
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'ARM64' } else { 'x64' })
)
$ErrorActionPreference = 'Stop'
$runtime = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$dist = Join-Path $PSScriptRoot "dist\$Platform"
$app = Join-Path $dist 'app'
$wix = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wix) { $wix = Get-Command (Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe') -ErrorAction SilentlyContinue }
if (-not $wix) { throw 'WiX is missing: dotnet tool install --global wix --version 5.0.2' }

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
dotnet publish (Join-Path $PSScriptRoot 'EmulatorShell') -c Release -p:Platform=$Platform -r $runtime --self-contained true `
    -p:Version=$Version -o $app -v q -clp:NoSummary
if ($LASTEXITCODE -ne 0) { throw 'publishing the app failed' }
# One file that stands in for emulator.exe among the SDK's own, trimmed to what it uses.
dotnet publish (Join-Path $PSScriptRoot 'Forwarder') -c Release -p:Platform=$Platform -r $runtime --self-contained true `
    -p:PublishTrimmed=true -p:EnableCompressionInSingleFile=true -p:Version=$Version -o (Join-Path $dist 'forwarder') -v q -clp:NoSummary
if ($LASTEXITCODE -ne 0) { throw 'publishing the forwarder failed' }
Copy-Item (Join-Path $dist 'forwarder\EmulatorShellForwarder.exe') $app -Force
Get-ChildItem $app -Filter *.pdb | Remove-Item

$msi = Join-Path $PSScriptRoot "dist\EmulatorShell-$Version-$Platform.msi"
& $wix.Source build (Join-Path $PSScriptRoot 'installer\EmulatorShell.wxs') -arch $Platform.ToLower() `
    -d "Version=$Version" -d "AppDir=$app" -o $msi
if ($LASTEXITCODE -ne 0) { throw 'building the installer failed' }
Write-Host "Built $msi"
