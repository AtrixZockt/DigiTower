<#
.SYNOPSIS
    Installs (or removes) the DigiTower bridge in a DCS Saved Games folder.

.DESCRIPTION
    Copies the hook and the mission-side script into <Saved Games>\<Instance>\Scripts and adds a
    marked block to <Saved Games>\<Instance>\Config\autoexec.cfg that lets hooks run code in the
    mission scripting environment. -Uninstall removes both again.

    The bridge settings are in <Saved Games>\<Instance>\Scripts\DigiTower\DigiTowerConfig.lua. The first install writes
    it with every setting explained; later installs keep it. -NoAi / -Ai switch AI handling off / on there,
    -NoMapDrawings / -MapDrawings the taxi routes and landing spots on the F10 map.

    An install from before the rename (DCS-ATC: Scripts\Hooks\DcsAtcBridgeHook.lua, Scripts\DcsAtc\) is moved over:
    its files are removed, its settings file becomes the new one, its autoexec.cfg block is replaced.

.EXAMPLE
    .\bridge\install.ps1                                  # the dedicated server (DCS.dcs_serverrelease)
    .\bridge\install.ps1 -Instance DCS                    # the normal game, for single-player tests
    .\bridge\install.ps1 -Instance DCS.dcs_serverrelease -NoAi   # AI departures are left to DCS on this server
    .\bridge\install.ps1 -Instance DCS.dcs_serverrelease -NoMapDrawings   # nothing drawn on the F10 map
    .\bridge\install.ps1 -Instance DCS.dcs_serverrelease -Uninstall
