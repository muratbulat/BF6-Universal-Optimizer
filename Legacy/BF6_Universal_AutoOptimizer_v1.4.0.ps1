#Requires -Version 5.1
<#
BF6 Universal Auto Optimizer v1.4.0
Portable, reversible, per-user Battlefield 6 optimizer for Windows 10/11. No admin required.

Modes:
  Analyze         Detect hardware and show the plan. No file report is created.
  Apply           No UAC. Create snapshot, change HKCU + BF6 profile, verify, report.
  Status          Verify current state and overwrite Latest_Status.json.
  RestoreLast     Restore the most recent pre-Apply snapshot for this machine.
  RestoreOriginal Restore the first baseline captured by this optimizer line for this machine.

Goals:
  Auto, Competitive, Balanced, Quality

Safety exclusions:
- no Secure Boot / TPM / VBS / HVCI disabling
- no HAGS/HKLM modifications or administrator elevation
- no HPET / timer-resolution / Nagle / service-debloat tweaks
- no CPU overclock / undervolt / PBO changes
- no GPU power-limit changes
- no NIC interrupt/offload registry hacks
- no vendor-specific laptop MUX/Turbo automation
#>

[CmdletBinding()]
param(
    [ValidateSet('Analyze','Apply','Status','RestoreLast','RestoreOriginal','OpenBackups','OpenReports')]
    [string]$Mode = 'Analyze',

    [ValidateSet('Auto','Competitive','Balanced','Quality')]
    [string]$Goal = 'Auto',

    [ValidateRange(0,1000)]
    [int]$FpsCap = 0
)

$ErrorActionPreference = 'Stop'
$ScriptVersion = '1.4.0'
$script:RebootRecommended = $false
$script:Warnings = New-Object System.Collections.Generic.List[string]
$script:LastPatchResult = $null
$script:Stage = 'Startup'
$script:SnapshotPath = $null
$script:FactsForError = $null
$script:PlanForError = $null
$script:RolledBack = $false
$script:Root = Split-Path -Parent $PSCommandPath
if ([string]::IsNullOrWhiteSpace($script:Root)) { $script:Root = (Get-Location).Path }

function Info([string]$s) { Write-Host "[INFO] $s" -ForegroundColor Cyan }
function Ok([string]$s)   { Write-Host "[ OK ] $s" -ForegroundColor Green }
function Warn([string]$s) { Write-Host "[WARN] $s" -ForegroundColor Yellow; $script:Warnings.Add($s) | Out-Null }
function Fail([string]$s) { Write-Host "[FAIL] $s" -ForegroundColor Red }

function Sanitize-Name([string]$s) {
    if ([string]::IsNullOrWhiteSpace($s)) { return 'Unknown' }
    return ($s -replace '[^A-Za-z0-9._-]','_').Trim('_')
}

function Docs-Path {
    $p = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
    if ([string]::IsNullOrWhiteSpace($p)) { $p = Join-Path $env:USERPROFILE 'Documents' }
    return $p
}

function Machine-Key {
    try {
        $uuid = (Get-CimInstance Win32_ComputerSystemProduct -ErrorAction Stop).UUID
        if ($uuid -and $uuid -notmatch '^0{8}-0{4}-0{4}-0{4}-0{12}$' -and $uuid -notmatch '^(F{8}-F{4}-F{4}-F{4}-F{12})$') {
            return ('Machine_' + (Sanitize-Name $uuid))
        }
    } catch {}
    return ('Machine_' + (Sanitize-Name $env:COMPUTERNAME))
}

function Test-DataFolderWritable([string]$Path) {
    try {
        if (-not (Test-Path -LiteralPath $Path)) { New-Item -ItemType Directory -Path $Path -Force -ErrorAction Stop | Out-Null }
        $probe = Join-Path $Path ('.bf6write_' + [guid]::NewGuid().ToString('N'))
        try { [IO.File]::WriteAllText($probe, 'probe'); return $true }
        finally { if (Test-Path -LiteralPath $probe) { Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue } }
    } catch { return $false }
}
function Initialize-Storage {
    $script:MachineKey = Machine-Key
    if ($Mode -eq 'Analyze') {
        $script:DataRoot = $script:Root
        return
    }
    if (Test-DataFolderWritable $script:Root) { $script:DataRoot = $script:Root }
    else {
        $local = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA } else { Join-Path $env:USERPROFILE 'AppData\Local' }
        $fallback = Join-Path $local 'BF6UniversalOptimizer'
        if (-not (Test-DataFolderWritable $fallback)) { throw 'Cannot write package directory or LocalAppData data directory.' }
        $script:DataRoot = $fallback
        Warn "Package directory not writable. Data stored at: $fallback"
    }
    $script:BackupRoot = Join-Path (Join-Path $script:DataRoot 'Backups') $script:MachineKey
    $script:ReportRoot = Join-Path (Join-Path $script:DataRoot 'Reports') $script:MachineKey
    $script:HistoryRoot = Join-Path $script:ReportRoot 'History'
    foreach ($p in @($script:BackupRoot,$script:ReportRoot,$script:HistoryRoot)) {
        if (-not (Test-Path -LiteralPath $p)) { New-Item -ItemType Directory -Path $p -Force -ErrorAction Stop | Out-Null }
    }
}

function Reg-State([string]$Path,[string]$Name) {
    $exists = $false; $value = $null
    if (Test-Path $Path) {
        try { $value = (Get-ItemProperty -Path $Path -Name $Name -ErrorAction Stop).$Name; $exists = $true } catch {}
    }
    [pscustomobject]@{Path=$Path;Name=$Name;Exists=$exists;Value=$value}
}

function Set-Dword([string]$Path,[string]$Name,[int]$Value) {
    if (-not (Test-Path $Path)) { New-Item -Path $Path -Force | Out-Null }
    New-ItemProperty -Path $Path -Name $Name -PropertyType DWord -Value $Value -Force | Out-Null
}

function Restore-Reg($s) {
    if ($s.Exists) {
        if (-not (Test-Path $s.Path)) { New-Item -Path $s.Path -Force | Out-Null }
        New-ItemProperty -Path $s.Path -Name $s.Name -PropertyType DWord -Value ([int]$s.Value) -Force | Out-Null
    } elseif (Test-Path $s.Path) {
        Remove-ItemProperty -Path $s.Path -Name $s.Name -ErrorAction SilentlyContinue
    }
}

function Find-BF6Roots {
    $roots = New-Object System.Collections.Generic.List[string]
    $candidates = @((Docs-Path))
    if ($env:OneDrive) { $candidates += (Join-Path $env:OneDrive 'Documents') }
    if ($env:OneDriveConsumer) { $candidates += (Join-Path $env:OneDriveConsumer 'Documents') }
    if ($env:OneDriveCommercial) { $candidates += (Join-Path $env:OneDriveCommercial 'Documents') }
    foreach ($docs in @($candidates | Select-Object -Unique)) {
        $p = Join-Path $docs 'Battlefield 6\settings'
        if (Test-Path -LiteralPath $p) {
            $rp = (Resolve-Path -LiteralPath $p).Path
            if (-not $roots.Contains($rp)) { $roots.Add($rp) }
        }
    }
    return @($roots)
}

function Get-BF6Profiles {
    $names = @('PROFSAVE_profile','PROFSAVEbf6mp_profile','PROFSAVEbf6sp_profile')
    $out = New-Object System.Collections.Generic.List[System.IO.FileInfo]
    foreach ($root in Find-BF6Roots) {
        foreach ($f in Get-ChildItem -LiteralPath $root -File -Recurse -ErrorAction SilentlyContinue) {
            if ($names -contains $f.Name) { $out.Add($f) }
        }
    }
    return @($out | Sort-Object FullName -Unique)
}

function Get-Setting([string]$Text,[string]$Key) {
    $pattern = '(?m)^[ \t]*' + [regex]::Escape($Key) + '[ \t]+([^\r\n]*)'
    $m = [regex]::Match($Text, $pattern)
    if ($m.Success) { return $m.Groups[1].Value.Trim() }
    return $null
}

