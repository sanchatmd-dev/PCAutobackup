param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [string]$OutputDirectory = '',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot "artifacts\PCAutobackup-$Runtime" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$localSdk = Join-Path $env:LOCALAPPDATA 'PCAutobackup\dotnet-sdk\dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
Push-Location $repoRoot
try {
    if (-not $SkipTests) {
        & $dotnetCommand test 'tests/PCAutobackup.Tests/PCAutobackup.Tests.csproj' -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    & $dotnetCommand publish 'src/PCAutobackup.Desktop/PCAutobackup.Desktop.csproj' -c Release -r $Runtime --self-contained true -o $OutputDirectory -p:PublishSingleFile=false
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
    & $dotnetCommand publish 'src/PCAutobackup.Worker/PCAutobackup.Worker.csproj' -c Release -r $Runtime --self-contained true -o (Join-Path $OutputDirectory 'Worker') -p:PublishSingleFile=false
    if ($LASTEXITCODE -ne 0) { throw 'Worker publish failed.' }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $OutputDirectory
    Copy-Item -LiteralPath (Join-Path $repoRoot 'Contex.md') -Destination $OutputDirectory
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs') -Destination $OutputDirectory -Recurse -Force
    Write-Output "Ready: $(Join-Path $OutputDirectory 'PCAutobackup.exe')"
    Write-Output 'Keep the application folder at a stable location after enabling the schedule.'
}
finally { Pop-Location }
