<#
Developer only. Publishes this repository and its release. Run after `gh auth login` as muratbulat.
Stops on any failed check. Never force-pushes and never overwrites a tag or release.
#>
[CmdletBinding()]
param([string]$Tag = 'v0.1.0-beta.1')

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $repo
$slug = 'muratbulat/BF6-Universal-Optimizer'

function Fail([string]$m) { Write-Host "[FAIL] $m" -ForegroundColor Red; exit 1 }
function Check([string]$what) { if ($LASTEXITCODE -ne 0) { Fail "$what failed." } }

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { Fail 'GitHub CLI not found. Run: winget install GitHub.cli' }
$login = gh api user --jq .login
if ($login -ne 'muratbulat') { Fail 'Sign in as muratbulat with: gh auth login' }
if (git status --porcelain) { Fail 'Working tree has uncommitted changes.' }

& (Join-Path $PSScriptRoot 'check-publication-safety.ps1')
Check 'Publication safety check'

foreach ($f in 'Release\BF6Optimizer.exe', 'Release\SHA256SUMS.txt') { if (-not (Test-Path -LiteralPath $f)) { Fail "Missing $f. Run scripts\build-release.ps1 first." } }
$sum = (Get-FileHash -Algorithm SHA256 -LiteralPath Release\BF6Optimizer.exe).Hash
if (-not (Select-String -LiteralPath Release\SHA256SUMS.txt -SimpleMatch $sum -Quiet)) { Fail 'SHA256SUMS.txt does not match the executable.' }

$env:GH_PAGER = ''
$exists = $true
gh repo view $slug --json name 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { $exists = $false }

if ($exists) {
    Write-Host "$slug already exists. Inspect it first; this script does not overwrite existing repositories." -ForegroundColor Yellow
    Fail 'Repository exists.'
}

gh repo create $slug --public --description 'Native Windows gaming performance analyzer and Battlefield 6 optimizer with hardware diagnostics, network latency testing, and portable deployment.'
Check 'Repository creation'
git remote add origin "https://github.com/$slug.git"
git push -u origin main
Check 'Push'
gh repo edit $slug --add-topic battlefield --add-topic battlefield-6 --add-topic gaming --add-topic windows --add-topic optimization --add-topic hardware-monitoring --add-topic network-diagnostics --add-topic dotnet --add-topic csharp --add-topic wpf
Check 'Topics'

Write-Host 'Waiting for CI...'
Start-Sleep -Seconds 15
gh run watch --repo $slug --exit-status (gh run list --repo $slug --workflow ci.yml --limit 1 --json databaseId --jq '.[0].databaseId')
Check 'CI'

gh release create $Tag Release\BF6Optimizer.exe Release\SHA256SUMS.txt --repo $slug --title "BF6 Universal Optimizer $Tag" --notes-file "docs\release-notes\$Tag.md" --prerelease --target main
Check 'Release'

$check = Join-Path $env:TEMP "bf6-release-$([guid]::NewGuid().ToString('N'))"
gh release download $Tag --repo $slug --pattern BF6Optimizer.exe --dir $check
Check 'Asset download'
if ((Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $check 'BF6Optimizer.exe')).Hash -ne $sum) { Fail 'Downloaded asset does not match.' }
Remove-Item -LiteralPath $check -Recurse -Force
Write-Host "[ OK ] https://github.com/$slug/releases/tag/$Tag" -ForegroundColor Green
