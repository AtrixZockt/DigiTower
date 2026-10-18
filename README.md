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
| `src/DcsAtc.Airfields` | Airfield data: reader for DCS taxi networks (`.rn5`), routing graph, runways and holding points, taxi routing. |
| `data/dcs/` | Airbase data dumped from DCS by the app (`--dump`). |
| `data/airfields/<theatre>/` | Hand-labelled airfields: taxiway names, areas, frequencies ([docs/labeling.md](docs/labeling.md)). |
| `src/DcsAtc.Atc` | The ATC logic: ground control, tower, flights, phraseology, airfield catalog. |
| `src/DcsAtc.Srs` | Headless SRS client: the controllers' radio stations (TCP sync, UDP Opus voice). |
| `src/DcsAtc.Voice` | The ATC voice: how text is said, TTS engines (Kokoro/Piper via sherpa-onnx, Windows), clip cache, stitching. |
| `data/voice/` | `voices.json` (the voices); `models/` and `cache/` are downloaded / generated, not in git ([docs/voice.md](docs/voice.md)). |
| `src/DcsAtc.App` | The ATC app (console): connects to DCS, F10 menus, runs the controllers, speaks on SRS. |
| `tools/DcsAtc.Rn5Tool` | Inspects `.rn5` files, renders them as SVG, scans whole DCS installs. |
| `tools/DcsAtc.VoiceTool` | Voice audition, test transmissions, SRS check without DCS, model download. |
| `tests/DcsAtc.Tests` | C# tests. |
| `docs/bridge-protocol.md` | The JSON protocol between DCS and the app. |
| `docs/labeling.md` | How to add taxiway names for an airfield. |
| `docs/voice.md` | The ATC voice on SRS: how it works, setup, choosing the voice, the SRS protocol. |

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
lua bridge/tests/fake_dcs.lua --static --click "Request taxi"   # terminal 1: fake mission at Al Dhafra (real layout)
dotnet run --project src/DcsAtc.App                             # terminal 2
```

## ATC in DCS (Ground and Tower)

1. Install (or update) the bridge in the server and **restart the server** (hooks load at start-up):
   ```powershell
   .\bridge\install.ps1 -Instance DCS.dcs_serverrelease
   ```
2. Start the SRS server and the mission, then the app from the repository folder (it finds
   `D:\DCS\DCS World` and `data\`):
   ```powershell
   dotnet run --project tools/DcsAtc.VoiceTool -- fetch   # once: the voice models (else a Windows voice speaks)
   dotnet run --project src/DcsAtc.App                    # options: --srs host:port, --voice <id>, --no-voice
   ```
   At airfields with chart data (so far Al Dhafra) the stock ATC is silenced. The controllers speak on SRS
   (127.0.0.1:5002): at Al Dhafra **Ground 250.0**, **Tower 270.0** and **Approach 257.0** AM; every call is also
   shown as text. ILS frequencies and TACAN channels come from the DCS terrain (`beacons.lua`).
   Choosing the voice: [docs/voice.md](docs/voice.md).
3. In the aircraft, F10 → Other → **ATC** → *airfield* → the call. On the ground the airfield is the one you are
   at; in the air the five nearest within 100 NM (nearest first; the order only changes when the list does), plus
   any airfield working you. Under each, the menu only shows the calls you can make right now and changes as you go
   (parked: *Request taxi*; taxiing: *Cancel taxi*, *Say again (Ground)*; at the holding point: *Ready for
   departure*; airborne: *Request approach*, *Request landing*; …), and **Frequencies** (ATIS, Ground, Tower,
   Approach, ILS, TACAN). Every call goes to the airfield you picked. Menus are per group in DCS, so every flight
   sees its own; players sharing one group (e.g. a two-seater) share its menu. *Flight* and *Debug* sit next to the
   airfields.

   | Menu | What happens |
   |---|---|
   | Request taxi | Taxi clearance to the holding point of the runway in use (from the wind; with parallel runways the closer one), e.g. *"Hangman 1-1, Al Dhafra Ground, taxi to holding point runway 31R via A, B. Wind 310 degrees, 10 knots, QNH 1013, altimeter 29.92."* The route is drawn on the F10 map (coalition-wide). Ground warns if you leave the route or taxi faster than 30 kt; at the holding point: *hold short, contact Tower*. |
   | Ready for departure → Own navigation / Departure north, east, south, west / Closed traffic | At the holding point: *cleared for takeoff*, *line up and wait* (behind a departing aircraft) or *hold short, runway occupied / traffic on final / number two for departure*; you are cleared as soon as the runway allows. The clearance says how to climb out, from your choice and the traffic: *unrestricted climb approved*; *after departure turn right heading 360*; under pattern traffic *fly runway heading, maintain at or below 500 feet until 3 miles, then …*; behind a departing aircraft *fly runway heading, climb and maintain 3000 feet until advised* (later *climb unrestricted, resume own navigation*); *right closed traffic approved*. Tower warns *check altitude / check heading* if you break a restriction. After takeoff: *contact Approach* (closed traffic stays with Tower: touch and go, or a full stop and *vacate*). The numbers are per airfield (`"departure"` in its labels file). |
   | Request landing | Airborne within 30 NM: runway, wind, QNH, *report final*. Cleared to land within 6 NM on final (or once you report final) if the runway is free; *go around* if it gets blocked inside 1 NM. After landing: *vacate, contact Ground*. |
   | Report final | Report final early (landing clearance as soon as the runway allows). |
   | Report initial | Overhead: *break approved*; cleared to land on the downwind (or on final) when the runway allows. |
   | Check in (Tower) | After Approach's *contact Tower*: Tower already expects you for that airfield and runway (even if another airfield is nearer) and answers *cleared to land* or *continue approach*; if you do not check in, it calls you itself at 6 NM. Without Approach, *Request landing* goes to the airfield you are lined up on final for, else the nearest one with chart data. |
   | Request approach → ILS / TACAN / Visual / Overhead | Within 60 NM: *radar contact, 35 miles northwest*, a descent, QNH, the approach to expect (with the localizer frequency or TACAN channel from DCS), then radar vectors: join, downwind, base, intercept, *maintain 3000 feet until established on the localizer, cleared ILS approach runway 31R*, and at about 8 NM *contact Tower*. Arrivals are spaced about 5 NM on final (*extend downwind, number 2*); with more than three at once the later ones *hold over the Al Dhafra TACAN* at 7000 ft and up until a slot is free. Visual: *report field in sight* → *cleared visual approach*. Overhead: *proceed to initial*, at 20 NM down to 1500 ft, then *contact Tower, report initial*. Approach watches altitude and heading (*check altitude / check heading*) and calls traffic: *traffic, 2 o'clock, 5 miles, opposite direction, 2000 feet above*. |
   | Check in (Approach) | After Tower's hand-off on departure: *radar contact*; held below descending arrivals if need be (*maintain 5000 feet until advised* → *climb unrestricted*); at 40 NM *radar service terminated*. |
   | Field in sight, Cancel approach, Say again (Approach) | As named. |
   | Request taxi to parking | Route to the free spot that is quickest to reach, named by its area: *"taxi to Apron 1 West via D2, A"*. |
   | Cancel taxi, Say again (Ground / Tower), Frequencies | As named. *Debug → Where am I?* shows your position. |
   | Flight → Flight of 2 / 3 / 4, Single ship | Declares a flight: you and the players with your callsign prefix ("Hangman 1-1", "Hangman 1-2" → "Hangman 1 flight"), lowest number leads; members who spawn later join until the flight is complete. Ground and Tower then give one clearance to *"Hangman 1 flight"* (sent to every member, from the lead's position), and members are never traffic to each other. Any member may make the calls. *Single ship* leaves the flight. |

   Every aircraft counts as traffic for Tower, AI included. Airfields without chart data get routes and
   map drawings without taxiway names, and text messages only (we have no frequencies for them). Callsigns come from the player name before "|"
   ("Hangman 1-1 | DiGii" → "Hangman 1-1"), otherwise the whole name; flights need the
   "Name 1-1" form.

## First bridge test (spike S1)

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

# with the DCS airbase dump from the app (--dump): runways, holding points, parking spots
dotnet run --project tools/DcsAtc.Rn5Tool -- airfield "<file.rn5>" "data\dcs\Al Dhafra AFB.json" out\dhafra.svg
# taxi route from a parking spot (DCS Term_Index) to a runway's departure holding point
dotnet run --project tools/DcsAtc.Rn5Tool -- route "<file.rn5>" "data\dcs\Al Dhafra AFB.json" 106 13L out\route.svg [--steps]
```