function Set-Setting([string]$Text,[string]$Key,[string]$Value) {
    $pattern = '(?m)^([ \t]*)' + [regex]::Escape($Key) + '[ \t]+[^\r\n]*'
    $rx = New-Object System.Text.RegularExpressions.Regex($pattern)
    if (-not $rx.IsMatch($Text)) { return $Text }
    $eval = [System.Text.RegularExpressions.MatchEvaluator]{ param($m) ($m.Groups[1].Value + $Key + ' ' + $Value) }
    return $rx.Replace($Text, $eval)
}

function Read-TextFileInfo([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $offset = 0; $hasBom = $false; $enc = $null; $name = ''
    if ($bytes.Length -ge 4 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE -and $bytes[2] -eq 0 -and $bytes[3] -eq 0) {
        $enc = [Text.UTF32Encoding]::new($false,$true); $offset = 4; $hasBom = $true; $name = 'UTF32-LE'
    } elseif ($bytes.Length -ge 4 -and $bytes[0] -eq 0 -and $bytes[1] -eq 0 -and $bytes[2] -eq 0xFE -and $bytes[3] -eq 0xFF) {
        $enc = [Text.UTF32Encoding]::new($true,$true); $offset = 4; $hasBom = $true; $name = 'UTF32-BE'
    } elseif ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $enc = [Text.UTF8Encoding]::new($true); $offset = 3; $hasBom = $true; $name = 'UTF8-BOM'
    } elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
        $enc = [Text.Encoding]::Unicode; $offset = 2; $hasBom = $true; $name = 'UTF16-LE'
    } elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF) {
        $enc = [Text.Encoding]::BigEndianUnicode; $offset = 2; $hasBom = $true; $name = 'UTF16-BE'
    } else {
        $utf8 = [Text.UTF8Encoding]::new($false,$true)
        try { $null = $utf8.GetString($bytes); $enc = $utf8; $name = 'UTF8-NoBOM' }
        catch { $enc = [Text.Encoding]::Default; $name = 'ANSI-SystemCodePage' }
    }
    $text = $enc.GetString($bytes,$offset,$bytes.Length-$offset)
    $newline = if ($text.Contains("`r`n")) {'CRLF'} elseif ($text.Contains("`n")) {'LF'} else {'None'}
    return [pscustomobject]@{Text=$text;Encoding=$enc;EncodingName=$name;HasBom=$hasBom;NewLine=$newline;ByteLength=$bytes.Length}
}

function Write-Atomic([string]$Path,[string]$Text,$Encoding,[bool]$HasBom) {
    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($item.IsReadOnly) { throw "BF6 profile is read-only: $Path. Remove Read-only attribute and retry." }
    $tmp = Join-Path $item.DirectoryName ('.' + $item.Name + '.' + [guid]::NewGuid().ToString('N') + '.bf6tmp')
    try {
        $body = $Encoding.GetBytes($Text)
        if ($HasBom) {
            $pre = $Encoding.GetPreamble()
            $all = New-Object byte[] ($pre.Length + $body.Length)
            [Array]::Copy($pre,0,$all,0,$pre.Length)
            [Array]::Copy($body,0,$all,$pre.Length,$body.Length)
        } else { $all = $body }
        [IO.File]::WriteAllBytes($tmp,$all)
        try { [IO.File]::Replace($tmp,$Path,$null) }
        catch {
            # On drives that do not implement Replace, Copy is the best-effort fallback;
            # SHA256 verification and pre-write snapshots still protect against data loss.
            [IO.File]::Copy($tmp,$Path,$true)
        }
    } finally {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
    }
}

function File-Hash([string]$Path) {
    if (-not (Test-Path $Path)) { return $null }
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
}

function Gpu-Vendor([string]$Name) {
    if ($Name -match '(?i)NVIDIA|GeForce') { return 'NVIDIA' }
    if ($Name -match '(?i)Radeon|AMD') { return 'AMD' }
    if ($Name -match '(?i)Intel.*Arc|\bArc\b') { return 'Intel' }
    if ($Name -match '(?i)Intel') { return 'Intel-iGPU' }
    return 'Unknown'
}

function Nvidia-Rows {
    $cmd = Get-Command nvidia-smi.exe -ErrorAction SilentlyContinue
    if (-not $cmd) { return @() }

    # Query identity/VRAM separately from power fields. Some laptop drivers expose
    # memory.total but reject one or more power.* fields; v1.3.1 then discarded the
    # entire row and fell back to the often-truncated WMI AdapterRAM value (~4 GB).
    try {
        $coreLines = @(& nvidia-smi.exe '--query-gpu=index,name,driver_version,memory.total' '--format=csv,noheader,nounits' 2>$null)
        $rows = @()
        foreach ($line in $coreLines) {
            $p = $line -split ',\s*'
            if ($p.Count -ge 4) {
                $rows += [pscustomobject]@{
                    Index=[int]$p[0];Name=$p[1];Driver=$p[2];VRAMMB=[double]$p[3]
                    PowerLimitW=$null;PowerDefaultW=$null;PowerMaxW=$null
                }
            }
        }
        if (-not $rows) { return @() }

        # Best-effort power query. Failure here must never invalidate VRAM detection.
        $powerMap = @{}
        try {
            $powerLines = @(& nvidia-smi.exe '--query-gpu=index,power.limit,power.default_limit,power.max_limit' '--format=csv,noheader,nounits' 2>$null)
            foreach ($line in $powerLines) {
                $p = $line -split ',\s*'
                if ($p.Count -ge 4 -and $p[0] -match '^\d+$') {
                    $powerMap[[int]$p[0]] = @($p[1],$p[2],$p[3])
                }
            }
        } catch {
            try {
                $powerLines = @(& nvidia-smi.exe '--query-gpu=index,power.limit,power.default_limit' '--format=csv,noheader,nounits' 2>$null)
                foreach ($line in $powerLines) {
                    $p = $line -split ',\s*'
                    if ($p.Count -ge 3 -and $p[0] -match '^\d+$') {
                        $powerMap[[int]$p[0]] = @($p[1],$p[2],$null)
                    }
                }
            } catch {}
        }

        foreach ($row in $rows) {
            if ($powerMap.ContainsKey($row.Index)) {
                $vals=$powerMap[$row.Index]
                foreach($pair in @(@('PowerLimitW',0),@('PowerDefaultW',1),@('PowerMaxW',2))){
                    $raw=$vals[$pair[1]]
                    if($null -ne $raw -and $raw -match '^-?\d+(\.\d+)?$'){$row.($pair[0])=[double]$raw}
                }
            }
        }
        return @($rows)
    } catch { return @() }
}

