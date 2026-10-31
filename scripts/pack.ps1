# Builds the per-user installer (Velopack): bin\pack\releases\BitcoinWallpaper-win-Setup.exe.
#
#   pwsh scripts/pack.ps1                  # version 0.1.0
#   pwsh scripts/pack.ps1 -Version 0.1.7
#
# Steps: dotnet publish (self-contained win-x64, so the owner needs no .NET runtime), then vpk pack. vpk is fetched
# into bin\pack\tools (git-ignored, nothing is installed system-wide). Setup.exe installs into
# %LOCALAPPDATA%\BitcoinWallpaper without admin rights, creates the Desktop and Start Menu shortcuts
# "Bitcoin Live Wallpaper" with the app icon and registers the app in Settings -> Apps under that name.
param(
    [string]$Version = '0.1.0',
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$VpkVersion = '1.2.161'   # keep equal to the Velopack package in src/Wallpaper.Host/Wallpaper.Host.csproj

$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $root 'bin\pack'
$publish = Join-Path $work 'publish'
$releases = Join-Path $work 'releases'
$tools = Join-Path $work 'tools'

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}

if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
if (Test-Path $releases) { Remove-Item -Recurse -Force $releases }

Invoke-Checked 'dotnet publish' {
    dotnet publish (Join-Path $root 'src\Wallpaper.Host\Wallpaper.Host.csproj') -c $Configuration -r $Runtime `
        --self-contained true -p:Version=$Version -o $publish
}

$vpk = Join-Path $tools 'vpk.exe'
if (-not (Test-Path $vpk) -or -not (Test-Path (Join-Path $tools ".store\vpk\$VpkVersion"))) {
    if (Test-Path $tools) { Remove-Item -Recurse -Force $tools }
    Invoke-Checked 'fetching vpk' { dotnet tool install vpk --version $VpkVersion --tool-path $tools }
}

Invoke-Checked 'vpk pack' {
    & $vpk pack `
        --packId BitcoinWallpaper `
        --packVersion $Version `
        --packTitle 'Bitcoin Live Wallpaper' `
        --packAuthors 'Bitcoin Live Wallpaper' `
        --packDir $publish `
        --mainExe BitcoinWallpaper.exe `
        --icon (Join-Path $root 'assets\icon\app.ico') `
        --shortcuts 'Desktop,StartMenuRoot' `
        --runtime $Runtime `
        --outputDir $releases
}

$setup = Get-ChildItem $releases -Filter '*Setup.exe' | Select-Object -First 1
if (-not $setup) { throw "no Setup.exe in $releases" }
Write-Host "Setup: $($setup.FullName) ($([math]::Round($setup.Length / 1MB, 1)) MB)"
