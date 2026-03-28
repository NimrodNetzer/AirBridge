# tail-logs.ps1 — Stream both AirBridge logs to the terminal.
#
# Usage:
#   .\scripts\tail-logs.ps1            # streams both Windows + Android logs
#   .\scripts\tail-logs.ps1 -Windows   # Windows log only
#   .\scripts\tail-logs.ps1 -Android   # Android logcat only
#
# Requires: adb on PATH or at the default Android SDK location.

param(
    [switch]$Windows,
    [switch]$Android
)

$showAll = -not $Windows -and -not $Android

# ── Locate adb ────────────────────────────────────────────────────────────────
$adb = $null
$candidates = @(
    "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe",
    "$env:USERPROFILE\AppData\Local\Android\Sdk\platform-tools\adb.exe"
)
foreach ($c in $candidates) {
    if (Test-Path $c) { $adb = $c; break }
}
if (-not $adb) { $adb = (Get-Command adb -ErrorAction SilentlyContinue)?.Source }

# ── Windows log ───────────────────────────────────────────────────────────────
$winLog = Join-Path $env:TEMP "AirBridge\airbridge.log"

if ($showAll -or $Windows) {
    Write-Host "Windows log: $winLog" -ForegroundColor Cyan
    if (-not (Test-Path $winLog)) {
        Write-Host "  (file not yet created — start the Windows app first)" -ForegroundColor DarkYellow
    } else {
        # Tail in background
        $winJob = Start-Job -ScriptBlock {
            param($path)
            Get-Content -Path $path -Wait -Tail 50
        } -ArgumentList $winLog
    }
}

# ── Android logcat ────────────────────────────────────────────────────────────
if (($showAll -or $Android) -and $adb) {
    Write-Host "Android logcat (AirBridge tags):" -ForegroundColor Green
    $devices = & $adb devices 2>&1 | Where-Object { $_ -match "device$" }
    if (-not $devices) {
        Write-Host "  No device connected via USB. Connect and enable USB debugging." -ForegroundColor Red
    } else {
        # Clear old logcat, then stream
        & $adb logcat -c
        $adbJob = Start-Job -ScriptBlock {
            param($adbPath)
            & $adbPath logcat -v time "AirBridge/Channel:V" "AirBridge/Transfer:V" "AirBridge/ConnSvc:V" "AirBridge/NSD:V" "*:S"
        } -ArgumentList $adb
    }
} elseif (($showAll -or $Android) -and -not $adb) {
    Write-Host "adb not found. Install Android SDK platform-tools and add to PATH." -ForegroundColor Red
}

# ── Unified stream ────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Streaming logs — press Ctrl+C to stop." -ForegroundColor Yellow
Write-Host "─────────────────────────────────────────" -ForegroundColor DarkGray

try {
    while ($true) {
        $jobs = @()
        if ($winJob)  { $jobs += $winJob  }
        if ($adbJob)  { $jobs += $adbJob  }

        foreach ($job in $jobs) {
            $output = Receive-Job -Job $job -ErrorAction SilentlyContinue
            foreach ($line in $output) {
                if ($job -eq $winJob) {
                    Write-Host "[WIN] $line" -ForegroundColor Cyan
                } else {
                    Write-Host "[APK] $line" -ForegroundColor Green
                }
            }
        }
        Start-Sleep -Milliseconds 200
    }
} finally {
    if ($winJob) { Stop-Job $winJob; Remove-Job $winJob }
    if ($adbJob) { Stop-Job $adbJob; Remove-Job $adbJob }
    Write-Host "Log streaming stopped."
}
