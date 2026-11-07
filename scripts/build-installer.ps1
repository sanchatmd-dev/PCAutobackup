param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
$package = Join-Path $root 'artifacts\PCAutobackup-win-x64'
if (-not (Test-Path -LiteralPath (Join-Path $package 'Install.cmd'))) { throw 'Build the application package first.' }
# Only release output belongs in the embedded archive. Never include a user's application-data directory.
$unsafe = @(Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object { $_.Name -eq 'configuration.json' -or $_.Name -like 'client_secret*' -or $_.Extension -in '.pcakey','.pca-key','.pca-recovery' })
if ($unsafe.Count) { throw 'User configuration or recovery credentials found in release output.' }
Compress-Archive -Path (Join-Path $package '*') -DestinationPath (Join-Path $root 'artifacts\installer-payload.zip') -Force
Copy-Item -LiteralPath (Join-Path $root 'artifacts\installer-payload.zip') -Destination (Join-Path $root 'artifacts\PCAutobackup-win-x64.zip') -Force
$sdk = Join-Path $env:LOCALAPPDATA 'PCAutobackup\dotnet-sdk\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = 'dotnet' }
& $sdk publish (Join-Path $root 'src\PCAutobackup.Installer\PCAutobackup.Installer.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $root 'artifacts\installer-build')
if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed.' }
$target = Join-Path $root 'artifacts\PCAutobackup-win-x64.exe'
Copy-Item -LiteralPath (Join-Path $root 'artifacts\installer-build\PCAutobackup-Setup.exe') -Destination $target -Force
Get-FileHash -LiteralPath $target -Algorithm SHA256
