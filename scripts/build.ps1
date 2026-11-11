param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [string]$OutputDirectory = '',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot "artifacts\PCAutobackup-$Runtime" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
function Assert-NoOutputReparsePoint([string]$Path) {
    for ($current = $Path; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Build output path contains a reparse point: $current"
        }
    }
}
Assert-NoOutputReparsePoint $OutputDirectory
# Publishing over an earlier package would ship files that no longer belong to the build.
# Only a package directory below artifacts\ is replaced; any other non-empty directory is refused and left untouched.
$replaceOutput = (Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count
if ($replaceOutput) {
    $artifacts = (Join-Path $repoRoot 'artifacts') + [IO.Path]::DirectorySeparatorChar
    if (-not $OutputDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar).StartsWith($artifacts, [StringComparison]::OrdinalIgnoreCase)) { throw "Output directory is not empty: $OutputDirectory. Use a new or empty directory." }
}
$localSdk = Join-Path $env:LOCALAPPDATA 'PCAutobackup\dotnet-sdk\dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
Push-Location $repoRoot
try {
    if (-not $SkipTests) {
        & $dotnetCommand test 'tests/PCAutobackup.Tests/PCAutobackup.Tests.csproj' -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    # The earlier package stays until the tests have passed.
    Assert-NoOutputReparsePoint $OutputDirectory
    if ($replaceOutput) { [IO.Directory]::Delete($OutputDirectory, $true) }
    & $dotnetCommand publish 'src/PCAutobackup.Desktop/PCAutobackup.Desktop.csproj' -c Release -r $Runtime --self-contained true -o $OutputDirectory -p:PublishSingleFile=false
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
    & $dotnetCommand publish 'src/PCAutobackup.Worker/PCAutobackup.Worker.csproj' -c Release -r $Runtime --self-contained true -o (Join-Path $OutputDirectory 'Worker') -p:PublishSingleFile=false
    if ($LASTEXITCODE -ne 0) { throw 'Worker publish failed.' }
    & $dotnetCommand publish 'src/PCAutobackup.Tray/PCAutobackup.Tray.csproj' -c Release -r $Runtime --self-contained true -o (Join-Path $OutputDirectory 'Tray') -p:PublishSingleFile=false
    if ($LASTEXITCODE -ne 0) { throw 'Tray publish failed.' }
    New-Item -ItemType Directory -Path (Join-Path $OutputDirectory 'Assets') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'assets\app.ico') -Destination (Join-Path $OutputDirectory 'Assets\app.ico') -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install.cmd'), (Join-Path $PSScriptRoot 'install.ps1') -Destination $OutputDirectory -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $OutputDirectory
    Copy-Item -LiteralPath (Join-Path $repoRoot 'Contex.md') -Destination $OutputDirectory
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs') -Destination $OutputDirectory -Recurse -Force
    Write-Output "Ready: $(Join-Path $OutputDirectory 'PCAutobackup.exe')"
    Write-Output 'Keep the application folder at a stable location after enabling the schedule.'
}
finally { Pop-Location }
