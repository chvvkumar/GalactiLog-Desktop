# WBPP Session Export
# Target: M 31
# Sessions: 2026-07-01, 2026-07-02
#
# To run this script:
#   Open PowerShell in this folder. Files downloaded via a browser are blocked
#   by Windows (Mark of the Web); this unblocks and runs the script in one step:
#     powershell -ExecutionPolicy Bypass -Command "Unblock-File -LiteralPath '.\wbpp_M_31.ps1'; & '.\wbpp_M_31.ps1'"
#
# When it finishes, open PixInsight WBPP and use 'Add Directory' on the
# staging root: D:\Staging\M 31

$StagingRoot = 'D:\Staging\M 31'
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $StagingRoot)) { New-Item -ItemType Directory -Force -Path $StagingRoot | Out-Null }

$Jobs = @(
    @{ Src = 'D:\Astro\M31\2026-07-01'; Dst = (Join-Path $StagingRoot '2026-07-01_M31'); ExcludeFiles = @('Ha\light_bad.fits') }
    @{ Src = 'D:\Astro\M31\2026-07-02'; Dst = (Join-Path $StagingRoot '2026-07-02_M31'); ExcludeFiles = @('OIII\light_bad.fits', 'Ha\other_bad.fits') }
)

# ---------------------------------------------------------------------------
# Display helpers
# ---------------------------------------------------------------------------
# Render block glyphs from code points so this script stays ASCII on disk;
# the console renders them as Unicode once OutputEncoding is UTF-8.
$Glyph = @{
    Full  = [char]0x2588   # full block
    Light = [char]0x2591   # light shade
    CapL  = [char]0x2595   # right one-eighth block (left edge cap)
    CapR  = [char]0x258F   # left one-eighth block (right edge cap)
}
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

function Format-Bytes {
    param([double]$Bytes)
    if ($Bytes -ge 1GB) { return ('{0:N1} GB' -f ($Bytes / 1GB)) }
    elseif ($Bytes -ge 1MB) { return ('{0:N1} MB' -f ($Bytes / 1MB)) }
    elseif ($Bytes -ge 1KB) { return ('{0:N0} KB' -f ($Bytes / 1KB)) }
    else { return ('{0:N0} B' -f $Bytes) }
}

function Format-Duration {
    param([double]$Seconds)
    if ($Seconds -lt 0 -or [double]::IsNaN($Seconds) -or [double]::IsInfinity($Seconds)) { return '--:--' }
    $ts = [TimeSpan]::FromSeconds([Math]::Round($Seconds))
    if ($ts.TotalHours -ge 1) {
        return ('{0:d2}:{1:d2}:{2:d2}' -f [int]$ts.TotalHours, $ts.Minutes, $ts.Seconds)
    }
    return ('{0:d2}:{1:d2}' -f $ts.Minutes, $ts.Seconds)
}

