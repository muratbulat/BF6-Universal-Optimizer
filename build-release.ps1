<#
Developer release script. End users never need this: they only run BF6Optimizer.exe.

Restores, builds, tests, publishes the self-contained single EXE, validates the publish output,
copies the EXE to Release\ and writes a manifest to artifacts\. Exits nonzero on any failure.
#>
[CmdletBinding()]
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

function Fail([string]$Message) {
    Write-Host "[FAIL] $Message" -ForegroundColor Red
    exit 1
}

function Step([string]$Message) { Write-Host "[ .. ] $Message" -ForegroundColor Cyan }

function Run([string]$What) {
    # Native tools do not throw; check each exit code.
    if ($LASTEXITCODE -ne 0) { Fail "$What failed with exit code $LASTEXITCODE." }
}

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
    $candidate = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $candidate) { $dotnet = $candidate }
}
if (-not $dotnet) { Fail 'The .NET 10 SDK was not found.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$project = 'src\BF6Optimizer.App\BF6Optimizer.App.csproj'
$publishDir = Join-Path $PSScriptRoot 'artifacts\publish'
$releaseDir = Join-Path $PSScriptRoot 'Release'
$manifestPath = Join-Path $PSScriptRoot 'artifacts\release-manifest.json'

Step 'Restoring NuGet packages'
& $dotnet restore BF6Optimizer.sln
Run 'Restore'

Step 'Building Release'
& $dotnet build BF6Optimizer.sln -c Release --no-restore -v q
Run 'Build'

$testSummary = 'skipped'
if (-not $SkipTests) {
    Step 'Running automated tests'
    & $dotnet test tests\BF6Optimizer.Tests\BF6Optimizer.Tests.csproj -c Release --no-build
    Run 'Tests'
    $testSummary = 'passed'
}

Step 'Publishing self-contained single file'
if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
& $dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -o $publishDir
Run 'Publish'

Step 'Validating publish output'
$exe = Join-Path $publishDir 'BF6Optimizer.exe'
if (-not (Test-Path -LiteralPath $exe)) { Fail 'BF6Optimizer.exe was not produced.' }

$unexpected = @(Get-ChildItem -LiteralPath $publishDir -Recurse -File | Where-Object { $_.Name -ne 'BF6Optimizer.exe' })
if ($unexpected.Count -gt 0) {
    $unexpected | ForEach-Object { Write-Host "       $($_.FullName)" }
    Fail "Publish folder holds $($unexpected.Count) unexpected file(s); this is not a single-file output."
}

$item = Get-Item -LiteralPath $exe
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $exe).Hash
$sizeMb = [math]::Round($item.Length / 1MB, 1)

Step 'Copying to Release'
if (Test-Path -LiteralPath $releaseDir) { Remove-Item -LiteralPath $releaseDir -Recurse -Force }
New-Item -ItemType Directory -Path $releaseDir | Out-Null
Copy-Item -LiteralPath $exe -Destination $releaseDir

$copy = Join-Path $releaseDir 'BF6Optimizer.exe'
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $copy).Hash -ne $hash) { Fail 'Release copy does not match the published file.' }

[pscustomobject]@{
    Product      = 'BF6 Universal Optimizer'
    Author       = 'Murat Bulat'
    Version      = (Get-Item -LiteralPath $copy).VersionInfo.ProductVersion
    Runtime      = 'win-x64, self-contained, single file'
    BuiltAt      = (Get-Date).ToString('o')
    Tests        = $testSummary
    File         = 'BF6Optimizer.exe'
    SizeBytes    = $item.Length
    SHA256       = $hash
} | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host "[ OK ] Release\BF6Optimizer.exe  $sizeMb MB" -ForegroundColor Green
Write-Host "[ OK ] SHA-256 $hash" -ForegroundColor Green
Write-Host "[ OK ] Manifest: artifacts\release-manifest.json" -ForegroundColor Green
