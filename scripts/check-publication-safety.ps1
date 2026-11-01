<#
Developer only. Local checks before publishing; nothing is uploaded.
Matched values are never printed, only the file and the kind of finding.
Static scanning is not perfect, so review the result too. Exits nonzero on critical findings.
#>
[CmdletBinding()]
param()

$repo = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $repo
$critical = New-Object System.Collections.Generic.List[string]
function Critical([string]$m) { $critical.Add($m); Write-Host "[CRIT] $m" -ForegroundColor Red }
function Ok([string]$m) { Write-Host "[ OK ] $m" -ForegroundColor Green }

$files = @(git ls-files --cached)
$self = 'scripts/check-publication-safety.ps1'
$textFiles = $files | Where-Object { $_ -ne $self -and $_ -notmatch '\.(png|ico|gif|jpg)$' -and (Test-Path -LiteralPath $_) }

# Secret shapes
$secretPatterns = @{
    'GitHub token'   = 'gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}'
    'AWS key'        = 'AKIA[0-9A-Z]{16}'
    'Private key'    = '-----BEGIN [A-Z ]*PRIVATE KEY-----'
    'Assigned secret'= '(?i)(password|passwd|secret|api[_-]?key|token)\s*[:=]\s*["''][^"'']{8,}["'']'
}
# Values taken from this machine at run time, so the script itself stores none of them.
$local = @{}
if ($env:USERPROFILE) { $local['profile path'] = [regex]::Escape($env:USERPROFILE); $local['profile path (slashes)'] = [regex]::Escape($env:USERPROFILE.Replace('\', '/')) }
# Very short names match ordinary text, so they are not usable as a signal.
if ($env:COMPUTERNAME.Length -ge 4) { $local['computer name'] = '\b' + [regex]::Escape($env:COMPUTERNAME) + '\b' }
$local['workspace path'] = [regex]::Escape($repo)
$local['documents path'] = [regex]::Escape([Environment]::GetFolderPath('MyDocuments'))
foreach ($n in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
    $ipProps = $n.GetIPProperties()
    foreach ($a in @($ipProps.UnicastAddresses.Address) + @($ipProps.GatewayAddresses.Address) + @($ipProps.DnsAddresses)) {
        if ($a -and $a.AddressFamily -eq 'InterNetwork' -and -not [Net.IPAddress]::IsLoopback($a)) { $local["local address $a"] = '\b' + [regex]::Escape($a.ToString()) + '\b' }
    }
    $mac = $n.GetPhysicalAddress().ToString()
    if ($mac.Length -eq 12) { $local['MAC address'] = ($mac -replace '(..)(?!$)', '$1[-:]?') }
}
# CPU and GPU model names are generic in code and tests, so they only count inside documents.
$docsOnly = @{}
foreach ($wmi in @(@('Win32_Processor', 'Name'), @('Win32_VideoController', 'Name')) ) {
    Get-CimInstance $wmi[0] -ErrorAction SilentlyContinue | ForEach-Object {
        $v = ([string]$_.($wmi[1])).Trim() -replace '^(AMD|Intel\(R\)|NVIDIA GeForce|NVIDIA)\s+', '' -replace '\s+\d+-Core Processor$', ''
        if ($v.Length -ge 6) { $docsOnly["hardware model ($($wmi[0]))"] = [regex]::Escape($v) }
    }
}
foreach ($wmi in @(@('Win32_BaseBoard', 'Product'), @('Win32_BaseBoard', 'SerialNumber'), @('Win32_BIOS', 'SerialNumber'), @('Win32_PhysicalMemory', 'PartNumber'))) {
    Get-CimInstance $wmi[0] -ErrorAction SilentlyContinue | ForEach-Object {
        $v = ([string]$_.($wmi[1])).Trim()
        if ($v.Length -ge 6 -and $v -notmatch '^(Default string|To be filled|None|Unknown)') { $local["hardware id ($($wmi[0]).$($wmi[1]))"] = [regex]::Escape($v) }
    }
}

foreach ($f in $textFiles) {
    $text = [IO.File]::ReadAllText((Join-Path $repo $f))
    foreach ($k in $secretPatterns.Keys) { if ($text -match $secretPatterns[$k]) { Critical "$k in $f" } }
    foreach ($k in $local.Keys) { if ($text -match $local[$k]) { Critical "$k in $f" } }
    if ($f -match '\.md$') { foreach ($k in $docsOnly.Keys) { if ($text -match $docsOnly[$k]) { Critical "$k in $f" } } }
    if ($text -match '(?i)[A-Z]:\\Users\\(?!Public|alice|samplename|<)[A-Za-z0-9._-]+') { Critical "absolute user path in $f" }
}
Ok "$($textFiles.Count) text files scanned"

# Same local checks over every commit
$revs = @(git rev-list --all)
foreach ($r in $revs) {
    foreach ($k in $secretPatterns.Keys) { if (git grep -lIE ($secretPatterns[$k] -replace '\(\?i\)', '') $r -- . ":(exclude)$self" 2>$null) { Critical "$k in history $($r.Substring(0, 7))" } }
    foreach ($k in $local.Keys) {
        $pattern = $local[$k] -replace '\\b', ''
        if (git grep -lIP -- $pattern $r -- . ":(exclude)$self" 2>$null) { Critical "$k in history $($r.Substring(0, 7))" }
    }
}
foreach ($r in $revs) {
    foreach ($k in $docsOnly.Keys) { if (git grep -lIP -- $docsOnly[$k] $r -- '*.md' 2>$null) { Critical "$k in history $($r.Substring(0, 7))" } }
}
Ok "$($revs.Count) commits scanned"

# Files that must never be tracked
$banned = '(?i)(^|/)(bin|obj|\.vs|TestResults|artifacts|Backups|Logs|Reports|Release)/|\.(log|dmp|mdmp|db|db-shm|db-wal|sqlite3?|pfx|p12|key|pem|snk|pdb|user|trx|zip)$|(^|/)\.env|PROFSAVE'
foreach ($f in $files) { if ($f -match $banned) { Critical "tracked runtime or private file: $f" } }
Ok "tracked file names checked"

foreach ($p in 'bin/', 'obj/', '*.log', '*.db', 'Release/', '*.pfx') {
    if (-not (Select-String -LiteralPath .gitignore -SimpleMatch $p -Quiet)) { Critical ".gitignore lacks $p" }
}
Ok ".gitignore checked"

# PNG metadata: only image chunks are allowed
foreach ($f in ($files | Where-Object { $_ -match '\.png$' })) {
    $b = [IO.File]::ReadAllBytes((Join-Path $repo $f))
    $i = 8
    while ($i + 8 -le $b.Length) {
        $len = [BitConverter]::ToInt32([byte[]]($b[($i + 3)..$i]), 0)
        $type = [Text.Encoding]::ASCII.GetString($b, $i + 4, 4)
        if ($type -in 'tEXt', 'iTXt', 'zTXt', 'eXIf', 'tIME') { Critical "metadata chunk $type in $f" }
        $i += 12 + $len
    }
}
Ok "screenshot metadata checked"

# Commit identity
$name = git config --get user.name
$email = git config --get user.email
$bad = '(?i)claude|anthropic|codex|chatgpt|openai|\bcopilot\b|ai assistant|ai agent'
foreach ($line in (git log --all --format='%an%x09%ae%x09%cn%x09%ce')) {
    $p = $line -split "`t"
    if ($p[0] -ne $name -or $p[1] -ne $email -or $p[2] -ne $name -or $p[3] -ne $email) { Critical 'commit identity differs from the configured user' }
}
foreach ($line in (git log --all --format='%(trailers:key=Co-authored-by,valueonly)')) {
    if ($line.Trim().Length -gt 0) { Critical 'commit has a co-author trailer' }
}
if ((git log --all --format='%an %ae %cn %ce %B') -match $bad) { Critical 'AI name in commit metadata' }
Ok "commit metadata checked"

if ($critical.Count -gt 0) {
    Write-Host "$($critical.Count) critical finding(s)." -ForegroundColor Red
    exit 1
}
Write-Host 'No critical findings. Review staged content yourself before publishing.' -ForegroundColor Green
