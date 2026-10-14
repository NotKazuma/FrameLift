# Installs the published exe to Program Files, with a Start Menu shortcut.
# Run elevated, after publish.ps1.   Uninstall: install.ps1 -Uninstall
# -Autostart also starts FrameLift at sign-in (elevated, no UAC prompt) so automatic Game Boost always works.
# User data (config) lives in %AppData%\FrameLift and is left alone on uninstall.
param([switch]$Uninstall, [switch]$Autostart)
$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw "Run this script from an elevated (Administrator) PowerShell." }

$dir = Join-Path $env:ProgramFiles "FrameLift"
$exe = Join-Path $dir "FrameLift.exe"
$lnk = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\FrameLift.lnk"
$journalPath = Join-Path $env:ProgramData "FrameLift\journal.json"

# The watchdog is a second FrameLift.exe (--watchdog) that undoes Game Boost if the main app dies.
# Stop the main app first and give the watchdog time to finish before touching the files.
function Stop-Running {
    $procs = Get-CimInstance Win32_Process -Filter "Name='FrameLift.exe'" -ErrorAction SilentlyContinue
    $main = $procs | Where-Object { $_.CommandLine -notmatch '--watchdog' }
    $watchdogs = $procs | Where-Object { $_.CommandLine -match '--watchdog' }

    foreach ($m in $main) { Stop-Process -Id $m.ProcessId -Force -ErrorAction SilentlyContinue }

    if ($watchdogs) {
        for ($i = 0; $i -lt 10; $i++) {
            if (-not (Get-Process -Id @($watchdogs.ProcessId) -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Seconds 1
        }
        foreach ($w in $watchdogs) { Stop-Process -Id $w.ProcessId -Force -ErrorAction SilentlyContinue }
    }
}

if ($Uninstall) {
    Stop-Running
    schtasks /Delete /TN FrameLift /F 2>$null | Out-Null   # autostart task, if enabled
    Remove-Item $lnk -Force -ErrorAction SilentlyContinue
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    "Uninstalled. Tweaks you applied stay in effect: open FrameLift and use 'Undo all changes' first if you want them reverted."
    "(Settings in %AppData%\FrameLift were kept.)"
    return
}

$src = Join-Path $PSScriptRoot "dist\FrameLift.exe"
if (-not (Test-Path $src)) { throw "dist\FrameLift.exe not found - run publish.ps1 first." }

Stop-Running
New-Item -ItemType Directory -Path $dir -Force | Out-Null
Copy-Item $src $exe -Force

$sh = New-Object -ComObject WScript.Shell
$s = $sh.CreateShortcut($lnk)
$s.TargetPath = $exe
$s.WorkingDirectory = $dir
$s.Description = "FrameLift - smoother games on weaker PCs"
$s.Save()

if ($Autostart) {
    # Starts hidden in the tray (--tray), elevated without a UAC prompt. The task's default limits would stop it on
    # battery and kill it after 3 days, which is wrong for a tray app on a laptop, so they are switched off.
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $action = New-ScheduledTaskAction -Execute $exe -Argument "--tray"
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName FrameLift -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    "Autostart: enabled (task FrameLift, starts hidden in the tray)"
}

"Installed: $exe"
"Shortcut:  $lnk"