function Base-GpuScore([string]$Name) {
    $n = $Name.ToUpperInvariant(); $s = 1.0
    if     ($n -match 'RTX\s*5090') {$s=5.5}
    elseif ($n -match 'RTX\s*5080') {$s=5.0}
    elseif ($n -match 'RTX\s*5070\s*TI') {$s=4.3}
    elseif ($n -match 'RTX\s*5070') {$s=4.0}
    elseif ($n -match 'RTX\s*5060\s*TI') {$s=3.4}
    elseif ($n -match 'RTX\s*5060') {$s=3.0}
    elseif ($n -match 'RTX\s*5050') {$s=2.2}
    elseif ($n -match 'RTX\s*4090') {$s=5.2}
    elseif ($n -match 'RTX\s*4080') {$s=4.7}
    elseif ($n -match 'RTX\s*4070\s*TI') {$s=4.1}
    elseif ($n -match 'RTX\s*4070') {$s=3.7}
    elseif ($n -match 'RTX\s*4060\s*TI') {$s=3.1}
    elseif ($n -match 'RTX\s*4060') {$s=2.7}
    elseif ($n -match 'RTX\s*4050') {$s=2.1}
    elseif ($n -match 'RTX\s*3090\s*TI') {$s=4.3}
    elseif ($n -match 'RTX\s*3090') {$s=4.0}
    elseif ($n -match 'RTX\s*3080\s*TI') {$s=3.8}
    elseif ($n -match 'RTX\s*3080') {$s=3.5}
    elseif ($n -match 'RTX\s*3070\s*TI') {$s=3.1}
    elseif ($n -match 'RTX\s*3070') {$s=2.9}
    elseif ($n -match 'RTX\s*3060\s*TI') {$s=2.6}
    elseif ($n -match 'RTX\s*3060') {$s=2.2}
    elseif ($n -match 'RTX\s*3050') {$s=1.6}
    elseif ($n -match 'RTX\s*2080\s*TI') {$s=2.8}
    elseif ($n -match 'RTX\s*2080') {$s=2.4}
    elseif ($n -match 'RTX\s*2070') {$s=2.1}
    elseif ($n -match 'RTX\s*2060') {$s=1.7}
    elseif ($n -match 'RX\s*9070\s*XT') {$s=4.6}
    elseif ($n -match 'RX\s*9070') {$s=4.1}
    elseif ($n -match 'RX\s*7900\s*XTX') {$s=5.0}
    elseif ($n -match 'RX\s*7900\s*XT') {$s=4.4}
    elseif ($n -match 'RX\s*7900\s*GRE') {$s=3.8}
    elseif ($n -match 'RX\s*7800\s*XT') {$s=3.7}
    elseif ($n -match 'RX\s*7700\s*XT') {$s=3.2}
    elseif ($n -match 'RX\s*7600\s*XT') {$s=2.6}
    elseif ($n -match 'RX\s*7600') {$s=2.3}
    elseif ($n -match 'RX\s*6950\s*XT') {$s=3.9}
    elseif ($n -match 'RX\s*6900\s*XT') {$s=3.7}
    elseif ($n -match 'RX\s*6800\s*XT') {$s=3.4}
    elseif ($n -match 'RX\s*6800') {$s=3.1}
    elseif ($n -match 'RX\s*6750\s*XT') {$s=2.8}
    elseif ($n -match 'RX\s*6700\s*XT') {$s=2.6}
    elseif ($n -match 'RX\s*6650\s*XT') {$s=2.2}
    elseif ($n -match 'RX\s*6600\s*XT') {$s=2.1}
    elseif ($n -match 'RX\s*6600') {$s=1.9}
    elseif ($n -match 'RX\s*5700\s*XT') {$s=1.9}
    elseif ($n -match 'RX\s*5600\s*XT') {$s=1.6}
    elseif ($n -match 'ARC\s*B580') {$s=2.8}
    elseif ($n -match 'ARC\s*B570') {$s=2.4}
    elseif ($n -match 'ARC\s*A770') {$s=2.4}
    elseif ($n -match 'ARC\s*A750') {$s=2.2}
    elseif ($n -match 'ARC\s*A580') {$s=1.9}
    elseif ($n -match 'ARC\s*A380') {$s=1.3}
    elseif ($n -match 'RTX') {$s=2.0}
    elseif ($n -match 'RADEON\s+RX') {$s=2.0}
    elseif ($n -match 'ARC') {$s=1.7}
    elseif ($n -match 'GTX') {$s=1.2}
    return [math]::Round($s,2)
}

function Laptop-GpuPenalty([string]$Vendor,[double]$PowerMaxW) {
    if ($Vendor -ne 'NVIDIA') { return 0.35 }
    if ($PowerMaxW -ge 130) { return 0.30 }
    if ($PowerMaxW -ge 105) { return 0.40 }
    if ($PowerMaxW -ge 80)  { return 0.55 }
    if ($PowerMaxW -gt 0)   { return 0.75 }
    return 0.40
}

function Estimate-Vram([string]$GpuName,[string]$Vendor,$GpuObject,$NvidiaInfo) {
    if ($Vendor -eq 'NVIDIA' -and $NvidiaInfo -and $NvidiaInfo.VRAMMB -gt 0) {
        return [math]::Round($NvidiaInfo.VRAMMB/1024.0,1)
    }
    $n=$GpuName.ToUpperInvariant()
    # NVIDIA model fallbacks for systems where nvidia-smi is unavailable/restricted.
    if ($n -match 'RTX\s*4090.*LAPTOP') {return 16}
    if ($n -match 'RTX\s*4080.*LAPTOP') {return 12}
    if ($n -match 'RTX\s*4070.*LAPTOP') {return 8}
    if ($n -match 'RTX\s*4060.*LAPTOP') {return 8}
    if ($n -match 'RTX\s*4050.*LAPTOP') {return 6}
    if ($n -match 'RTX\s*5070\s*TI.*LAPTOP') {return 12}
    if ($n -match 'RTX\s*5070.*LAPTOP') {return 8}
    if ($n -match 'RTX\s*5060.*LAPTOP') {return 8}
    if ($n -match 'RTX\s*5050.*LAPTOP') {return 8}
    if ($n -match 'RX\s*7900\s*XTX') {return 24}
    if ($n -match 'RX\s*7900\s*XT') {return 20}
    if ($n -match 'RX\s*(9070|7900\s*GRE|7800\s*XT|7600\s*XT|6950\s*XT|6900\s*XT|6800)') {return 16}
    if ($n -match 'RX\s*(7700\s*XT|6750\s*XT|6700\s*XT)') {return 12}
    if ($n -match 'RX\s*(7600|6650\s*XT|6600\s*XT|6600|5700\s*XT)') {return 8}
    if ($n -match 'RX\s*5600\s*XT') {return 6}
    if ($n -match 'ARC\s*B580') {return 12}
    if ($n -match 'ARC\s*B570') {return 10}
    if ($n -match 'ARC\s*(A770)') {return 16}
    if ($n -match 'ARC\s*(A750|A580)') {return 8}
    if ($n -match 'ARC\s*A380') {return 6}
    try {
        if ($GpuObject.AdapterRAM) {
            $gb=[math]::Round(([double]$GpuObject.AdapterRAM/1GB),1)
            if ($gb -gt 0 -and $gb -le 4.1) { return $gb }
        }
    } catch {}
    return 8
}

function Preferred-Gpu($Gpus,[bool]$IsLaptop) {
    $nv = @(Nvidia-Rows)
    $c=@()
    foreach($g in $Gpus) {
        if ($g.Name -match '(?i)Microsoft Basic') {continue}
        $vendor=Gpu-Vendor $g.Name
        $base=Base-GpuScore $g.Name
        $nvi=$null
        if($vendor -eq 'NVIDIA' -and $nv) {
            $nvi=$nv | Where-Object { $_.Name -eq $g.Name } | Select-Object -First 1
            if(-not $nvi) {$nvi=$nv | Select-Object -First 1}
        }
        $score=$base
        if($IsLaptop -or $g.Name -match '(?i)LAPTOP|MOBILE') {
            $pmax=if($nvi){[double]$nvi.PowerMaxW}else{0}
            $score -= (Laptop-GpuPenalty $vendor $pmax)
        }
        $bonus=if($g.Name -match '(?i)GeForce\s+(RTX|GTX)|Radeon\s+RX|Intel.*Arc|\bArc\b'){10}else{0}
        $c += [pscustomobject]@{Object=$g;Name=$g.Name;Vendor=$vendor;Score=[math]::Max(0.5,[math]::Round($score,2));Rank=$score+$bonus;NvidiaInfo=$nvi}
    }
    if(-not $c){return $null}
    return ($c|Sort-Object Rank -Descending|Select-Object -First 1)
}

function Get-PrimaryDisplay {
    $w=0;$h=0;$hz=0;$device=$null
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        $screen=[System.Windows.Forms.Screen]::PrimaryScreen
        if($screen){$w=$screen.Bounds.Width;$h=$screen.Bounds.Height;$device=$screen.DeviceName}
        $code=@'
using System;
using System.Runtime.InteropServices;
public class DisplayModeNative {
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
  public struct DEVMODE {
    private const int CCHDEVICENAME = 32;
    private const int CCHFORMNAME = 32;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string dmDeviceName;
    public short dmSpecVersion; public short dmDriverVersion; public short dmSize; public short dmDriverExtra;
    public int dmFields; public int dmPositionX; public int dmPositionY; public int dmDisplayOrientation; public int dmDisplayFixedOutput;
    public short dmColor; public short dmDuplex; public short dmYResolution; public short dmTTOption; public short dmCollate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)] public string dmFormName;
    public short dmLogPixels; public int dmBitsPerPel; public int dmPelsWidth; public int dmPelsHeight;
    public int dmDisplayFlags; public int dmDisplayFrequency; public int dmICMMethod; public int dmICMIntent; public int dmMediaType;
    public int dmDitherType; public int dmReserved1; public int dmReserved2; public int dmPanningWidth; public int dmPanningHeight;
  }
  [DllImport("user32.dll", CharSet = CharSet.Ansi)] public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
}
'@
        if(-not ('DisplayModeNative' -as [type])){Add-Type -TypeDefinition $code -ErrorAction Stop}
        $dm=New-Object DisplayModeNative+DEVMODE
        $dm.dmSize=[Runtime.InteropServices.Marshal]::SizeOf($dm)
        if([DisplayModeNative]::EnumDisplaySettings($device,-1,[ref]$dm)){$w=[int]$dm.dmPelsWidth;$h=[int]$dm.dmPelsHeight;$hz=[int]$dm.dmDisplayFrequency}
    } catch {}
    if($w -le 0 -or $h -le 0 -or $hz -le 0){
        foreach($g in @(Get-CimInstance Win32_VideoController)){
            if($g.CurrentHorizontalResolution -and $g.CurrentVerticalResolution -and $w -le 0){$w=[int]$g.CurrentHorizontalResolution;$h=[int]$g.CurrentVerticalResolution}
            if($g.CurrentRefreshRate -and [int]$g.CurrentRefreshRate -gt $hz){$hz=[int]$g.CurrentRefreshRate}
        }
    }
    [pscustomobject]@{Width=$w;Height=$h;Refresh=$hz;Device=$device}
}

