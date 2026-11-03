# e2e-player-resume.ps1 -- real-app regression for "close app -> reopen: same track + progress" (v0.65.1)
#
# Why this script exists:
#   Closing the app used to skip MainViewModel.ShutdownTools completely: App.Exit read
#   Application.MainWindow.DataContext, but WPF had already set Application.MainWindow to null when
#   the last window closed. So the player's OnClosed never ran: playlist.json kept the OLD current
#   track and folder-resume.json never got the final progress. The self-check cannot see that wiring
#   (no real Application / Window), so this script drives the real exe.
#
# What it does:
#   1. temp YLT_HOME (never touches your real Data/Config) + one 30 s silent WAV;
#      seeds playlist.json with currentPath = WAV, positionSeconds = 15, and enables
#      autoResumePlayback so the player really loads it and seeks to 15 s;
#   2. runs the real exe with YLT_NAV_TOOL=media-player and YLT_PLAYER_TRACE=<file>;
#   3. while running: asserts the track was restored and the player is really past 15 s;
#   4. closes the main window (the real app-exit path) and waits for the process to end;
#   5. asserts playlist.json now holds the SAME track and a position > 15 s (exit save ran),
#      and folder-resume.json holds that track too (per-folder memory also saved on exit);
#   6. asserts the trace contains OnClosed + PersistPlaybackState.
#
# Usage:
#   .\scripts\e2e-player-resume.ps1
#   .\scripts\e2e-player-resume.ps1 -Exe <path to YuLiangTookit.exe>
#
# Exit code: 0 = all checks passed, 1 = a check failed (details printed).

param(
    [string]$Exe = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $Exe = Join-Path $root "bin\Debug\net9.0-windows10.0.19041.0\YuLiangTookit.exe"
}
if (-not (Test-Path $Exe)) {
    throw "exe not found: $Exe (run .\build.ps1 first)"
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$work = Join-Path $root ".artifacts\e2e-player-resume"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
$media = Join-Path $work "media"
$homeDir = Join-Path $work "home"
$dataDir = Join-Path $homeDir "Data\media-player"
$cfgDir = Join-Path $homeDir "Config\media-player"
New-Item -ItemType Directory -Force -Path $media, $dataDir, $cfgDir | Out-Null

$pass = 0
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) {
        $script:pass++
        Write-Host ("PASS  {0}" -f $name)
    }
    else {
        $script:fail++
        Write-Host ("FAIL  {0}  {1}" -f $name, $detail)
    }
}

# 30 s of 8 kHz / 16-bit / mono silence: long enough that the position keeps moving while we wait.
function New-SilentWav([string]$path, [int]$seconds) {
    $sampleRate = 8000
    $channels = 1
    $bits = 16
    $dataLen = $sampleRate * $seconds * $channels * ($bits / 8)
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes("RIFF"))
    $bw.Write([int](36 + $dataLen))
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes("WAVE"))
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes("fmt "))
    $bw.Write([int]16)
    $bw.Write([int16]1)
    $bw.Write([int16]$channels)
    $bw.Write([int]$sampleRate)
    $bw.Write([int]($sampleRate * $channels * $bits / 8))
    $bw.Write([int16]($channels * $bits / 8))
    $bw.Write([int16]$bits)
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes("data"))
    $bw.Write([int]$dataLen)
    $bw.Write((New-Object byte[] $dataLen))
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($path, $ms.ToArray())
    $bw.Dispose()
    $ms.Dispose()
}

$wav = Join-Path $media "resume-test.wav"
New-SilentWav $wav 30

$playlistPath = Join-Path $dataDir "playlist.json"
$resumePath = Join-Path $dataDir "folder-resume.json"
$configPath = Join-Path $cfgDir "config.json"
$tracePath = Join-Path $work "player-trace.txt"

# "Last time" = this WAV at 0:15 (the player must restore it and continue from there).
$state = [ordered]@{
    paths           = @($wav)
    currentPath     = $wav
    positionSeconds = 15
}
[System.IO.File]::WriteAllText($playlistPath, ($state | ConvertTo-Json), $utf8)
[System.IO.File]::WriteAllText($configPath, ([ordered]@{ autoResumePlayback = $true } | ConvertTo-Json), $utf8)

