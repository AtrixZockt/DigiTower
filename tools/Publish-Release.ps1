<#
.SYNOPSIS
    Builds a DigiTower release: both programs, self-contained for 64-bit Windows, in one folder with the chart data
    and the bridge; a portable zip of it; with -Installer the Velopack installer and update packages.

.DESCRIPTION
    out\release\<version>\DigiTower\ gets DigiTower.exe (the control panel) and DigiTowerConsole.exe (the console),
    each with the .NET runtime (nothing to install first), data\airfields (taxiway names), data\voice\voices.json and
    recognition.json (the models are downloaded on the Install page), bridge\Scripts and bridge\install.ps1, the
    license and the readme. out\release\<version>\DigiTower-<version>-portable.zip is that folder zipped.

    Your own files are never packed: data\voice\voices.local.json, recognition.local.json, models, cache.

    -Installer packs that folder with Velopack (dotnet tool vpk, see dotnet-tools.json) into out\release\velopack:
    DigiTower-win-Setup.exe (installs for this Windows user, no admin rights, into %LOCALAPPDATA%\DigiTower),
    DigiTower-win-Portable.zip, and the packages the installed control panel updates itself from. The folder is kept
    from release to release: with the previous release in it, the update is a small delta. Uploading it to GitHub
    is a separate step (docs\releasing.md).

.EXAMPLE
    .\tools\Publish-Release.ps1 -Version 0.1.0
    .\tools\Publish-Release.ps1 -Version 0.1.0 -Installer
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Out = "out\release",
    [switch]$Installer
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$release = Join-Path $root (Join-Path $Out $Version)
$app = Join-Path $release "DigiTower"
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
New-Item -ItemType Directory -Force $app | Out-Null

foreach ($project in @("src\DcsAtc.Gui\DcsAtc.Gui.csproj", "src\DcsAtc.App\DcsAtc.App.csproj")) {
    dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained -p:Version=$Version -p:DebugType=None -o $app
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $project" }
}

# Whisper.net brings its native libraries for every platform: keep 64-bit Windows (CPU and Vulkan).
Get-ChildItem (Join-Path $app "runtimes") -Directory | Where-Object { $_.Name -notin @("win-x64", "vulkan") } | Remove-Item -Recurse -Force
Get-ChildItem (Join-Path $app "runtimes\vulkan") -Directory | Where-Object { $_.Name -ne "win-x64" } | Remove-Item -Recurse -Force

# Chart data and the voice lists; never this PC's own voice files or models.
Copy-Item (Join-Path $root "data\airfields") (Join-Path $app "data\airfields") -Recurse
New-Item -ItemType Directory -Force (Join-Path $app "data\voice") | Out-Null
foreach ($f in @("voices.json", "recognition.json")) { Copy-Item (Join-Path $root "data\voice\$f") (Join-Path $app "data\voice\$f") }

# The bridge, for the Install page (and install.ps1 for those who prefer it).
New-Item -ItemType Directory -Force (Join-Path $app "bridge") | Out-Null
Copy-Item (Join-Path $root "bridge\Scripts") (Join-Path $app "bridge\Scripts") -Recurse
Copy-Item (Join-Path $root "bridge\install.ps1") (Join-Path $app "bridge\install.ps1")

Copy-Item (Join-Path $root "LICENSE") (Join-Path $app "LICENSE.txt")
Copy-Item (Join-Path $root "README.md") (Join-Path $app "README.md")

$zip = Join-Path $release "DigiTower-$Version-portable.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $app "*") -DestinationPath $zip -CompressionLevel Optimal

$size = (Get-ChildItem $app -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("DigiTower {0}: {1} ({2:0} MB), {3} ({4:0} MB)" -f $Version, $app, $size, $zip, ((Get-Item $zip).Length / 1MB))

if ($Installer) {
    $velopack = Join-Path $root (Join-Path $Out "velopack")
    Push-Location $root
    try {
        dotnet tool restore | Out-Null
        dotnet vpk pack --packId DigiTower --packVersion $Version --runtime win-x64 --packDir $app --mainExe DigiTower.exe `
            --packTitle DigiTower --packAuthors "Julian | DiGii" --icon (Join-Path $root "src\DcsAtc.Gui\Assets\DigiTower.ico") `
            --shortcuts "Desktop,StartMenuRoot" --outputDir $velopack
        if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }
    }
    finally {
        Pop-Location
    }
    Write-Host "installer and update packages: $velopack"
}
