param([switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($PSScriptRoot)
$destination = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\PCAutobackup'))
if (-not (Test-Path -LiteralPath (Join-Path $source 'PCAutobackup.exe'))) { throw 'Run Install.cmd from the extracted release package.' }
# Do not interrupt backups or replace binaries used by an open settings window.
$busy = @(Get-Process -Name 'PCAutobackup','PCAutobackup.Worker' -ErrorAction SilentlyContinue)
if ($busy.Count) { throw 'Close PC AutoBackup settings and wait for any backup to finish, then run Install.cmd again.' }
$tray = Join-Path $destination 'Tray\PCAutobackup.Tray.exe'
$updating = Test-Path -LiteralPath $tray
if ($updating) {
    Start-Process -FilePath $tray -ArgumentList '--exit' -WindowStyle Hidden -Wait
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (@(Get-Process -Name 'PCAutobackup.Tray' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $tray }).Count -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if (@(Get-Process -Name 'PCAutobackup.Tray' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $tray }).Count) { throw 'The installed tray is still running. Exit it and retry.' }
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
if ($source -ne $destination) { Get-ChildItem -LiteralPath $source | Copy-Item -Destination $destination -Recurse -Force }
$shell = New-Object -ComObject WScript.Shell
$desktop = [Environment]::GetFolderPath('DesktopDirectory')
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'PC AutoBackup'
New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
foreach ($shortcutPath in @((Join-Path $desktop 'PC AutoBackup.lnk'), (Join-Path $startMenu 'PC AutoBackup.lnk'))) {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = Join-Path $destination 'PCAutobackup.exe'
    $shortcut.WorkingDirectory = $destination
    $shortcut.IconLocation = (Join-Path $destination 'Assets\app.ico') + ',0'
    $shortcut.Description = 'PC AutoBackup settings and recovery'
    $shortcut.Save()
}
$config = Join-Path $env:LOCALAPPDATA 'PCAutobackup\configuration.json'
$startup = '"' + $tray + '" --background --config "' + $config + '"'
if ($startup.Length -gt 260) { throw 'Startup command exceeds the Windows Run key limit.' }
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (-not (Test-Path -LiteralPath $runKey)) { New-Item -Path $runKey -Force | Out-Null }
# An update keeps "Start with Windows" off when the user turned it off from the tray menu.
$startupOff = $updating -and $null -eq (Get-ItemProperty -LiteralPath $runKey).PCAutobackup
if (-not $startupOff) { Set-ItemProperty -LiteralPath $runKey -Name 'PCAutobackup' -Value $startup }
if (Test-Path -LiteralPath $config) {
    $refresh = Start-Process -FilePath (Join-Path $destination 'Worker\PCAutobackup.Worker.exe') -ArgumentList ('--refresh-schedule --config "' + $config + '"') -WindowStyle Hidden -Wait -PassThru
    if ($refresh.ExitCode -ne 0) { throw 'Installed, but schedule refresh failed. Open settings and save the schedule again.' }
}
if (-not $NoLaunch) { Start-Process -FilePath $tray -ArgumentList '--background' -WindowStyle Hidden }
Write-Output "Installed: $destination"
Write-Output "Desktop shortcut: $(Join-Path $desktop 'PC AutoBackup.lnk')"
if ($startupOff) { Write-Output 'Start with Windows was off before this update and stays off; turn it on from the tray menu. Existing backup times and data are preserved.' }
else { Write-Output 'Starts in the tray at Windows sign-in. Existing backup times and data are preserved.' }
