# Run only inside Windows Sandbox. Never invoke on the host.
$ErrorActionPreference = 'Stop'
if ($env:USERNAME -ne 'WDAGUtilityAccount' -or $PSScriptRoot -ne 'C:\SandboxInput') {
    throw 'This runner is restricted to Windows Sandbox at C:\SandboxInput.'
}
$results = 'C:\SandboxResults'
Start-Transcript -Path (Join-Path $results 'sandbox-transcript.txt') -Force
try {
    $installer = Join-Path $PSScriptRoot 'Candidate-Setup.exe'
    $installed = Join-Path $env:LOCALAPPDATA 'Programs\PCAutobackup'
    if (Test-Path $installed) { throw 'A fresh Sandbox is required.' }
    function Install-Candidate {
        $p = Start-Process $installer -ArgumentList '--silent --no-launch' -WindowStyle Hidden -Wait -PassThru
        if ($p.ExitCode -ne 0) { throw "Installer failed: $($p.ExitCode)" }
    }
    Install-Candidate
    & "$PSScriptRoot\Fixture\SandboxFixture.exe" setup
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic fixture setup failed.' }
    $config = Join-Path $env:LOCALAPPDATA 'PCAutobackup\configuration.json'
    $worker = Join-Path $installed 'Worker\PCAutobackup.Worker.exe'
    $p = Start-Process $worker -ArgumentList '--refresh-schedule' -WindowStyle Hidden -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw 'Synthetic schedule registration failed.' }
    $device = (Get-Content $config -Raw | ConvertFrom-Json).DeviceId
    $scheduler = New-Object -ComObject Schedule.Service
    $scheduler.Connect()
    $taskBefore = $scheduler.GetFolder('\').GetTask("PCAutobackup-$device").Xml
    $configBefore = (Get-FileHash $config).Hash
    $successBefore = (Get-FileHash "$config.success.json").Hash
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    Remove-ItemProperty $runKey -Name PCAutobackup
    Install-Candidate
    if ((Get-FileHash $config).Hash -ne $configBefore) { throw 'Update changed config.' }
    if ((Get-FileHash "$config.success.json").Hash -ne $successBefore) { throw 'Update changed success marker.' }
    if ($scheduler.GetFolder('\').GetTask("PCAutobackup-$device").Xml -ne $taskBefore) { throw 'Update changed task XML.' }
    if ((Get-ItemProperty $runKey).PCAutobackup) { throw 'Update re-enabled startup.' }
    & "$PSScriptRoot\Fixture\SandboxFixture.exe" verify
    if ($LASTEXITCODE -ne 0) { throw 'Restore after update failed.' }
    'PASS: clean install, same-version update, config/task/success preservation, startup-off preservation, synthetic restore.' | Set-Content "$results\automated-result.txt"
    # Open the installed app for the manual UI checklist. Only synthetic sources exist here.
    Start-Process (Join-Path $installed 'PCAutobackup.exe')
} catch {
    ('FAIL: ' + $_.Exception.Message) | Set-Content "$results\automated-result.txt"
    throw
} finally { Stop-Transcript }