function System-Facts {
    $cs=Get-CimInstance Win32_ComputerSystem
    $cpu=Get-CimInstance Win32_Processor|Select-Object -First 1
    $os=Get-CimInstance Win32_OperatingSystem
    $bios=Get-CimInstance Win32_BIOS|Select-Object -First 1
    $gpus=@(Get-CimInstance Win32_VideoController)
    $battery=@(Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue)
    $mobile=@(2,8,9,10,14)
    $isLaptop=($battery.Count -gt 0)-or($mobile -contains [int]$cs.PCSystemType)
    $pref=Preferred-Gpu $gpus $isLaptop
    $display=Get-PrimaryDisplay
    $ram=0L;$speed=0
    foreach($m in @(Get-CimInstance Win32_PhysicalMemory)){$ram+=[int64]$m.Capacity;if($m.ConfiguredClockSpeed -and [int]$m.ConfiguredClockSpeed -gt $speed){$speed=[int]$m.ConfiguredClockSpeed}}
    $gpuName='Unknown';$vendor='Unknown';$score=0.5;$vram=0;$nvi=$null
    if($pref){$gpuName=$pref.Name;$vendor=$pref.Vendor;$score=$pref.Score;$nvi=$pref.NvidiaInfo;$vram=Estimate-Vram $gpuName $vendor $pref.Object $nvi}
    [pscustomobject]@{
        MachineKey=$script:MachineKey;Manufacturer=$cs.Manufacturer;Model=$cs.Model;IsLaptop=$isLaptop
        CPU=$cpu.Name.Trim();CpuCores=[int]$cpu.NumberOfCores;CpuThreads=[int]$cpu.NumberOfLogicalProcessors
        OS=$os.Caption;OSBuild=$os.BuildNumber;BIOS=$bios.SMBIOSBIOSVersion
        RAMGB=[math]::Round($ram/1GB,1);RAMSpeed=$speed
        GPU=$gpuName;GPUVendor=$vendor;GPUScore=$score;VRAMGB=$vram
        NvidiaDriver=if($nvi){$nvi.Driver}else{$null};NvidiaPowerLimitW=if($nvi){$nvi.PowerLimitW}else{$null};NvidiaPowerDefaultW=if($nvi){$nvi.PowerDefaultW}else{$null};NvidiaPowerMaxW=if($nvi){$nvi.PowerMaxW}else{$null}
        Width=$display.Width;Height=$display.Height;Refresh=$display.Refresh;DisplayDevice=$display.Device
    }
}

function Resolution-Penalty([int]$W,[int]$H) {
    if($W -le 0 -or $H -le 0){return 0}
    $factor=([double]$W*$H)/(1920.0*1080.0)
    if($factor -le 1){return 0}
    return [math]::Round((([math]::Log($factor)/[math]::Log(2))*0.60),2)
}

function Auto-Cap([int]$Hz,[double]$Score,[bool]$Laptop,[string]$ResolvedGoal) {
    if ($Hz -le 0) { return 0 } # Unknown refresh: preserve the current game's limiter.
    if ($Hz -le 60) { return $Hz }
    $ceiling = [math]::Max(60,$Hz-3)
    if ($ResolvedGoal -eq 'Competitive') { return $ceiling }
    if ($ResolvedGoal -eq 'Quality') { if ($Laptop) { return [math]::Min($ceiling,120) } else { return [math]::Min($ceiling,144) } }
    if ($Score -ge 3.5) { $t=180 } elseif ($Score -ge 2.5) { $t=165 } else { $t=120 }
    return [math]::Min($ceiling,$t)
}

function Build-Plan($Facts,[string]$RequestedGoal,[int]$ForcedCap) {
    $w=$Facts.Width;$h=$Facts.Height
    if($w -le 0 -or $h -le 0){
        $p=Get-BF6Profiles|Sort-Object LastWriteTime -Descending|Select-Object -First 1
        if($p){$t=[IO.File]::ReadAllText($p.FullName);$pw=Get-Setting $t 'GstRender.ResolutionWidth';$ph=Get-Setting $t 'GstRender.ResolutionHeight';if($pw -match '^\d+$'){$w=[int]$pw};if($ph -match '^\d+$'){$h=[int]$ph}}
    }
    if($w -le 0){$w=1920};if($h -le 0){$h=1080}
    $pen=Resolution-Penalty $w $h
    $eff=[math]::Max(0.3,[math]::Round(($Facts.GPUScore-$pen),2))
    $resolved=if($RequestedGoal -eq 'Auto'){'Competitive'}else{$RequestedGoal}
    if($eff -ge 3.8){$tier='High'}elseif($eff -ge 2.7){$tier='Medium'}elseif($eff -ge 1.7){$tier='Low'}else{$tier='VeryLow'}
    $cap=if($ForcedCap -gt 0){$ForcedCap}else{Auto-Cap $Facts.Refresh $eff $Facts.IsLaptop $resolved}
    if($Facts.VRAMGB -ge 16 -and $tier -eq 'High'){$texture=3}elseif($Facts.VRAMGB -ge 10){$texture=2}elseif($Facts.VRAMGB -ge 8){$texture=1}else{$texture=0}
    if($tier -eq 'High'){$mesh=2;$terrain=2;$sig=2}elseif($tier -eq 'Medium'){$mesh=2;$terrain=1;$sig=2}else{$mesh=1;$terrain=0;$sig=1}
    if($resolved -eq 'Quality'){$effects=1;$lighting=1;$localShadow=1;$shadow=1;$sun=1;$vol=1;$refl=1;$ssr=1;$ao=1;if($texture -lt 1){$texture=1}}
    elseif($resolved -eq 'Balanced'){$effects=0;$lighting=1;$localShadow=0;$shadow=0;$sun=0;$vol=0;$refl=0;$ssr=0;$ao=0}
    else{$effects=0;$lighting=0;$localShadow=0;$shadow=0;$sun=0;$vol=0;$refl=0;$ssr=0;$ao=0}
    $up='Preserve';$upq=$null;$px=[double]$w*$h
    if($Facts.GPUVendor -eq 'NVIDIA' -and $Facts.GPU -match '(?i)RTX'){
        if($px -ge (2560.0*1440.0)){$up='Preserve-NVIDIA'}
        elseif($eff -lt 2.0){$up='Preserve-NVIDIA'}
    }elseif($Facts.GPUVendor -eq 'AMD'){$up='Preserve-AMD'}elseif($Facts.GPUVendor -eq 'Intel'){$up='Preserve-Intel'}
    [pscustomobject]@{
        Goal=$resolved;Tier=$tier;Width=$w;Height=$h;Refresh=$Facts.Refresh;GPUScore=$Facts.GPUScore;ResolutionPenalty=$pen;EffectiveScore=$eff
        FpsCap=$cap;TextureQuality=$texture;TextureFiltering=2;MeshQuality=$mesh;TerrainQuality=$terrain;SignificanceQuality=$sig
        EffectsQuality=$effects;LightingQuality=$lighting;LocalLightShadowQuality=$localShadow;ShadowQuality=$shadow;SunShadowQuality=$sun
        VolumetricQuality=$vol;ReflectionQuality=$refl;ScreenSpaceReflections=$ssr;AmbientOcclusion=$ao;UpscalerAction=$up;UpscaleQuality=$upq
    }
}

