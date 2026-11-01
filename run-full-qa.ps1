<#
Developer only. WARNING: launches the app about 45 times, one window per UI test, for roughly ten minutes.
Do not use the PC meanwhile. Use an interactive desktop session, ideally a VM.

Writes tests\artifacts\TestResults.json, TestReport.html and screenshots. Exits nonzero on failure.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe' }
$out = Join-Path $PSScriptRoot 'tests\artifacts'
$raw = Join-Path $out 'raw'
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }

& $dotnet build BF6Optimizer.sln -c Debug -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet test tests\BF6Optimizer.Tests -c Debug --no-build --logger 'trx;LogFileName=unit.trx' --results-directory $raw
$unit = $LASTEXITCODE
& $dotnet test tests\BF6Optimizer.UiTests -c Debug --no-build --logger 'trx;LogFileName=ui.trx' --results-directory $raw
$ui = $LASTEXITCODE

$results = foreach ($file in Get-ChildItem $raw -Filter *.trx) {
    [xml]$trx = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($r in $trx.TestRun.Results.UnitTestResult) {
        [pscustomobject]@{
            Test = $r.testName; Suite = $file.BaseName; Outcome = $r.outcome; Duration = $r.duration; Started = $r.startTime
            Failure = if ($r.Output.ErrorInfo) { [string]$r.Output.ErrorInfo.Message } else { '' }
        }
    }
}

$notExecuted = @('3840x2160 viewport', 'Windows display scaling at 125, 150, 175 and 200 percent', 'Moving between monitors with different DPI', 'Clean Windows 10/11 machine')
[pscustomobject]@{ Generated = (Get-Date).ToString('o'); Environment = [Environment]::OSVersion.VersionString; NotExecuted = $notExecuted; Results = @($results) } |
    ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out 'TestResults.json') -Encoding UTF8

$rows = ($results | ForEach-Object { "<tr><td>$([Net.WebUtility]::HtmlEncode($_.Test))</td><td>$($_.Suite)</td><td>$($_.Outcome)</td><td>$($_.Duration)</td><td>$([Net.WebUtility]::HtmlEncode($_.Failure))</td></tr>" }) -join ''
$passed = @($results | Where-Object Outcome -eq 'Passed').Count
"<!DOCTYPE html><meta charset=utf-8><title>QA</title><h1>$passed / $(@($results).Count) passed</h1><p>Not executed: $($notExecuted -join '; ')</p><table border=1 cellpadding=4><tr><th>Test</th><th>Suite</th><th>Outcome</th><th>Duration</th><th>Failure</th></tr>$rows</table>" |
    Set-Content (Join-Path $out 'TestReport.html') -Encoding UTF8

if ($unit -ne 0 -or $ui -ne 0) { exit 1 }
