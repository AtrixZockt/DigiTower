<#
.SYNOPSIS
    Installs (or removes) the DCS-ATC bridge in a DCS Saved Games folder.

.DESCRIPTION
    Copies the hook and the mission-side script into <Saved Games>\<Instance>\Scripts and adds a
    marked block to <Saved Games>\<Instance>\Config\autoexec.cfg that lets hooks run code in the
    mission scripting environment. -Uninstall removes both again.

.EXAMPLE
    .\bridge\install.ps1                                  # the dedicated server (DCS.dcs_serverrelease)
    .\bridge\install.ps1 -Instance DCS                    # the normal game, for single-player tests
    .\bridge\install.ps1 -Instance DCS.dcs_serverrelease -Uninstall
#>
param(
    [string]$Instance = "DCS.dcs_serverrelease",
    [string]$SavedGames = (Join-Path $env:USERPROFILE "Saved Games"),
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$target = Join-Path $SavedGames $Instance
if (-not (Test-Path $target)) { throw "DCS folder not found: $target" }

$files = @("Hooks\DcsAtcBridgeHook.lua", "DcsAtc\DcsAtcMission.lua")
$autoexec = Join-Path $target "Config\autoexec.cfg"
$begin = "-- >>> DCS-ATC bridge"
$end = "-- <<< DCS-ATC bridge"
$noBom = New-Object System.Text.UTF8Encoding($false)

function Remove-Block([string]$text) {
    return [regex]::Replace($text, "(?ms)^" + [regex]::Escape($begin) + ".*?^" + [regex]::Escape($end) + "[^\r\n]*\r?\n?", "")
}

if ($Uninstall) {
    foreach ($f in $files) {
        $path = Join-Path $target "Scripts\$f"
        if (Test-Path $path) { Remove-Item $path; Write-Host "removed $path" }
    }
    if (Test-Path $autoexec) {
        $text = [System.IO.File]::ReadAllText($autoexec)
        $clean = Remove-Block $text
        if ($clean -ne $text) {
            [System.IO.File]::WriteAllText($autoexec, $clean, $noBom)
            Write-Host "removed the DCS-ATC block from $autoexec"
        }
    }
    return
}

foreach ($f in $files) {
    $dest = Join-Path $target "Scripts\$f"
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    Copy-Item (Join-Path $PSScriptRoot "Scripts\$f") $dest -Force
    Write-Host "installed $dest"
}

$block = @"
$begin (added by DCS_ATC\bridge\install.ps1; run it with -Uninstall to remove)
-- Lets Scripts\Hooks run code in the mission scripting environment (needed by DcsAtcBridgeHook.lua).
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
    Write-Host "autoexec.cfg already contains the DCS-ATC block"
} else {
    if ($existing -ne "") {
        Copy-Item $autoexec "$autoexec.bak-dcsatc" -Force
        if (-not $existing.EndsWith("`n")) { $existing += "`r`n" }
    }
    [System.IO.File]::WriteAllText($autoexec, $existing + $block.Replace("`r`n", "`n").Replace("`n", "`r`n"), $noBom)
    Write-Host "added the DCS-ATC block to $autoexec"
}

$others = Get-ChildItem (Join-Path $target "Scripts\Hooks") -Filter *.lua | Where-Object { $_.Name -ne "DcsAtcBridgeHook.lua" }
if ($others) {
    Write-Warning ("These other hooks in $Instance can now also run code in missions: " + (($others | ForEach-Object Name) -join ", ") +
        ". Make sure none of them accepts code from the network.")
}
Write-Host "Done. Restart DCS ($Instance) for the hook to load."