function Plan-Settings($Facts,$Plan) {
    $s = [ordered]@{
        'GstRender.VSyncMode'='0'; 'GstRender.DRSEnabled'='0'
        'GstRender.AmbientOcclusion'=[string]$Plan.AmbientOcclusion
        'GstRender.RaytracingAmbientOcclusion'='0'
        'GstRender.EffectsQuality'=[string]$Plan.EffectsQuality
        'GstRender.LightingQuality'=[string]$Plan.LightingQuality
        'GstRender.LocalLightShadowQuality'=[string]$Plan.LocalLightShadowQuality
        'GstRender.ShadowQuality'=[string]$Plan.ShadowQuality
        'GstRender.SunShadowQuality'=[string]$Plan.SunShadowQuality
        'GstRender.TransparentShadows'='0'
        'GstRender.VolumetricQuality'=[string]$Plan.VolumetricQuality
        'GstRender.ReflectionQuality'=[string]$Plan.ReflectionQuality
        'GstRender.ScreenSpaceReflections'=[string]$Plan.ScreenSpaceReflections
        'GstRender.UndergrowthQuality'='0'; 'GstRender.PostProcessQuality'='0'
        'GstRender.TextureQuality'=[string]$Plan.TextureQuality
        'GstRender.TextureFiltering'=[string]$Plan.TextureFiltering
        'GstRender.MeshQuality'=[string]$Plan.MeshQuality
        'GstRender.TerrainQuality'=[string]$Plan.TerrainQuality
        'GstRender.SignificanceQuality'=[string]$Plan.SignificanceQuality
        'GstRender.MotionBlurWeapon'='0.000000'; 'GstRender.MotionBlurWorld'='0.000000'
        'GstRender.ChromaticAberration'='0'; 'GstRender.FilmGrain'='0'
        'GstRender.Vignette'='0'; 'GstRender.LensDistortion'='0'; 'GstRender.WeaponDOF'='0'
    }
    if ([int]$Plan.FpsCap -gt 0) {
        $s['GstRender.FrameRateLimit'] = ('{0}.000000' -f $Plan.FpsCap)
        $s['GstRender.FrameRateLimiterEnable'] = '1'
    }
    if ($Plan.Goal -eq 'Competitive') {
        $s['GstRender.FutureFrameRendering'] = '0'
        foreach ($key in @('GstRender.NVIDIAFrameGenerationEnabled','GstRender.NVIDIADynamicFrameGenerationEnabled',
                'GstRender.NvidiaMultiFrameGeneration','GstRender.AMDFrameGenerationEnabled',
                'GstRender.IntelFrameGenerationEnabled','GstRender.IntelMultiFrameGeneration')) { $s[$key] = '0' }
        if ($Facts.GPUVendor -eq 'NVIDIA') { $s['GstRender.NvidiaLowLatency'] = '1' }
        elseif ($Facts.GPUVendor -eq 'AMD') { $s['GstRender.AMDLowLatency'] = '1' }
        elseif ($Facts.GPUVendor -eq 'Intel') { $s['GstRender.IntelLowLatency'] = '1' }
    }
    return $s
}

function Required-Keys($Facts,$Plan) {
    # No game-version-specific key is mandatory. Only values present in the current
    # game's profile are changed, and the actual modified keys are verified.
    return @()
}

function Validate-Schema([string]$Path,$Facts,$Plan) {
    $t = (Read-TextFileInfo $Path).Text
    $settings = Plan-Settings $Facts $Plan
    $recognized = @($settings.Keys | Where-Object { $null -ne (Get-Setting $t $_) }).Count
    $signature = (($null -ne (Get-Setting $t 'GstRender.ResolutionWidth')) -or
                  ($null -ne (Get-Setting $t 'GstRender.VSyncMode')) -or
                  ($null -ne (Get-Setting $t 'GstRender.TextureQuality')))
    $valid = ($recognized -ge 4 -and $signature)
    return [pscustomobject]@{Path=$Path;Valid=$valid;RecognizedKeys=$recognized;Reason=if($valid){'Supported BF6 settings detected'}else{'Too few recognized render settings; safely skipping this profile'}}
}

function Test-ExpectedSettings([string]$Text,$Expected,$Facts,$Plan) {
    $mismatch = @()
    foreach ($kv in $Expected.GetEnumerator()) {
        $actual = Get-Setting $Text $kv.Key
        if ([string]$actual -ne [string]$kv.Value) {
            $mismatch += [pscustomobject]@{Key=$kv.Key;Expected=$kv.Value;Actual=$actual}
        }
    }
    return [pscustomobject]@{Mismatch=@($mismatch);MissingRequired=@();Verified=($mismatch.Count -eq 0)}
}

function Relevant-Processes {
    $names=@('bf6','EADesktop','EALauncher','EABackgroundService','steam','steamwebhelper','EpicGamesLauncher')
    $out=@()
    foreach($p in Get-Process -ErrorAction SilentlyContinue){
        if($names -contains $p.ProcessName){$out += [pscustomobject]@{Name=$p.ProcessName;Id=$p.Id}}
    }
    return @($out|Sort-Object Name,Id -Unique)
}

function Patch-And-Verify([string]$Path,$Facts,$Plan) {
    $file=Get-Item -LiteralPath $Path -ErrorAction Stop
    $fileInfo=Read-TextFileInfo $Path
    $beforeText=$fileInfo.Text; $beforeHash=File-Hash $Path
    $settings=Plan-Settings $Facts $Plan; $expected=[ordered]@{}; $new=$beforeText
    foreach ($kv in $settings.GetEnumerator()) {
        $old = Get-Setting $new $kv.Key
        if ($null -ne $old) { $new=Set-Setting $new $kv.Key $kv.Value; $expected[$kv.Key]=$kv.Value }
    }
    if ($expected.Count -lt 4) { throw "Not enough recognized BF6 profile keys: $Path" }
    $changed=($new -cne $beforeText)
    if ($changed) { Write-Atomic $Path $new $fileInfo.Encoding $fileInfo.HasBom }
    $afterHash=File-Hash $Path
    $afterText=(Read-TextFileInfo $Path).Text
    $v1=Test-ExpectedSettings $afterText $expected $Facts $Plan
    if ($changed) { Start-Sleep -Milliseconds 750 }
    $settledHash=File-Hash $Path
    $v2=Test-ExpectedSettings (Read-TextFileInfo $Path).Text $expected $Facts $Plan
    $result=[pscustomobject]@{
        Path=$Path;BeforeSHA256=$beforeHash;AfterWriteSHA256=$afterHash;SettledSHA256=$settledHash
        Encoding=$fileInfo.EncodingName;NewLine=$fileInfo.NewLine;Attributes=$file.Attributes.ToString()
        Checked=$expected.Count;Changed=$changed
        ImmediateMatched=($expected.Count-$v1.Mismatch.Count);ImmediateMismatch=$v1.Mismatch;ImmediateVerified=$v1.Verified
        SettledMatched=($expected.Count-$v2.Mismatch.Count);Mismatch=$v2.Mismatch
        ExternalModificationObserved=([string]$afterHash -ne [string]$settledHash)
        RelevantProcesses=(Relevant-Processes);ExpectedSettings=$expected;Verified=($v1.Verified -and $v2.Verified)
    }
    $script:LastPatchResult=$result
    return $result
}

function Windows-RegistryStates {
    # HKLM HAGS is intentionally read-only and NOT captured for restoration.
    return @(
        (Reg-State 'HKCU:\Software\Microsoft\GameBar' 'AllowAutoGameMode'),
        (Reg-State 'HKCU:\Software\Microsoft\GameBar' 'AutoGameModeEnabled'),
        (Reg-State 'HKCU:\System\GameConfigStore' 'GameDVR_Enabled'),
        (Reg-State 'HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR' 'AppCaptureEnabled')
    )
}

