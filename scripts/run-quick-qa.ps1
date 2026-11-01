<#
Developer only. Build and unit tests; opens no windows.
Add -Ui for the main-window and navigation smoke tests, which open the app several times.
#>
[CmdletBinding()]
param([switch]$Ui)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $repo
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe' }

& $dotnet build BF6Optimizer.sln -c Debug -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet test tests\BF6Optimizer.Tests -c Debug --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Ui) {
    & $dotnet test tests\BF6Optimizer.UiTests -c Debug --no-build --filter 'FullyQualifiedName~ShellTests'
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
