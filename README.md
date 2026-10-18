# DCS-ATC

A realistic air traffic control for DCS World: clearance, ground (with real taxi routes), tower,
departure and approach, for jets and helicopters, on your own server. The plan and the research
behind it are in [RESEARCH.md](RESEARCH.md).

## Layout

| Path | What |
|---|---|
| `bridge/Scripts/Hooks/DcsAtcBridgeHook.lua` | Runs inside DCS. Talks to the app over 127.0.0.1:42700 and injects the mission side. |
| `bridge/Scripts/DcsAtc/DcsAtcMission.lua` | Runs in the mission: unit states, events, F10 menus, text, airbase data. |
| `bridge/install.ps1` | Installs/uninstalls the bridge in a DCS Saved Games folder. |
| `bridge/tests/` | Lua tests and a **fake DCS** that runs the real hook without the game. |
| `src/DcsAtc.Bridge` | C# client for the bridge. |
| `src/DcsAtc.Airfields` | Airfield data. Currently: reader for DCS taxi networks (`.rn5`). |
| `src/DcsAtc.App` | The ATC app. Currently: the bridge test (spike S1). |
| `tools/DcsAtc.Rn5Tool` | Inspects `.rn5` files, renders them as SVG, scans whole DCS installs. |
| `tests/DcsAtc.Tests` | C# tests. |
| `docs/bridge-protocol.md` | The JSON protocol between DCS and the app. |

## Requirements

- .NET 10 SDK
- For the Lua tests and the fake DCS: Lua 5.1 with LuaSocket (e.g. Lua for Windows). DCS itself uses Lua 5.1.

## Build and test

```powershell
dotnet build DcsAtc.slnx
dotnet test DcsAtc.slnx                       # integration tests skip themselves if Lua or DCS is missing
lua bridge/tests/test_bridge.lua              # Lua tests (hook + mission side)
```

## Try it without DCS

```powershell
lua bridge/tests/fake_dcs.lua --click         # terminal 1: fake mission at Al Dhafra
dotnet run --project src/DcsAtc.App           # terminal 2
```

## Try it in DCS (spike S1)

1. Install the bridge into the dedicated server (or `-Instance DCS` for single-player):
   ```powershell
   .\bridge\install.ps1 -Instance DCS.dcs_serverrelease
   ```
   This copies two Lua files and adds a marked block to that instance's `Config\autoexec.cfg`
   (`-Uninstall` removes both).
2. Start the server with a mission that has a client slot at Al Dhafra, then start the app:
   ```powershell
   dotnet run --project src/DcsAtc.App -- --dump "Al Dhafra AFB" --silence "Al Dhafra AFB"
   ```
3. Join and spawn. F10 → Other → **ATC (test)** → *Ping* / *Where am I?* answer with a text message.
   The console shows traffic and events; `data/dcs/Al Dhafra AFB.json` gets the real runway and parking data.
4. If nothing happens, look for `DCS-ATC` lines in `Saved Games\DCS.dcs_serverrelease\Logs\dcs.log`.

## Taxi networks

```powershell
dotnet run --project tools/DcsAtc.Rn5Tool -- info "D:\DCS\DCS World\Mods\terrains\PersianGulf\AirfieldsTaxiways\Dhafra_AFB.rn5"
dotnet run --project tools/DcsAtc.Rn5Tool -- svg  "<file.rn5>" out\dhafra.svg 0.33
dotnet run --project tools/DcsAtc.Rn5Tool -- scan "D:\DCS\DCS World\Mods\terrains"
```