# Pass 1: gather the full file list (so progress can show an accurate total).
Write-Host ''
Write-Host '  Scanning source folders...' -ForegroundColor Cyan
$Files = @()
$TotalBytes = [long]0
foreach ($Job in $Jobs) {
    Get-ChildItem -Path $Job.Src -Recurse -File | Where-Object {
        $RelPath = $_.FullName.Substring($Job.Src.Length).TrimStart('\', '/').Replace('/', '\')
        (-not ($_.FullName.Split([char[]]@('\', '/')) | Where-Object { $_ -match "^(WBPP|PixInsight|finals|WORK_AREA|masters|Masters|MASTERS|.*CALIBRATED|CALIBRATED)$" })) -and (-not ($Job.ExcludeFiles -contains $RelPath))
    } | ForEach-Object {
        $RelPath = $_.FullName.Substring($Job.Src.Length).TrimStart('\', '/')
        $TotalBytes += $_.Length
        $Files += [pscustomobject]@{ Source = $_.FullName; Target = (Join-Path $Job.Dst $RelPath); Size = $_.Length }
    }
}

# Pass 2: copy with an inline progress bar.
$Total = $Files.Count
Write-Host ('  Found {0} file(s), {1}' -f $Total, (Format-Bytes $TotalBytes)) -ForegroundColor Gray
Write-Host ('  Destination: {0}' -f $StagingRoot) -ForegroundColor DarkGray
Write-Host ''

# Decide whether we can drive the cursor for in-place redraws.
$CanDraw = $false
$OriginRow = 0
try {
    if (-not [Console]::IsOutputRedirected) {
        [Console]::CursorVisible = $false
        Write-Host ''   # reserve line 1 (stats)
        Write-Host ''   # reserve line 2 (bar)
        $OriginRow = [Console]::CursorTop - 2
        $CanDraw = $true
    }
} catch { $CanDraw = $false }

# Bar width adapts to the window, clamped to a sane range.
$BarWidth = 40
try { $BarWidth = [Math]::Max(20, [Math]::Min(50, [Console]::WindowWidth - 38)) } catch { }

function Show-CopyProgress {
    param(
        [int]$Index, [int]$Total, [long]$Copied, [long]$TotalBytes, [double]$ElapsedSec, [switch]$Done
    )
    if ($TotalBytes -gt 0) { $frac = $Copied / $TotalBytes } else { $frac = $Index / [Math]::Max($Total, 1) }
    if ($frac -gt 1) { $frac = 1 }
    if ($frac -lt 0) { $frac = 0 }
    $pct = [int][Math]::Floor($frac * 100)

    if ($ElapsedSec -gt 0) { $rate = $Copied / $ElapsedSec } else { $rate = 0 }
    if ($rate -gt 0 -and -not $Done) { $eta = ($TotalBytes - $Copied) / $rate } else { $eta = -1 }
    if ($Done) { $eta = 0 }

    $cells = [int][Math]::Round($frac * $BarWidth)
    if ($cells -gt $BarWidth) { $cells = $BarWidth }
    $filled = ([string]$Glyph.Full) * $cells
    $empty = ([string]$Glyph.Light) * ($BarWidth - $cells)
    if ($Done) { $barColor = 'Green' } else { $barColor = 'Cyan' }

    $rest = ('   {0}/{1}   {2,3}%   {3} / {4}   {5}/s   ETA {6}' -f `
        $Index, $Total, $pct, (Format-Bytes $Copied), (Format-Bytes $TotalBytes), `
        (Format-Bytes $rate), (Format-Duration $eta))

    if ($CanDraw) {
        try {
            $w = [Console]::WindowWidth
            [Console]::SetCursorPosition(0, $OriginRow)

            # Line 1: title accent + stats, padded to clear any previous frame.
            Write-Host -NoNewline '  '
            Write-Host -NoNewline 'WBPP staging copy' -ForegroundColor Cyan
            $line1Len = 2 + 17 + $rest.Length
            $pad1 = [Math]::Max(0, $w - 1 - $line1Len)
            Write-Host ($rest + (' ' * $pad1)) -ForegroundColor Gray

            # Line 2: the bar.
            Write-Host -NoNewline '  '
            Write-Host -NoNewline ([string]$Glyph.CapL) -ForegroundColor DarkGray
            Write-Host -NoNewline $filled -ForegroundColor $barColor
            Write-Host -NoNewline $empty -ForegroundColor DarkGray
            Write-Host -NoNewline ([string]$Glyph.CapR) -ForegroundColor DarkGray
            $line2Len = 2 + 1 + $BarWidth + 1
            $pad2 = [Math]::Max(0, $w - 1 - $line2Len)
            Write-Host (' ' * $pad2)
        } catch {
            $script:CanDraw = $false
        }
    }
    if (-not $CanDraw) {
        Write-Host ('  WBPP staging copy   {0}' -f $rest.Trim())
    }
}

Write-Host '  Copying frames to WBPP staging' -ForegroundColor Cyan
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$i = 0
$CopiedBytes = [long]0
$lastPct = -1
$lastDrawMs = [long](-1000)
foreach ($f in $Files) {
    $i++
    $TargetDir = Split-Path $f.Target -Parent
    if (-not (Test-Path $TargetDir)) { New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null }
    Copy-Item -Path $f.Source -Destination $f.Target -Force
    $CopiedBytes += $f.Size

    $nowMs = $sw.ElapsedMilliseconds
    $pctNow = [int][Math]::Floor((($CopiedBytes / [Math]::Max($TotalBytes, 1)) * 100))
    if ($i -eq $Total -or $pctNow -ne $lastPct -or ($nowMs - $lastDrawMs) -ge 100) {
        Show-CopyProgress -Index $i -Total $Total -Copied $CopiedBytes -TotalBytes $TotalBytes -ElapsedSec $sw.Elapsed.TotalSeconds
        $lastPct = $pctNow
        $lastDrawMs = $nowMs
    }
}
$sw.Stop()
Show-CopyProgress -Index $Total -Total $Total -Copied $TotalBytes -TotalBytes $TotalBytes -ElapsedSec $sw.Elapsed.TotalSeconds -Done
if ($CanDraw) { try { [Console]::CursorVisible = $true } catch { } }

Write-Host ''
Write-Host ('  Done. Copied {0} file(s), {1} in {2}.' -f $Total, (Format-Bytes $TotalBytes), (Format-Duration $sw.Elapsed.TotalSeconds)) -ForegroundColor Green
Write-Host ('  Open WBPP and use Add Directory on: {0}' -f $StagingRoot) -ForegroundColor Gray