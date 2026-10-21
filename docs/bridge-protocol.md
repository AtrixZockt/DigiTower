# Bridge protocol

The hook (`bridge/Scripts/Hooks/DigiTowerHook.lua`) listens on **127.0.0.1:42700**. Its settings are in
`Scripts/DigiTower/DigiTowerConfig.lua`, which the installer (the control panel's Install page, or `bridge/install.ps1`)
writes on the first install (every setting explained) and keeps after that: `port`, `manageAi` (false: the server
does not let the app hold or release AI, departures or arrivals; `install.ps1 -NoAi` / `-Ai`) and `mapDrawings`
(false: `draw_route` draws nothing and answers `{ marks = 0 }`; `install.ps1 -NoMapDrawings` / `-MapDrawings`). A file that does not load is reported in `hello.error` and the defaults are used.
One app connects at a time; a new connection replaces the old one. Both directions send **one JSON object per line** (UTF-8, `\n`).

Coordinates are DCS map coordinates in metres: `x` = north, `z` = east, `y` = altitude MSL.
Headings are true, in degrees.

## DCS → app

| `type` | When | Fields |
|---|---|---|
| `hello` | right after connecting | `protocol`, `mission`, `missionRunning`, `info` (mission info, if running), `multiplayer`, `manageAi` (false when the server turned AI handling off), `mapDrawings` (false when it turned the F10 map drawings off), `error` (last hook error, e.g. a failed injection) |
| `mission_start` | a mission started (bridge injected) | `mission`, `info`: `protocol`, `theatre`, `date {Year,Month,Day}`, `startTime`, `absTime`, `modelTime` |
| `mission_stop` | the mission ended | |
| `poll` | 5×/s while a mission runs | `t` (mission time), `msgs` (array of the messages below), `units` (every 0.5 s) |
| `error` | something failed | `error`, `cmd` |
| `reply` | answer to a command with `req` | `req`, `cmd`, `ok`, `data` or `error` |

Messages inside `poll.msgs`:

| `type` | Fields |
|---|---|
| `event` | `ev` (`birth`, `player_enter_unit`, `player_leave_unit`, `engine_startup`, `engine_shutdown`, `takeoff`, `runway_takeoff`, `land`, `runway_touch`, `crash`, `dead`, `pilot_dead`, `ejection`, `unit_lost`), `t`, `unit`, `unitId`, `unitType`, `coal`, `player`, `group`, `groupId`, `place` (airbase name) |
| `menu` | a player selected an app menu entry: `groupId`, `id`, `t` |
| `reply`, `error` | as above |

`units` entries (airplanes and helicopters, all coalitions): `id`, `name`, `type`, `cat` (`plane`/`heli`),
`coal`, `group`, `groupId`, `player` (absent for AI), `callsign`, `x`, `y`, `z`, `vx`, `vy`, `vz`, `hdg`,
`agl` (m), `inAir`.

## App → DCS

Every command has `cmd`; add `req` (integer) to get a `reply`. Commands are data only: the hook turns
them into a Lua table literal and the mission side runs one of the whitelisted handlers below.

| `cmd` | Fields | Reply `data` |
|---|---|---|
| `hello` | | (sends a new `hello`) |
| `ping` | | `{ pong, t }` |
| `text` | `text`, `seconds`, `clear`, and one of `groupId` / `unitId` / `coalition` (none = everyone) | |
| `menu_add` | `groupId`, `path` (array of submenu names under F10 Other), `label`, `id` | |
| `menu_remove` | `groupId`, `id` | |
| `menu_clear` | `groupId` | |
| `airbase_list` | | `[{ name, id, category, coalition, x, y, z }]` (category 0 airdrome, 1 helipad, 2 ship) |
| `airbase_info` | `name` | `{ name, id, callsign, category, coalition, x, y, z, radioSilent, runways: [{ name, course, width, length, x, y, z }], parking: [{ index, index0, termType, x, y, z, distToRunway, occupied }] }` |
| `radio_silent` | `name`, `silent` | `{ name, silent }` |
| `farp_list` | | `[{ name, type, id, x, z, coalition, frequency, modulation, callsignId, callsign, pads: [{ index, x, z }] }]`: the FARPs of the mission file (statics of category `Heliports`) with the frequency (MHz) and callsign id the mission gives them; `id`, `coalition`, `callsign` ("London") and `pads` (parking spots) from DCS when the FARP exists as an airbase |
| `airbase_radios` | | `[{ id, name, frequencies: [{ hz, modulation }] }]`: every airdrome's ATC frequencies, the ones the briefing shows (modulation 0 = AM). Answered by the hook itself, as DCS's briefing reads them: the terrain's `GetTerrainConfig('Airdromes')[id].radio` lists radio ids, `DCS.getATCradiosData(id)` their frequencies. `id` is the airbase id. |
| `airbase_weather` | `name` | `{ windX, windZ, temperature, pressure, elevation }`: wind 10 m above the field in m/s (the direction it blows **towards**), temperature in K and pressure in Pa at field elevation |
| `mission_weather` | | `{ visibility, dust, fogVisibility, fogThickness, cloudBase, cloudThickness, cloudDensity, cloudPreset, precipitation, absTime }`: the editor's weather (`env.mission.weather`, metres; `cloudPreset` such as `"Preset7"`, whose layers are in DCS's `Config/Effects/clouds.lua` and move with `cloudBase`), the current fog from `world.weather`, and `timer.getAbsTime()` (seconds since midnight of the start date, local time of the map). Absent fields are not set in the mission. |
| `draw_route` | `key`, `coalition` (-1 = all), `points` (`[[x, z], ...]`), `color` (`[r, g, b, a]`, 0–1), `text` (optional, at the last point), `lineType`, `fontSize` | `{ marks }`; drawing again with the same `key` replaces the old drawing |
| `drawing_clear` | `key` | |
| `mark_group` | `key`, `groupId`, `x`, `z`, `text` | `{ id }`: a mark point with a label meant only for that group (`trigger.action.markToGroup`); removed with `drawing_clear` and its key. Lines and text (`draw_route`) can only be drawn for a whole coalition. |
| `group_hold` | `group` | `true`: holds an AI group at its parking spots. The group is removed and spawned again from the mission file (`env.mission`) under the same names, **uncontrolled** and cold on the same spots: engines off, waiting for a start command. Fails (`not in the mission file`) for groups spawned by scripts. Measured: the removal fires no `dead`, `crash`, `unit_lost` or `kill` events. |
| `group_release` | `group` | `true`: DCS's `Start` command for the group (a held group starts its engines and taxis) |
| `group_destination` | `group` | `{ inMission, id?, name?, x?, z? }`: where the group's route in the mission file lands (its last `Land` waypoint: airdrome, helipad or ship id and name). `inMission = false` for groups spawned by scripts; no `id` for a route that does not land. |
| `arrival_hold` | `group`, `flag`, `airdromeId`, `hold { x, z, alt, speed, x2?, z2? }`, `final { x, z, alt, speed }`, `land { x, z, alt }` | `true`: holds an arriving AI group. It gets a new route: to the holding fix, where it orbits (a circle, or a race track to `x2, z2`) at `alt` (m) and `speed` (m/s) until the user flag `flag` is 1; then to `final` and a landing waypoint for the airdrome at `land`, a point on final at the glide-path altitude (measured: at altitude 0 the AI dives to a few hundred feet miles out). The flag is set to 0 first. |
| `arrival_clear` | `flag` | `true`: sets the flag to 1: the holding group leaves the hold. |
| `debug_events` | `on` | `{ allEvents }`: while on, every DCS event is forwarded, not only those listed above (diagnostics) |

