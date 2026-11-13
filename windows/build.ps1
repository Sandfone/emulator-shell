# Builds a release Emulator Shell for Windows and installs it in %LOCALAPPDATA%\Programs\Emulator Shell (another
# folder as the first argument), with a Start menu shortcut, so the manager can be started from Start or the
# taskbar. Run it again after changing the code; close the app first. The counterpart of scripts/make-app.sh.
# Usage: powershell -ExecutionPolicy Bypass -File windows\build.ps1 [install folder]
param([string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\Emulator Shell'))
$ErrorActionPreference = 'Stop'
$platform = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'ARM64' } else { 'x64' }

if (Get-Process EmulatorShell -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$Destination\*" }) {
    throw "Emulator Shell is running from $Destination; quit it first."
}
$project = Join-Path $PSScriptRoot 'EmulatorShell\EmulatorShell.csproj'
dotnet build $project -c Release -p:Platform=$platform -v q -clp:NoSummary
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
$output = Get-ChildItem (Join-Path $PSScriptRoot "EmulatorShell\bin\$platform\Release") -Recurse -Filter EmulatorShell.exe | Select-Object -First 1

New-Item -ItemType Directory -Force $Destination | Out-Null
robocopy $output.DirectoryName $Destination /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "copying to $Destination failed" }

$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Emulator Shell.lnk'
$link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
$link.TargetPath = Join-Path $Destination 'EmulatorShell.exe'
$link.WorkingDirectory = $Destination
$link.IconLocation = Join-Path $Destination 'Assets\AppIcon.ico'
$link.Description = 'Android emulators in their own windows'
$link.Save()
Write-Host "Installed $Destination\EmulatorShell.exe and the Start menu shortcut"