$previous = @{}
foreach ($name in @("YLT_HOME", "YLT_NAV_TOOL", "YLT_PLAYER_TRACE", "YLT_PLAYER_DEMO", "YLT_SHOT", "YLT_SHOT_TOOL")) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name)
}
$process = $null
try {
    $env:YLT_HOME = $homeDir
    $env:YLT_NAV_TOOL = "media-player"
    $env:YLT_PLAYER_TRACE = $tracePath
    Remove-Item Env:YLT_PLAYER_DEMO -ErrorAction SilentlyContinue
    Remove-Item Env:YLT_SHOT -ErrorAction SilentlyContinue
    Remove-Item Env:YLT_SHOT_TOOL -ErrorAction SilentlyContinue

    $process = Start-Process -FilePath $Exe -PassThru

    # Wait for the window, then give the restore + auto-play a few seconds.
    $deadline = [DateTime]::UtcNow.AddSeconds(40)
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) { break }
        if ($process.MainWindowHandle -ne 0) { break }
    }
    $process.Refresh()
    Check "real app window opened" (-not $process.HasExited -and $process.MainWindowHandle -ne 0)
    Start-Sleep -Seconds 4

    # --- while running: the restored track is loaded and the live per-folder memory exists ---
    # (playlist.json itself is written on track changes / pause / exit, not every second;
    #  the once-per-10s live snapshot lives in folder-resume.json)
    $running = Get-Content -LiteralPath $playlistPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Check "playlist.json still points at the restored track" ($running.currentPath -eq $wav) ($running.currentPath)
    Check "live per-folder memory was captured while playing" (Test-Path -LiteralPath $resumePath)
    if (Test-Path -LiteralPath $resumePath) {
        $live = Get-Content -LiteralPath $resumePath -Raw -Encoding UTF8 | ConvertFrom-Json
        $liveEntry = @($live.entries) | Select-Object -First 1
        Check "live per-folder memory holds the restored track at >= 0:15" ($null -ne $liveEntry -and $liveEntry.currentPath -eq $wav -and [double]$liveEntry.positionSeconds -ge 15) ("entry={0} @ {1}" -f $liveEntry.currentPath, $liveEntry.positionSeconds)
    }

    # --- real app exit: close the main window exactly like the user does ---
    $process.CloseMainWindow() | Out-Null
    if (-not $process.WaitForExit(30000)) {
        $process.Kill()
        throw "app did not exit within 30 s after closing the window"
    }
    Check "app exited normally after closing the window" ($process.ExitCode -eq 0) ("exit={0}" -f $process.ExitCode)

    # --- after exit: the exit save must have run ---
    $saved = Get-Content -LiteralPath $playlistPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $savedPosition = [double]$saved.positionSeconds
    Check "exit save kept the current track" ($saved.currentPath -eq $wav) ($saved.currentPath)
    Check "exit save wrote the real progress (> 15 s, not the seeded or zeroed value)" ($savedPosition -gt 16) ("position={0}" -f $savedPosition)
    Check "per-folder memory (folder-resume.json) was also written on exit" (Test-Path -LiteralPath $resumePath)
    if (Test-Path -LiteralPath $resumePath) {
        $resume = Get-Content -LiteralPath $resumePath -Raw -Encoding UTF8 | ConvertFrom-Json
        $entry = @($resume.entries) | Select-Object -First 1
        Check "per-folder memory holds the same track + progress" ($null -ne $entry -and $entry.currentPath -eq $wav -and [double]$entry.positionSeconds -gt 16) ("entry={0}" -f $entry.currentPath)
    }

    $trace = if (Test-Path -LiteralPath $tracePath) { Get-Content -LiteralPath $tracePath -Raw -Encoding UTF8 } else { "" }
    Check "restore seek (pendingSeek=15s) really happened" ($trace.Contains("pendingSeek=15s"))
    Check "close path reached the player's OnClosed" ($trace.Contains("OnClosed"))
    Check "PersistPlaybackState ran on exit" ($trace.Contains("PersistPlaybackState"))
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        try { $process.Kill() } catch { }
    }
    foreach ($name in $previous.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name])
    }
}

Write-Host ""
Write-Host ("RESULT: pass={0} fail={1}" -f $pass, $fail)
if ($fail -gt 0) { exit 1 }
exit 0
