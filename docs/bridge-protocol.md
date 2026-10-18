# Bridge protocol

The hook (`bridge/Scripts/Hooks/DcsAtcBridgeHook.lua`) listens on **127.0.0.1:42700** (override in
`Scripts/DcsAtc/DcsAtcConfig.lua`, e.g. `return { port = 42701 }`). One app connects at a time; a new
connection replaces the old one. Both directions send **one JSON object per line** (UTF-8, `\n`).

Coordinates are DCS map coordinates in metres: `x` = north, `z` = east, `y` = altitude MSL.
Headings are true, in degrees.

## DCS → app

| `type` | When | Fields |
|---|---|---|
| `hello` | right after connecting | `protocol`, `mission`, `missionRunning`, `info` (mission info, if running), `multiplayer`, `error` (last hook error, e.g. a failed injection) |
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
| `airbase_weather` | `name` | `{ windX, windZ, temperature, pressure, elevation }`: wind 10 m above the field in m/s (the direction it blows **towards**), temperature in K and pressure in Pa at field elevation |
| `draw_route` | `key`, `coalition` (-1 = all), `points` (`[[x, z], ...]`), `color` (`[r, g, b, a]`, 0–1), `text` (optional, at the last point), `lineType`, `fontSize` | `{ marks }`; drawing again with the same `key` replaces the old drawing |
| `drawing_clear` | `key` | |

Runway `course` is DCS's value in radians; the true heading is `-course`.

## Requirements in DCS

`Saved Games\<instance>\Config\autoexec.cfg` must allow hooks to run code in the mission (added by
`bridge/install.ps1`):

```lua
net.allow_unsafe_api = { "userhooks" }
net.allow_dostring_in = { "mission" }
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