function Current-WindowsVerification {
    $map=@{}
    foreach ($r in Windows-RegistryStates) { $map[$r.Path+'\'+$r.Name]=if($r.Exists){$r.Value}else{$null} }
    $h=Reg-State 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' 'HwSchMode'
    return [pscustomobject]@{
        HwSchMode=if($h.Exists){$h.Value}else{$null}
        HwSchModeModifiedByTool=$false
        AllowAutoGameMode=$map['HKCU:\Software\Microsoft\GameBar\AllowAutoGameMode']
        AutoGameModeEnabled=$map['HKCU:\Software\Microsoft\GameBar\AutoGameModeEnabled']
        GameDVR_Enabled=$map['HKCU:\System\GameConfigStore\GameDVR_Enabled']
        AppCaptureEnabled=$map['HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR\AppCaptureEnabled']
        MatchesPlan=(($map['HKCU:\Software\Microsoft\GameBar\AllowAutoGameMode'] -eq 1) -and
                     ($map['HKCU:\Software\Microsoft\GameBar\AutoGameModeEnabled'] -eq 1) -and
                     ($map['HKCU:\System\GameConfigStore\GameDVR_Enabled'] -eq 0) -and
                     ($map['HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR\AppCaptureEnabled'] -eq 0))
    }
}

function Save-State([string]$Dir,$Facts,$Plan,[string]$Kind) {
    if(-not(Test-Path $Dir)){New-Item -ItemType Directory -Path $Dir -Force|Out-Null}
    $gdir=Join-Path $Dir 'GameConfigs';if(-not(Test-Path $gdir)){New-Item -ItemType Directory -Path $gdir -Force|Out-Null}
    $map=@();$i=0
    foreach($p in Get-BF6Profiles){
        $i++;$dest=Join-Path $gdir ("{0:D2}_{1}" -f $i,$p.Name);Copy-Item -LiteralPath $p.FullName -Destination $dest -Force
        $map += [pscustomobject]@{Original=$p.FullName;Backup=$dest;SHA256=File-Hash $dest}
    }
    [pscustomobject]@{ScriptVersion=$ScriptVersion;Created=(Get-Date).ToString('o');Kind=$Kind;MachineKey=$script:MachineKey;Facts=$Facts;Plan=$Plan;Registry=(Windows-RegistryStates);Profiles=$map}|ConvertTo-Json -Depth 10|Set-Content (Join-Path $Dir 'State.json') -Encoding UTF8
    $map|ConvertTo-Json -Depth 6|Set-Content (Join-Path $Dir 'GameConfigMap.json') -Encoding UTF8
}

function Ensure-OriginalBaseline($Facts,$Plan) {
    $dir=Join-Path $script:BackupRoot 'OriginalBaseline'
    if(-not(Test-Path (Join-Path $dir 'State.json'))){Save-State $dir $Facts $Plan 'OriginalBaseline';Ok "Original baseline created: $dir"}else{Info "Original baseline already exists: $dir"}
    return $dir
}

function New-Snapshot($Facts,$Plan) {
    $root=Join-Path $script:BackupRoot 'Snapshots'
    if (-not (Test-Path -LiteralPath $root)) { New-Item -ItemType Directory -Path $root -Force | Out-Null }
    $name=(Get-Date -Format 'yyyyMMdd_HHmmss_fff') + '_' + ([guid]::NewGuid().ToString('N').Substring(0,6))
    $dir=Join-Path $root $name
    Save-State $dir $Facts $Plan 'PreApplySnapshot'
    # A failed Apply should not overwrite the last successful restoration pointer.
    return $dir
}

function Restore-State([string]$Dir) {
    $statePath=Join-Path $Dir 'State.json'; $mapPath=Join-Path $Dir 'GameConfigMap.json'
    if (-not (Test-Path -LiteralPath $statePath) -or -not (Test-Path -LiteralPath $mapPath)) { throw "Backup metadata not found: $Dir" }
    $st=Get-Content -LiteralPath $statePath -Raw -ErrorAction Stop | ConvertFrom-Json
    if ($st.MachineKey -ne $script:MachineKey) { throw 'Snapshot belongs to a different computer.' }
    $maps=@(Get-Content -LiteralPath $mapPath -Raw -ErrorAction Stop | ConvertFrom-Json)
    if ($maps.Count -eq 1 -and $null -eq $maps[0]) { $maps=@() }
    # Preflight ALL hashes before changing either profiles or registry.
    foreach ($m in $maps) {
        if (-not (Test-Path -LiteralPath $m.Backup)) { throw "Backup file missing: $($m.Backup)" }
        if ((File-Hash $m.Backup) -ne $m.SHA256) { throw "Backup hash verification failed: $($m.Backup)" }
    }
    $results=@()
    foreach ($m in $maps) {
        $parent=Split-Path -Parent $m.Original
        if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        Copy-Item -LiteralPath $m.Backup -Destination $m.Original -Force -ErrorAction Stop
        $hash=File-Hash $m.Original
        $ok=($hash -eq $m.SHA256)
        $results += [pscustomobject]@{Original=$m.Original;Backup=$m.Backup;BackupSHA256=$m.SHA256;RestoredSHA256=$hash;Verified=$ok}
        if (-not $ok) { throw "Restore verification failed: $($m.Original)" }
    }
    foreach ($r in @($st.Registry)) {
        # Compatibility: older v1.3.x snapshots included HKLM/HAGS. Never change it silently.
        if ([string]$r.Path -like 'HKCU:*') { Restore-Reg $r }
    }
    return @($results)
}

function Apply-Windows {
    $steps=@(
        @('HKCU:\Software\Microsoft\GameBar','AllowAutoGameMode',1),
        @('HKCU:\Software\Microsoft\GameBar','AutoGameModeEnabled',1),
        @('HKCU:\System\GameConfigStore','GameDVR_Enabled',0),
        @('HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR','AppCaptureEnabled',0)
    )
    foreach($step in $steps) {
        try { Set-Dword ([string]$step[0]) ([string]$step[1]) ([int]$step[2]) }
        catch { Warn "Windows setting skipped ($($step[1])): $($_.Exception.Message)" }
    }
    Ok 'Per-user Game Mode / capture preferences requested. HAGS and power plan left unchanged.'
}

function Upscale-Label($Plan) {
    if($Plan.UpscalerAction -ne 'DLSS'){return $Plan.UpscalerAction}
    switch([int]$Plan.UpscaleQuality){0{return 'DLSS Quality'}1{return 'DLSS Balanced'}2{return 'DLSS Performance'}3{return 'DLSS Ultra Performance'}default{return 'DLSS'}}
}

function Show-Plan($Facts,$Plan) {
    Write-Host "`n=== BF6 Universal Auto Optimizer $ScriptVersion ===" -ForegroundColor White
    Write-Host "`nDetected system" -ForegroundColor White
    Write-Host "  Computer : $($Facts.Manufacturer) $($Facts.Model)"
    Write-Host "  Type     : $(if($Facts.IsLaptop){'Laptop'}else{'Desktop'})"
    Write-Host "  CPU      : $($Facts.CPU) ($($Facts.CpuCores)C/$($Facts.CpuThreads)T)"
    Write-Host "  RAM      : $($Facts.RAMGB) GB @ $($Facts.RAMSpeed) MT/s"
    Write-Host "  GPU      : $($Facts.GPU)"
    Write-Host "  Vendor   : $($Facts.GPUVendor)"
    Write-Host "  VRAM     : ~$($Facts.VRAMGB) GB"
    if($Facts.NvidiaDriver){Write-Host "  Driver   : $($Facts.NvidiaDriver)"}
    if($Facts.NvidiaPowerMaxW){Write-Host "  NVIDIA power limit/default/max : $($Facts.NvidiaPowerLimitW) / $($Facts.NvidiaPowerDefaultW) / $($Facts.NvidiaPowerMaxW) W"}
    Write-Host "  Display  : $($Plan.Width)x$($Plan.Height) @ $($Facts.Refresh) Hz"
    Write-Host "`nAutomatic plan" -ForegroundColor White
    Write-Host "  Goal             : $($Plan.Goal)"
    Write-Host "  Hardware tier    : $($Plan.Tier)"
    Write-Host "  Effective score  : $($Plan.EffectiveScore)"
    Write-Host "  FPS cap          : $(if($Plan.FpsCap -gt 0){$Plan.FpsCap}else{'preserved (refresh unknown)'})"
    Write-Host "  Upscaler         : $(Upscale-Label $Plan)"
    Write-Host "  Texture/Mesh     : $($Plan.TextureQuality) / $($Plan.MeshQuality)"
    Write-Host "  Terrain          : $($Plan.TerrainQuality)"
    Write-Host "  Effects/Lighting : $($Plan.EffectsQuality) / $($Plan.LightingQuality)"
    Write-Host "  Shadow/Volumetric: $($Plan.ShadowQuality) / $($Plan.VolumetricQuality)"
    Write-Host "  Frame Generation : $(if($Plan.Goal -eq 'Competitive'){'OFF when keys exist'}else{'preserved'})"
    Write-Host "  Future Frames    : $(if($Plan.Goal -eq 'Competitive'){'OFF when key exists'}else{'preserved'})"
    Write-Host "  V-Sync           : OFF"
    if($Facts.GPUVendor -eq 'AMD'){Warn 'AMD: current FSR/upscaler selection is preserved; no undocumented enum is forced.'}
    if($Facts.GPUVendor -eq 'Intel'){Warn 'Intel Arc: current XeSS/upscaler selection is preserved; no undocumented enum is forced.'}
    if($Facts.IsLaptop){Info 'Laptop: use AC power + OEM high-performance/Turbo mode + dGPU-only/MUX when available.'}
    Write-Host "`nBF6 profiles" -ForegroundColor White
    $p=Get-BF6Profiles;if($p){foreach($f in $p){Write-Host "  $($f.FullName)"}}else{Warn 'No BF6 profile found. Launch BF6 once, close it, then rerun.'}
}

function Verify-Profiles($Facts,$Plan,[bool]$Verbose=$true,$TargetPaths=$null,$SavedProfiles=$null) {
    $settings=Plan-Settings $Facts $Plan
    $results=@()
    $profiles=if($null -ne $TargetPaths){@($TargetPaths)}else{@(Get-BF6Profiles | Select-Object -ExpandProperty FullName)}
    foreach($path in $profiles) {
        if (-not (Test-Path -LiteralPath $path)) {
            $results += [pscustomobject]@{Path=$path;Verified=$false;Reason='Profile missing';Checked=0;Matched=0;Mismatch=@()}
            continue
        }
        $t=(Read-TextFileInfo $path).Text
        $expected=[ordered]@{}
        if ($null -ne $SavedProfiles) {
            $saved=@($SavedProfiles | Where-Object { $_.Path -eq $path } | Select-Object -First 1)
            if ($saved.Count -ne 1 -or $null -eq $saved[0].ExpectedSettings) {
                $results += [pscustomobject]@{Path=$path;Verified=$false;Reason='Saved expected values missing';Checked=0;Matched=0;Mismatch=@()}
                continue
            }
            $values=$saved[0].ExpectedSettings
            if($values -is [System.Collections.IDictionary]) {
                foreach($key in $values.Keys){$expected[[string]$key]=[string]$values[$key]}
            } else {
                foreach($kv in $values.PSObject.Properties){$expected[[string]$kv.Name]=[string]$kv.Value}
            }
        } else {
            $schema=Validate-Schema $path $Facts $Plan
            if (-not $schema.Valid) {continue}
            foreach($kv in $settings.GetEnumerator()) { if($null -ne (Get-Setting $t $kv.Key)) {$expected[$kv.Key]=$kv.Value} }
        }
        $v=Test-ExpectedSettings $t $expected $Facts $Plan
        $obj=[pscustomobject]@{Path=$path;SHA256=File-Hash $path;Checked=$expected.Count;Matched=($expected.Count-$v.Mismatch.Count);Mismatch=$v.Mismatch;ExpectedSettings=$expected;Verified=($expected.Count -gt 0 -and $v.Verified)}
        $results += $obj
        if($Verbose){
            Write-Host "`nProfile: $path" -ForegroundColor White
            if($v.Verified){Ok "Profile matches saved plan ($($expected.Count) keys)."}
            else {foreach($m in $v.Mismatch | Select-Object -First 12){Write-Host "  [MISMATCH] $($m.Key) actual=$($m.Actual) expected=$($m.Expected)" -ForegroundColor Red}}
        }
    }
    return @($results)
}

function Successful-ApplyMarkerPath { return (Join-Path $script:BackupRoot 'LastSuccessfulApply.json') }

function Write-SuccessfulApplyMarker($Facts,$Plan,$Verification,[string]$SnapshotName) {
    $obj=[pscustomobject]@{
        ScriptVersion=$ScriptVersion;Timestamp=(Get-Date).ToString('o');MachineKey=$script:MachineKey
        Snapshot=$SnapshotName;Facts=$Facts;Plan=$Plan;Profiles=$Verification
    }
    $obj|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Successful-ApplyMarkerPath) -Encoding UTF8
}

function Write-ApplyError([string]$Message,$Facts,$Plan) {
    try {
        $path=Join-Path $script:ReportRoot 'Latest_Apply_Error.json'
        [pscustomobject]@{
            ScriptVersion=$ScriptVersion;Timestamp=(Get-Date).ToString('o');Action='ApplyFailed';MachineKey=$script:MachineKey
            Error=$Message;Stage=$script:Stage;Snapshot=$script:SnapshotPath;RolledBack=$script:RolledBack;DataRoot=$script:DataRoot;Facts=$Facts;Plan=$Plan;Windows=(Current-WindowsVerification)
            ProfileDiagnostics=$script:LastPatchResult
            RelevantProcesses=(Relevant-Processes)
            Warnings=@($script:Warnings)
        }|ConvertTo-Json -Depth 14|Set-Content -LiteralPath $path -Encoding UTF8
        Warn "Apply error report: $path"
    } catch {}
}

function Write-Report([string]$Action,$Facts,$Plan,$Backup,$Verification,$RestoreVerification) {
    $obj=[pscustomobject]@{
        ScriptVersion=$ScriptVersion;Timestamp=(Get-Date).ToString('o');Action=$Action;MachineKey=$script:MachineKey
        Backup=$Backup;Facts=$Facts;Plan=$Plan;Windows=(Current-WindowsVerification);Profiles=$Verification;RestoreVerification=$RestoreVerification;Warnings=@($script:Warnings)
    }
    if($Action -eq 'Status'){$path=Join-Path $script:ReportRoot 'Latest_Status.json'}
    else{$path=Join-Path $script:HistoryRoot ((Get-Date -Format 'yyyyMMdd_HHmmss') + '_' + $Action + '.json')}
    $obj|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $path -Encoding UTF8
    Info "Report: $path"
    return $path
}

try {
    $script:Stage='Storage initialization'
    Initialize-Storage
    if ($Mode -eq 'OpenBackups' -or $Mode -eq 'OpenReports') {
        $folder=if($Mode -eq 'OpenBackups'){$script:BackupRoot}else{$script:ReportRoot}
        Info "Folder: $folder"
        Start-Process explorer.exe -ArgumentList ('"{0}"' -f $folder) -ErrorAction Stop
        exit 0
    }
    $script:Stage='Hardware detection'
    $facts=System-Facts
    $script:FactsForError=$facts
    $script:Stage='Plan preparation'
    $plan=Build-Plan $facts $Goal $FpsCap
    $script:PlanForError=$plan

    if ($Mode -eq 'Analyze') {
        Show-Plan $facts $plan
        $profiles=@(Get-BF6Profiles)
        foreach($p in $profiles){
            $v=Validate-Schema $p.FullName $facts $plan
            if(-not $v.Valid){Warn "Skipped unsupported profile: $($p.FullName) ($($v.Reason))"}
            else{Info "Recognized $($v.RecognizedKeys) compatible render settings: $($p.Name)"}
        }
        Write-Host "`nAnalyze completed. No report or backup file was created." -ForegroundColor Green
        exit 0
    }

    if ($Mode -eq 'Status') {
        $script:Stage='Status verification'
        $markerPath=Successful-ApplyMarkerPath
        if (-not (Test-Path -LiteralPath $markerPath)) {
            Warn 'No successful Apply marker. Run Apply to establish a verified plan.'
            $ver=@()
            $ret=2
        } else {
            $saved=Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
            $savedPaths=@($saved.Profiles | ForEach-Object { $_.Path })
            $ver=@(Verify-Profiles $saved.Facts $saved.Plan $true $savedPaths $saved.Profiles)
            $ret=if($ver.Count -lt 1 -or @($ver|Where-Object{-not $_.Verified}).Count -gt 0){2}else{0}
        }
        $wv=Current-WindowsVerification
        Write-Host "`nWindows gaming preferences (HKCU): GameMode=$($wv.AutoGameModeEnabled), DVR=$($wv.GameDVR_Enabled), Capture=$($wv.AppCaptureEnabled)"
        Write-Host "  HAGS (read-only): $($wv.HwSchMode)"
        if(-not $wv.MatchesPlan){Warn 'User-level Windows gaming preferences do not match requested values (possibly enforced by policy).'}
        Write-Report 'Status' $facts $plan '' $ver $null|Out-Null
        if($ret -ne 0){Fail 'Status does not match the saved successful Apply (or no Apply exists).'}
        else {Ok 'All saved BF6 profile settings verified.'}
        exit $ret
    }

    if ($Mode -eq 'RestoreLast' -or $Mode -eq 'RestoreOriginal') {
        $script:Stage='Restore preparation'
        if ($Mode -eq 'RestoreLast') {
            $pointer=Join-Path $script:BackupRoot 'LatestSnapshot.txt'
            if (-not (Test-Path -LiteralPath $pointer)) { throw 'No successful Apply snapshot found for this machine.' }
            $name=(Get-Content -LiteralPath $pointer -Raw).Trim()
            if($name -notmatch '^[A-Za-z0-9_-]+$'){throw 'Invalid snapshot pointer.'}
            $dir=Join-Path (Join-Path $script:BackupRoot 'Snapshots') $name
        } else {
            $name='OriginalBaseline'
            $dir=Join-Path $script:BackupRoot 'OriginalBaseline'
        }
        $script:Stage='Restoring saved profiles and HKCU settings'
        if (Get-Process bf6 -ErrorAction SilentlyContinue){throw 'Close BF6 before restoring settings.'}
        $rv=@(Restore-State $dir)
        $markerPath=Successful-ApplyMarkerPath
        if (Test-Path -LiteralPath $markerPath){Remove-Item -LiteralPath $markerPath -Force -ErrorAction SilentlyContinue}
        $action=if($Mode -eq 'RestoreLast'){'RestoreLast'}else{'RestoreOriginal'}
        Write-Report $action $facts $plan $name $null $rv|Out-Null
        Ok "Restore completed and verified: $dir"
        exit 0
    }

    if ($Mode -ne 'Apply') {throw "Unsupported action: $Mode"}
    $script:Stage='BF6 process and profile preflight'
    if (Get-Process bf6 -ErrorAction SilentlyContinue){throw 'BF6 is running. Close the game before Apply.'}
    $allProfiles=@(Get-BF6Profiles)
    if($allProfiles.Count -eq 0){throw 'No BF6 profile found. Launch BF6 once, close it, then rerun. Expected Documents\Battlefield 6\settings\<launcher>\PROFSAVE_profile.'}
    $profiles=@()
    foreach ($p in $allProfiles) {
        $schema=Validate-Schema $p.FullName $facts $plan
        if($schema.Valid){$profiles += $p}
        else{Warn "Unsupported BF6 profile skipped: $($p.FullName) ($($schema.Reason))"}
    }
    if($profiles.Count -eq 0){throw 'BF6 profiles were found, but none use a recognized render-settings schema. No settings were changed.'}
    foreach($p in $profiles){
        if($p.IsReadOnly){throw "BF6 profile is read-only: $($p.FullName)"}
    }
    Show-Plan $facts $plan
    $script:Stage='Original baseline and pre-Apply snapshot'
    Ensure-OriginalBaseline $facts $plan|Out-Null
    $snapshot=New-Snapshot $facts $plan
    $script:SnapshotPath=$snapshot
    Ok "Pre-Apply snapshot: $snapshot"
    # Preserve the previous successful pointers in case reporting/marker writing fails
    # AFTER patching the BF6 files, so rollback cannot leave false success metadata.
    $markerPathBefore=Successful-ApplyMarkerPath
    $pointerPathBefore=Join-Path $script:BackupRoot 'LatestSnapshot.txt'
    $markerBefore=if(Test-Path -LiteralPath $markerPathBefore){[IO.File]::ReadAllBytes($markerPathBefore)}else{$null}
    $pointerBefore=if(Test-Path -LiteralPath $pointerPathBefore){[IO.File]::ReadAllBytes($pointerPathBefore)}else{$null}
    try {
        $script:Stage='Per-user Windows gaming preferences'
        Apply-Windows
        $script:Stage='BF6 profile patch and verification'
        $applyResults=@()
        foreach ($p in $profiles) {
            $r=Patch-And-Verify $p.FullName $facts $plan
            $applyResults += $r
            if(-not $r.Verified){
                $bad=@($r.Mismatch|Select-Object -First 5|ForEach-Object{"$($_.Key): expected=$($_.Expected), actual=$($_.Actual)"}) -join '; '
                $hint=if($r.ExternalModificationObserved){' EA/Steam/cloud sync may be overwriting the profile.'}else{''}
                throw "Profile verification failed: $($p.FullName). $bad$hint"
            }
            Ok "Verified: $($p.FullName) ($($r.SettledMatched)/$($r.Checked))"
        }
        $script:Stage='Final independent verification'
        $targetPaths=@($profiles | ForEach-Object {$_.FullName})
        $second=@(Verify-Profiles $facts $plan $false $targetPaths $applyResults)
        if($second.Count -ne $profiles.Count -or @($second|Where-Object{-not $_.Verified}).Count -gt 0){throw 'Final BF6 settings verification failed.'}
        $script:Stage='Save success state'
        $name=Split-Path -Leaf $snapshot
        # Write report before success marker; avoid claiming success without a persisted report.
        Write-Report 'Apply' $facts $plan $name $second $null|Out-Null
        Write-SuccessfulApplyMarker $facts $plan $second $name
        Set-Content -LiteralPath (Join-Path $script:BackupRoot 'LatestSnapshot.txt') -Value $name -Encoding ASCII
        $errFile=Join-Path $script:ReportRoot 'Latest_Apply_Error.json'
        if(Test-Path -LiteralPath $errFile){Remove-Item -LiteralPath $errFile -Force -ErrorAction SilentlyContinue}
        Write-Host "`nOptimization completed and verified. No UAC required." -ForegroundColor Green
        if(-not (Current-WindowsVerification).MatchesPlan){Warn 'Some user-level Windows gaming settings are restricted. BF6 profile edits were verified.'}
        exit 0
    } catch {
        $applyError=$_.Exception.Message
        Fail "Apply failed at $($script:Stage): $applyError"
        $script:Stage='Automatic rollback'
        try { Restore-State $snapshot|Out-Null; $script:RolledBack=$true; Ok 'Automatic rollback verified.' }
        catch { Warn "Automatic rollback FAILED: $($_.Exception.Message). Restore backup manually before proceeding." }
        try {
            if($null -ne $markerBefore){[IO.File]::WriteAllBytes($markerPathBefore,$markerBefore)}
            elseif(Test-Path -LiteralPath $markerPathBefore){Remove-Item -LiteralPath $markerPathBefore -Force}
            if($null -ne $pointerBefore){[IO.File]::WriteAllBytes($pointerPathBefore,$pointerBefore)}
            elseif(Test-Path -LiteralPath $pointerPathBefore){Remove-Item -LiteralPath $pointerPathBefore -Force}
        } catch { Warn "Could not restore previous Apply metadata: $($_.Exception.Message)" }
        $script:Stage='Apply failure after rollback attempt'
        Write-ApplyError $applyError $facts $plan
        throw $applyError
    }
} catch {
    $err=$_.Exception.Message
    Fail "[$($script:Stage)] $err"
    if($Mode -eq 'Apply' -and $script:ReportRoot) {
        Write-ApplyError $err $script:FactsForError $script:PlanForError
        Write-Host "[INFO] Error file: $(Join-Path $script:ReportRoot 'Latest_Apply_Error.json')" -ForegroundColor Yellow
    }
    exit 1
}