#>
param(
    [string]$Instance = "DCS.dcs_serverrelease",
    [string]$SavedGames = (Join-Path $env:USERPROFILE "Saved Games"),
    # Do not let DigiTower hold or release AI on this DCS (manageAi = false in DigiTowerConfig.lua).
    [switch]$NoAi,
    # Let DigiTower hold AI departures and release them in turn again (manageAi = true).
    [switch]$Ai,
    # Draw nothing on the F10 map (mapDrawings = false): DCS shows drawings to the whole side, not one player.
    [switch]$NoMapDrawings,
    # Draw taxi routes and helicopter landing spots on the F10 map again (mapDrawings = true).
    [switch]$MapDrawings,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
if ($NoAi -and $Ai) { throw "-NoAi and -Ai cannot be used together" }
if ($NoMapDrawings -and $MapDrawings) { throw "-NoMapDrawings and -MapDrawings cannot be used together" }
$target = Join-Path $SavedGames $Instance
if (-not (Test-Path $target)) { throw "DCS folder not found: $target" }

$files = @("Hooks\DigiTowerHook.lua", "DigiTower\DigiTowerMission.lua")
$config = Join-Path $target "Scripts\DigiTower\DigiTowerConfig.lua"
$autoexec = Join-Path $target "Config\autoexec.cfg"
$begin = "-- >>> DigiTower bridge"
$end = "-- <<< DigiTower bridge"
# Before the rename the project was called DCS-ATC: its files, settings file and autoexec.cfg block.
$oldFiles = @("Hooks\DcsAtcBridgeHook.lua", "DcsAtc\DcsAtcMission.lua")
$oldConfig = Join-Path $target "Scripts\DcsAtc\DcsAtcConfig.lua"
$oldBegin = "-- >>> DCS-ATC bridge"
$oldEnd = "-- <<< DCS-ATC bridge"
$noBom = New-Object System.Text.UTF8Encoding($false)

function Remove-Block([string]$text, [string]$from, [string]$to) {
    return [regex]::Replace($text, "(?ms)^" + [regex]::Escape($from) + ".*?^" + [regex]::Escape($to) + "[^\r\n]*\r?\n?", "")
}

# The old hook goes on install and on uninstall: two hooks would both listen on the port.
foreach ($f in $oldFiles) {
    $path = Join-Path $target "Scripts\$f"
    if (Test-Path $path) { Remove-Item $path; Write-Host "removed the old (DCS-ATC) $path" }
}
if (Test-Path $autoexec) {
    $text = [System.IO.File]::ReadAllText($autoexec)
    $clean = Remove-Block $text $oldBegin $oldEnd
    if ($clean -ne $text) {
        [System.IO.File]::WriteAllText($autoexec, $clean, $noBom)
        Write-Host "removed the old (DCS-ATC) block from $autoexec"
    }
}

if ($Uninstall) {
    foreach ($f in $files) {
        $path = Join-Path $target "Scripts\$f"
        if (Test-Path $path) { Remove-Item $path; Write-Host "removed $path" }
    }
    if (Test-Path $autoexec) {
        $text = [System.IO.File]::ReadAllText($autoexec)
        $clean = Remove-Block $text $begin $end
        if ($clean -ne $text) {
            [System.IO.File]::WriteAllText($autoexec, $clean, $noBom)
            Write-Host "removed the DigiTower block from $autoexec"
        }
    }
    foreach ($c in @($config, $oldConfig)) {
        if (Test-Path $c) { Write-Host "kept your settings in $c (delete it if you do not install DigiTower again)" }
    }
    return
}

# The old settings file becomes the new one, with your changes.
if ((Test-Path $oldConfig) -and -not (Test-Path $config)) {
    New-Item -ItemType Directory -Force (Split-Path $config) | Out-Null
    Move-Item $oldConfig $config
    Write-Host "moved your settings from $oldConfig to $config"
}
$oldDir = Split-Path $oldConfig
if ((Test-Path $oldDir) -and -not (Get-ChildItem $oldDir -Force)) { Remove-Item $oldDir; Write-Host "removed the empty $oldDir" }

foreach ($f in $files) {
    $dest = Join-Path $target "Scripts\$f"
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    Copy-Item (Join-Path $PSScriptRoot "Scripts\$f") $dest -Force
    Write-Host "installed $dest"
}

# The settings file: written with every setting explained on the first install, kept (with your changes) after that.
$configTemplate = @'
-- DigiTower bridge settings for this DCS. DCS reads them when it starts: restart DCS (or the server) after a change.
-- Installing the bridge again keeps this file.
return {
  -- true:  DigiTower holds AI departures at their parking spots and releases them in turn, players first.
  -- false: AI is left to DCS. The ATC still talks to players and counts AI as traffic.
  -- (bridge\install.ps1 -NoAi / -Ai switches this.)
  manageAi = true,

  -- true:  taxi routes and helicopter landing spots are drawn on the F10 map while they are in use. DCS shows map
  --        drawings to everyone on that side, not to one player.
  -- false: nothing is drawn.
  -- (bridge\install.ps1 -NoMapDrawings / -MapDrawings switches this.)
  mapDrawings = true,

  -- The port the DigiTower app connects to (on this PC only).
  port = 42700,
}
'@
if (-not (Test-Path $config)) {
    [System.IO.File]::WriteAllText($config, $configTemplate.Replace("`r`n", "`n").Replace("`n", "`r`n"), $noBom)
    Write-Host "created the settings file $config"
}
# Sets a true/false setting in the settings file (added if the file does not have it yet).
function Set-Setting([string]$name, [bool]$on) {
    $value = if ($on) { "true" } else { "false" }
    $text = [System.IO.File]::ReadAllText($config)
    $setting = [regex]('(?m)^(\s*)' + $name + '\s*=\s*(true|false)')
    $table = [regex]'return\s*\{'
    if ($setting.IsMatch($text)) {
        $text = $setting.Replace($text, '${1}' + $name + ' = ' + $value, 1)
    } elseif ($table.IsMatch($text)) {
        $text = $table.Replace($text, "return {`r`n  $name = $value,", 1)
    } else {
        throw "cannot set $name in $config (it does not return a table { ... }); set $name = $value there yourself"
    }
    [System.IO.File]::WriteAllText($config, $text, $noBom)
}
function Test-Off([string]$name) { [regex]::IsMatch([System.IO.File]::ReadAllText($config), '(?m)^\s*' + $name + '\s*=\s*false') }

if ($NoAi -or $Ai) { Set-Setting "manageAi" ([bool]$Ai) }
if ($NoMapDrawings -or $MapDrawings) { Set-Setting "mapDrawings" ([bool]$MapDrawings) }
Write-Host ("AI handling: " + $(if (Test-Off "manageAi") { "off (AI is left to DCS)" } else { "on" }) + ", switch with -NoAi / -Ai or in $config")
Write-Host ("F10 map drawings: " + $(if (Test-Off "mapDrawings") { "off" } else { "on" }) + ", switch with -NoMapDrawings / -MapDrawings")

$block = @"
$begin (added by DigiTower's bridge\install.ps1; run it with -Uninstall to remove)
-- Lets Scripts\Hooks run code in the mission scripting environment (needed by DigiTowerHook.lua).
net = net or {}
net.allow_unsafe_api = net.allow_unsafe_api or {}
table.insert(net.allow_unsafe_api, "userhooks")
net.allow_dostring_in = net.allow_dostring_in or {}
table.insert(net.allow_dostring_in, "mission")
$end

"@

New-Item -ItemType Directory -Force (Split-Path $autoexec) | Out-Null
$existing = if (Test-Path $autoexec) { [System.IO.File]::ReadAllText($autoexec) } else { "" }
if ($existing.Contains($begin)) {
    Write-Host "autoexec.cfg already contains the DigiTower block"
} else {
    if ($existing -ne "") {
        Copy-Item $autoexec "$autoexec.bak-digitower" -Force
        if (-not $existing.EndsWith("`n")) { $existing += "`r`n" }
    }
    [System.IO.File]::WriteAllText($autoexec, $existing + $block.Replace("`r`n", "`n").Replace("`n", "`r`n"), $noBom)
    Write-Host "added the DigiTower block to $autoexec"
}

$others = Get-ChildItem (Join-Path $target "Scripts\Hooks") -Filter *.lua | Where-Object { $_.Name -ne "DigiTowerHook.lua" }
if ($others) {
    Write-Warning ("These other hooks in $Instance can now also run code in missions: " + (($others | ForEach-Object Name) -join ", ") +
        ". Make sure none of them accepts code from the network.")
}
Write-Host "Done. Restart DCS ($Instance) for the hook to load."