With `manageAi = false` in the server's `DigiTowerConfig.lua`, the hook refuses `group_hold`, `group_release`,
`arrival_hold` and `arrival_clear` (`AI handling is turned off on this server ...`).

**Held AI never stays held without the app:** when the app disconnects the hook releases every held group (departures
started, arrivals' flags set), and the mission side does the same by itself when the hook has not polled for 60 s.

Runway `course` is DCS's value in radians; the true heading is `-course`.

## Requirements in DCS

`Saved Games\<instance>\Config\autoexec.cfg` must allow hooks to run code in the mission (the installer adds this,
between `-- >>> DigiTower bridge` and `-- <<< DigiTower bridge`):

```lua
net = net or {}
net.allow_unsafe_api = net.allow_unsafe_api or {}
table.insert(net.allow_unsafe_api, "userhooks")
net.allow_dostring_in = net.allow_dostring_in or {}
table.insert(net.allow_dostring_in, "mission")
```

ED temporarily reverted this restriction in a hotfix, but the documentation still requires it, so the
install script adds it.

## DCS bug: `a_do_script` return values

Since DCS 2.9.18 (still in 2.9.30), `a_do_script()` passes return values back shifted: a `nil` is
prepended and the last value dropped, so `return a_do_script("return 42")` yields nothing
([forum report](https://forum.dcs.world/topic/376809-a_do_script-return-value-pass-thru-mangled-since-dcs-291812722/)).
The hook runs every piece of mission code inside a function whose result is returned twice and takes
the second value, which is correct with and without the bug. The fake DCS in `bridge/tests` reproduces
the bug by default (`fixedReturns = true` turns it off), and the tests cover both.

If injection fails anyway, the hook logs `mission injection failed` in `dcs.log` and reports it in `hello.error`.
