# D-36 install-over check, for the CI runner only (it installs the app for the current user; never run it on a PC
# you use). Installs -OldInstaller silently, starts the app, writes sample user files, then installs -NewInstaller
# silently over it, and checks: the installed version is -NewVersion, the user files (settings.json, screensaver.json,
# future.json, logsold.log, lockoriginal.jpg) are unchanged, the app log names the restore, one install folder,
# and the Velopack hooks' results (Run value, Desktop shortcut, BitcoinWallpaper.scr). Every leftover process is stopped
# at the end. Exits non-zero when any check fails.
#
#   pwsh scripts/install-over-test.ps1 -OldInstaller old\BLW_Installer.exe -NewInstaller new\BLW_Installer.exe -NewVersion 0.1.57
param(
    [Parameter(Mandatory)][string]$OldInstaller,
    [Parameter(Mandatory)][string]$NewInstaller,
    [Parameter(Mandatory)][string]$NewVersion,
    [int]$SetupTimeoutSeconds = 180,
    [string]$LogDir = 'install-over-logs'
)

$ErrorActionPreference = 'Stop'
$root = Join-Path $env:LOCALAPPDATA 'BitcoinWallpaper'
$exe = Join-Path $root 'current\BitcoinWallpaper.exe'
$images = 'BitcoinWallpaper.exe', 'BitcoinWallpaper.scr', 'BLW_Installer.exe', 'Update.exe'
New-Item -ItemType Directory -Force $LogDir | Out-Null
$failures = [System.Collections.Generic.List[string]]::new()

function Check([string]$what, [bool]$ok, [string]$detail = '') {
    if ($ok) { Write-Host "PASS  $what" } else { Write-Host "FAIL  $what $detail"; $failures.Add($what) }
}

function Install([string]$installer, [string]$name) {
    $log = Join-Path (Resolve-Path $LogDir) "$name-setup.log"
    Write-Host "Installing $name ($installer)"
    $p = Start-Process -FilePath $installer -ArgumentList '--silent', '--log', "`"$log`"" -PassThru
    if (-not $p.WaitForExit($SetupTimeoutSeconds * 1000)) {
        $p.Kill($true)
        throw "$name Setup did not finish in $SetupTimeoutSeconds s"
    }
    if ($p.ExitCode -ne 0) { throw "$name Setup exit code $($p.ExitCode) (log: $log)" }
}

function InstalledVersion {
    $file = Get-ChildItem $root -Recurse -Filter 'sq.version' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $file) { return $null }
    Write-Host "Velopack metadata: $($file.FullName)"
    return ([xml](Get-Content -Raw $file.FullName)).package.metadata.version
}

function StopAll {
    foreach ($image in $images) { & taskkill.exe /F /T /IM $image 2>&1 | Out-Null }
}

try {
    if (Test-Path $root) { throw "$root exists before the test; this check needs a machine without the app" }

    Install $OldInstaller 'old'
    $oldVersion = InstalledVersion
    Write-Host "Installed (old): $oldVersion"
    Check 'old install: BitcoinWallpaper.exe present' (Test-Path $exe)

    # The owner's case: the old version runs while the new installer runs (Velopack stops it).
    Start-Process -FilePath $exe | Out-Null
    Start-Sleep -Seconds 5

    $settings = Join-Path $root 'settings.json'
    $screensaver = Join-Path $root 'screensaver.json'
    $marker = Join-Path $root 'first-run.done'
    $settingsText = '{"wallpaperOn":false,"sample":"install-over-test"}'
    $screensaverText = "{`n  `"version`": 1,`n  `"SCRNSAVE.EXE`": `"C:\\Windows\\system32\\Mystify.scr`",`n  `"ScreenSaveActive`": `"1`"`n}"
    Set-Content -NoNewline -Path $settings -Value $settingsText
    Set-Content -NoNewline -Path $screensaver -Value $screensaverText
    # Files a later version may add (any *.json, more in logs\ and lock\) must survive too.
    $future = Join-Path $root 'future.json'
    $futureText = '{"addedBy":"a later version","n":42}'
    Set-Content -NoNewline -Path $future -Value $futureText
    $oldLog = Join-Path $root 'logs\old.log'
    $oldLogText = "2026-10-31 00:00:00.000 [1] INFO  install-over-test sample line"
    New-Item -ItemType Directory -Force (Split-Path $oldLog) | Out-Null
    Set-Content -NoNewline -Path $oldLog -Value $oldLogText
    $lockImage = Join-Path $root 'lock\original.jpg'
    New-Item -ItemType Directory -Force (Split-Path $lockImage) | Out-Null
    $lockBytes = [byte[]](0xFF, 0xD8, 0xFF, 0xE0) + [System.Text.Encoding]::ASCII.GetBytes('install-over-test') + [byte[]](0, 1, 2, 254, 255, 0xFF, 0xD9)
    [System.IO.File]::WriteAllBytes($lockImage, $lockBytes)
    $lockHash = (Get-FileHash $lockImage).Hash
    Check 'old install: first-run.done written by the install hook' (Test-Path $marker)

    Install $NewInstaller 'new'
    $newInstalled = InstalledVersion
    Write-Host "Installed (new): $newInstalled"
    Check "installed version is $NewVersion" ($newInstalled -eq $NewVersion) "(found '$newInstalled', old was '$oldVersion')"

    $uninstall = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\BitcoinWallpaper' -ErrorAction SilentlyContinue
    Check "Settings -> Apps version is $NewVersion" ($uninstall.DisplayVersion -eq $NewVersion) "(found '$($uninstall.DisplayVersion)')"

    Check 'settings.json kept' ((Test-Path $settings) -and (Get-Content -Raw $settings) -eq $settingsText)
    Check 'screensaver.json kept' ((Test-Path $screensaver) -and (Get-Content -Raw $screensaver) -eq $screensaverText)
    Check 'first-run.done kept' (Test-Path $marker)
    Check 'future.json kept' ((Test-Path $future) -and (Get-Content -Raw $future) -eq $futureText)
    Check 'logs\old.log kept' ((Test-Path $oldLog) -and (Get-Content -Raw $oldLog) -eq $oldLogText)
    Check 'lock\original.jpg kept' ((Test-Path $lockImage) -and (Get-FileHash $lockImage).Hash -eq $lockHash)
    $appLog = Join-Path $root 'logs\wallpaper.log'
    Check 'app log names the restore' ((Test-Path $appLog) -and (Select-String -Quiet -SimpleMatch -Path $appLog 'install over a previous install'))

    $folders = @(Get-ChildItem $env:LOCALAPPDATA -Directory -Filter 'BitcoinWallpaper*')
    Check 'one install folder' ($folders.Count -eq 1) "(found: $($folders.Name -join ', '))"
    Check 'BitcoinWallpaper.exe present' (Test-Path $exe)
    Check 'BitcoinWallpaper.scr written next to the exe' (Test-Path (Join-Path $root 'current\BitcoinWallpaper.scr'))

    $run = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).BitcoinWallpaper
    Check 'Run value points to the installed exe' ($run -eq "`"$exe`" --autostart") "(found '$run')"

    $desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Bitcoin Live Wallpaper.lnk'
    Check 'Desktop shortcut present' (Test-Path $desktop)
}
catch {
    Write-Host "FAIL  $_"
    $failures.Add("$_")
}
finally {
    StopAll
    if (Test-Path (Join-Path $root 'logs')) { Copy-Item -Recurse -Force (Join-Path $root 'logs') (Join-Path $LogDir 'app-logs') }
}

if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) check(s) failed"
    exit 1
}
Write-Host 'All install-over checks passed'
exit 0
