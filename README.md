# DigiTower

*ATC for DCS World.* A realistic air traffic control for DCS World: ground (with real taxi
routes), tower, departure and approach, ATIS and FARPs, for jets and helicopters, on your own server. The
controllers talk on SRS, understand what pilots say on the frequency, and handle AI traffic, players first.

DigiTower runs on the PC that runs the mission: the dedicated server, or your own PC in single player and when you
host from the game. Players only need SRS. It is a beta: see [Requirements and limits](#requirements-and-limits).

## Install

1. Download **DigiTower-win-Setup.exe** from the [latest release](https://github.com/AtrixZockt/DigiTower/releases/latest)
   and run it. It installs for your Windows user (no admin rights), adds a desktop and a Start menu shortcut, and
   offers new versions itself. The builds are not code-signed, so Windows SmartScreen may ask once (*More info → Run
   anyway*). The portable zip on the same page works without installing.
2. In DigiTower, open the **Install** page:
   - **The bridge:** *Install* it for your DCS server's Saved Games folder (`DCS.dcs_serverrelease` or similar), or
     for the game's (`DCS`) when you fly single player or host from the game. DCS loads it when it starts, so restart
     DCS afterwards.
   - **The controllers' voice:** download *kokoro-multi-lang-v1_0* (400 MB). Without it a Windows voice speaks.
   - **Voice requests** (optional, to talk to the controllers): download the speech recognizer
     *ggml-large-v3-turbo-q5_0* (570 MB; it needs a graphics card, see [Requirements and
     limits](#requirements-and-limits)), then turn on *Settings → Voice requests*, pick *Whisper large-v3-turbo
     (GPU, Vulkan)* and save. The ATC uses new settings from its next start (*Stop ATC*, *Start ATC*).
3. Start your **SRS server**, in single player too (on your PC, with your SRS client connected to it). DigiTower
   joins it as the controllers; it does not start it.
4. Start the mission. DigiTower starts its ATC when it opens (the *Operations* page; *Start ATC* otherwise), and
   *DCS BRIDGE* there turns green once DCS is connected.

## Your first flight

An example at Al Dhafra (Persian Gulf); every airfield works the same way, on the frequencies DCS gives it
(F10 → Other → ATC → *airfield* → *Frequencies* lists them).

- **Your callsign** is your player name before "|": "Hangman 1-2 | DiGii" is called *Hangman 1-2*. Flights need the
  "Name 1-1" form.
- **SRS:** tune a radio (AM) to Al Dhafra **Ground 250.0**; Tower is **270.0**, Approach **257.0**, the ATIS **270.5**.
- **The calls:** F10 → Other → **ATC** → *Al Dhafra* lists the calls you can make right now. With voice requests on,
  say them on the frequency with your SRS push-to-talk instead ("Al Dhafra Ground, Hangman 1-2, request taxi").

1. *Request startup* (optional, parked): *"Hangman 1-2, Al Dhafra Ground, start up approved, wind 310 degrees, 10
   knots, QNH 1013."*
2. *Request taxi*: *"…, taxi to holding point runway 31R via A, B."* The route is drawn on the F10 map; Ground warns
   you if you leave it, taxi too fast or meet other traffic.
3. At the holding point Ground sends you to Tower (270.0). *Ready for departure* (own navigation, a direction or
   closed traffic): *"…, runway 31R, cleared for takeoff"*, with how to climb out.
4. After takeoff Tower sends you to Approach (257.0): check in.
5. Coming back: *Request approach* (ILS, TACAN, visual or overhead) to Approach. You get vectors to final, then
   *"contact Tower"*, and Tower clears you to land.
6. After landing: *"vacate, contact Ground"*. *Request taxi to parking* gives you a free spot, marked on the map.

The rest is in [Using DigiTower in DCS](#using-digitower-in-dcs): every call, AI traffic, emergencies.

## Requirements and limits

**What it needs**

- 64-bit Windows 10 or 11, DCS World and an SRS server (DCS-SimpleRadio-Standalone). Nothing else; .NET is included.
- DigiTower runs where the mission runs: on a dedicated server, the server's PC; in single player or when you host
  from the game, your PC. Players only need SRS. One DigiTower per DCS: a second one that connects takes over.
- About 250 MB for the program, 400 MB for the default voice, and 570 MB more for voice requests.
- **Voice requests** (optional): a microphone, your SRS push-to-talk, and a graphics card with Vulkan for the
  recommended recognizer, Whisper large-v3-turbo (about half a second per call). It is tested on an AMD Radeon;
  NVIDIA and Intel cards should work through Vulkan too. On the CPU it takes far too long; the smaller recognizers
  run on the CPU but understand less. Without voice requests everything works from the F10 menu.

**Limits (beta)**

- Tested mostly in single player and on one dedicated server; multiplayer with many players is little tested yet.
- Taxiway names exist for the Persian Gulf (27 airfields with charts). Elsewhere taxi routes follow DCS's taxi
  network but name no taxiways. Airfields DCS gives no ATC frequency (a few small ones) get text messages only.
- Departure and approach figures (altitudes, ranges) are tailored to Al Dhafra; other airfields use defaults.
- Pilots use standard phrases (request taxi, ready for departure, …); free speech is not understood. A strong accent
  can trip the recognizer; it can be [tuned on your voice](docs/voice.md#tuning-the-recognizer-on-your-voice).
- No carrier, AWACS or tanker control.
- AI: only AI flights from the mission file can be held at parking; AI spawned by scripts goes when DCS lets it. DCS
  flies AI approaches itself (no vectors, no go-arounds). AI helicopters are left to DCS.
- F10 map drawings (taxi routes, spots) are seen by the whole side, because DCS cannot draw for one group; they can
  be turned off (Settings, or the bridge settings on the Install page).
- The builds are not code-signed, so Windows SmartScreen asks once.

## The control panel and the console

DigiTower comes as two programs that share one settings file, `%APPDATA%\DigiTower\settings.json`:

- **`DigiTower.exe`**, the control panel. *Operations*: start and stop the ATC, status lights (DCS bridge, SRS,
  voice, voice requests, AI handling, map drawings), the mission, and per airfield the flight progress strips of
  Ground, Tower and Approach (or the FARP's controller) with how long each has had its clearance and what it waits
  for. *Log*: everything the controllers say and do, by category, with search; which categories are shown and which
  go to the log file is set there and applies at once. *Settings*: the rest (DCS folder, SRS, voice, voice requests,
  AI, map drawings, the Approach distances); the ATC uses them from its next start. *Install*: the bridge in each DCS
  Saved Games folder with its settings, and the voice and recognition models to download. Closing the window while
  the ATC runs keeps it running in the notification area (a setting); opening DigiTower again shows it. It can also
  start with Windows, in the notification area when you sign in (Settings).
- **`DigiTowerConsole.exe`**, the console: the same ATC without a window, for a server box, a scheduled task or a
  service. Its options override the settings file for that run:
  `--settings <file>`, `--host`/`--port` (the bridge), `--dcs <folder>`, `--data <folder>`, `--srs host:port`,
  `--voice <id>`, `--no-voice`, `--ai-voice <id>`, `--no-ai-voice`, `--transcribe [<id>]`, `--recordings <folder>`,
  `--no-ai`, `--no-drawings`, `--no-emergencies`, `--silence <airbase>`, `--keep-stock-atc`, `--dump <airbase>`, `--debug`.

Run from the repository (`dotnet run`), both find `D:\DCS\DCS World` (or `DIGITOWER_DCS_DIR`) and the repository's
`data\` and keep what they make where they did before: `data\voice\models`, `out\heard`, `out\logs`. Installed, the
models, recordings and logs go to `%LOCALAPPDATA%\DigiTower`.

## Using DigiTower in DCS

1. Install (or update) the bridge in the server and **restart the server** (hooks load at start-up): on the
   control panel's *Install* page (it also shows when an update of DigiTower brings a new bridge), or
   ```powershell
   .\bridge\install.ps1 -Instance DCS.dcs_serverrelease
   ```
   The first install also writes the bridge settings, every setting explained, to
   `Saved Games\DCS.dcs_serverrelease\Scripts\DigiTower\DigiTowerConfig.lua`; installing again keeps your changes. A
   server that does not want DigiTower to handle its AI adds `-NoAi` (`-Ai` turns it back on); one that does not want
   taxi routes and landing spots on the F10 map adds `-NoMapDrawings` (`-MapDrawings`; the app's `--no-drawings` does
   the same for one app).
2. Start the SRS server and the mission, then DigiTower and *Start ATC*. From the repository folder (it finds
   `D:\DCS\DCS World` and `data\`):
   ```powershell
   dotnet run --project tools/DcsAtc.VoiceTool -- fetch   # once: the voice model (or the control panel's Install page; else a Windows voice speaks)
   dotnet run --project src/DcsAtc.Gui                    # the control panel; or the console: src/DcsAtc.App (options above)
   ```
   The controllers speak on SRS (127.0.0.1:5002) at **every airfield**, on the ATC frequencies DCS gives it, the
   ones in your briefing: Ground, Tower and Approach share them (VHF and UHF AM, e.g. Al Minhad **118.55** and
   **250.1**), and DCS's own ATC there is silenced. Al Dhafra also has its own positions from chart data: **Ground
   250.0**, **Tower 270.0**, **Approach 257.0**. Every call is also shown as text. An airfield goes on the air (its
   ATIS broadcasts, voice requests are heard, its phrases are prepared) when a player comes within 60 NM. ILS
   frequencies and TACAN channels come from the DCS terrain (`beacons.lua`). The **ATIS** loops while anyone is tuned
   in, on the ATC UHF frequency + 10 MHz and the VHF + 1 MHz (Al Minhad 260.1 and 119.55; Al Dhafra 270.5): information
   letter, Zulu time, runway in use, expected approach, wind, visibility and weather, clouds (from the DCS weather
   preset), temperature, QNH and altimeter; a new letter with every significant change and every hour. Every
   airfield's ATIS can also be read in the menu, and *Frequencies* lists them all.
   The controllers' default voice is Kokoro "Michael"; another is chosen in Settings (the voices:
   [docs/voice.md](docs/voice.md#voices)).
3. In the aircraft, F10 → Other → **ATC** → *airfield* → the call. On the ground the airfield is the one you are
   at; in the air the five nearest within 100 NM (nearest first; the order only changes when the list does), plus
   any airfield working you. Under each, the menu only shows the calls you can make right now and changes as you go
   (parked: *Request startup*, *Request taxi*; taxiing: *Cancel taxi*, *Say again (Ground)*; at the holding point: *Ready for
   departure*; airborne: *Request approach*, *Request landing*; …), and **Frequencies** (ATIS, Ground, Tower,
   Approach, ILS, TACAN). Every call goes to the airfield you picked. Menus are per group in DCS, so every flight
   sees its own; players sharing one group (e.g. a two-seater) share its menu. *Flight* and *Debug* sit next to the
   airfields.

   | Menu | What happens |
   |---|---|
   | Request taxi | Taxi clearance to the holding point of the runway in use (from the wind; with parallel runways the closer one), e.g. *"Hangman 1-1, Al Dhafra Ground, taxi to holding point runway 31R via A, B. Wind 310 degrees, 10 knots, QNH 1013, altimeter 29.92."* The route is drawn on the F10 map (coalition-wide). Ground warns if you leave the route or taxi faster than 30 kt; at the holding point: *hold short, contact Tower*. Where no taxiway leads to the threshold (Fujairah, Batumi, Khasab and others), the route ends at the holding point before the runway and Tower has you backtrack. |
   | Request startup | Optional, while parked: *"Hangman 1-2, Al Dhafra Ground, start up approved, wind 310 degrees, 10 knots, QNH 1013, altimeter 29.92."* Ground can say it again until you taxi. How you depart is asked for at the holding point. |
   | Ready for departure → Own navigation / Departure north, east, south, west / Closed traffic | At the holding point: *cleared for takeoff*, *line up and wait* (behind a departing aircraft) or *hold short, runway occupied / traffic on final / number two for departure*; you are cleared as soon as the runway allows. Without a taxiway to the threshold: *"runway 11, backtrack and line up"* (once nobody is on final within 10 NM), then *cleared for takeoff* when you are lined up at the threshold. The clearance says how to climb out, from your choice and the traffic: *unrestricted climb approved*; *after departure turn right heading 360*; under pattern traffic *fly runway heading, maintain at or below 500 feet until 3 miles, then …*; behind a departing aircraft *fly runway heading, climb and maintain 3000 feet until advised* (later *climb unrestricted, resume own navigation*); *right closed traffic approved*. Tower warns *check altitude / check heading* if you break a restriction. After takeoff: *contact Approach* (closed traffic stays with Tower: touch and go, or a full stop and *vacate*). The numbers are per airfield (`"departure"` in its labels file). |
   | Request landing | Airborne within 30 NM: runway, wind, QNH, *report final*. Cleared to land within 6 NM on final (or once you report final) if the runway is free; *go around* if it gets blocked inside 1 NM. After landing: *vacate, contact Ground*; *backtrack and vacate via D* when the way off to the parking is behind you. |
   | Report final | Report final early (landing clearance as soon as the runway allows). |
   | Report initial | Overhead: *break approved*; cleared to land on the downwind (or on final) when the runway allows. |
   | Check in (Tower) | After Approach's *contact Tower*: Tower already expects you for that airfield and runway (even if another airfield is nearer) and answers *cleared to land* or *continue approach*; if you do not check in, it calls you itself at 6 NM. Without Approach, *Request landing* goes to the airfield you are lined up on final for, else the nearest one with chart data. |
   | Request approach → ILS / TACAN / Visual / Overhead | Within 60 NM (Settings: *Approach request range*): *radar contact, 35 miles northwest*, a descent, QNH, the approach to expect (with the localizer frequency or TACAN channel from DCS), then radar vectors: join, downwind, base, intercept, *maintain 3000 feet until established on the localizer, cleared ILS approach runway 31R*, and at about 8 NM *contact Tower*. Arrivals are spaced about 5 NM on final (*extend downwind, number 2*); with more than three at once the later ones *hold over the Al Dhafra TACAN* at 7000 ft and up until a slot is free. Visual: *report field in sight* → *cleared visual approach*. Overhead: *proceed to initial*, at 20 NM down to 1500 ft, then *contact Tower, report initial*. Approach watches altitude and heading (*check altitude / check heading*) and calls traffic: *traffic, 2 o'clock, 5 miles, opposite direction, 2000 feet above*. |
   | Check in (Approach) | After Tower's hand-off on departure: *radar contact*; held below descending arrivals if need be (*maintain 5000 feet until advised* → *climb unrestricted*); at 40 NM (Settings: *Radar service radius*) *radar service terminated*. |
   | Field in sight, Cancel approach, Say again (Approach) | As named. |
   | Request taxi to parking | Route to the free spot that is quickest to reach, named by its area: *"taxi to Apron 1 West via D2, A"*. |
   | Cancel taxi, Say again (Ground / Tower), ATIS, Frequencies | As named. *Debug → Where am I?* shows your position. |
   | **FARPs** (helicopters) | Each FARP site has a controller of its own, called by the site's name, on the frequency the mission gives its FARPs (Retribution: 127.5 for all; the nearest site answers a call). FARPs sharing a name ("FOB Laos Invisible FARP 0", "… 1", …) or within 1 km are one site; its pads are numbered. On a pad: *Request startup*, **Request departure → Own navigation / north / east / south / west** (*"Hangman 1-2, FOB Laos, from present position, make right turn for north departure, wind …, cleared for takeoff"*; *hold position, helicopter traffic* while another helicopter takes off or lands within 200 ft), and once clear *"frequency change approved, good day"*. In the air within 15 NM: **Request landing** gives you a free pad (the nearest, never within 200 ft of one another helicopter is landing on or leaving), marked on the F10 map: *"make straight-in to pad 4, wind …, cleared to land"* (*all pads occupied, landing will be at your own risk*). Also *Say again* and *Frequencies*, and by voice. DCS's own FARP ATC is silenced where we talk. |
   | Debug → Private pin test / Remove private pin | Multiplayer test: a pin at your position meant only for your group (DCS `markToGroup`), *"Only Hangman 1-2 should see this"*. The map drawings (taxi routes, landing spots) are seen by **everyone on your side**: DCS can draw lines and text only for a whole side. If the others do not see this pin, private pins can mark each player's own route end and spot. |
   | **Helicopters** | *Request taxi* becomes **air-taxi** (*"air-taxi to holding point runway 31L via U9"*): hovering is fine (DCS calls a hover "in the air"; below 300 ft over the airfield it still counts as on the field), no speed limit, warned above 100 ft (*remain at or below 100 feet*) or far off the route. Unless Ground sent you to the runway, **Request departure → Own navigation / Departure north, east, south, west** clears you from where you stand, however close a runway is: *"Hangman 1-1, Al Dhafra Tower, from present position, make right turn for north departure, wind 310 degrees, 10 knots, cleared for takeoff"*; you *hold position* while your way out crosses a runway in use (traffic on final, a takeoff or landing cleared, an aircraft on it) or another helicopter takes off or lands within 200 ft. After *Request taxi* to the runway, *Ready for departure* at the holding point works as for jets. **Request landing** gives you a free helicopter spot (helicopter-only first, then open parking), marked on the F10 map: *"make straight-in to Apron 3, wind …, cleared to land"*; no runway, no taxi-in, and jets on the runway are not held for you (no free spot: a runway landing). Hovering low over the field you get both the ground calls and *Request landing*; no *Report initial*. |
   | Emergency → Mayday / Pan Pan (in the air) | A priority landing at the nearest airfield of your side (or neutral) with a runway; the controller you called stays with you: *"Hangman 1-2, roger Mayday, nearest airfield Al Dhafra, heading 290, 25 miles, runway 31R, wind 310 degrees, 10 knots, cleared to land"* (heading and distance from 10 NM out). **Mayday**: cleared at once, never sent around; *"All stations, stop transmitting, Mayday"*; traffic cleared to land behind you goes around (*go around, emergency traffic*), departures hold short, AI waits and its pilots stay quiet; once you are down and slow, *"All stations, distress traffic ended"*. **Pan Pan**: you land next, nobody is sent around (*continue approach, number two* behind traffic already landing). *Cancel Mayday / Pan Pan* ends it. Also by voice ("Mayday, Mayday, Mayday, …"). On the ground an emergency is only acknowledged. Servers can turn this off: *Settings → Emergency calls*, or the console's `--no-emergencies`. |
   | Flight → Flight of 2 / 3 / 4, Single ship | Declares a flight: you and the players with your callsign prefix ("Hangman 1-1", "Hangman 1-2" → "Hangman 1 flight"), lowest number leads; members who spawn later join until the flight is complete. Ground and Tower then give one clearance to *"Hangman 1 flight"* (sent to every member, from the lead's position), and members are never traffic to each other. Any member may make the calls. The flight takes off on one clearance, formation or interval as you like; Tower keeps the runway for it until the last member is airborne. Taxiing in, every member gets its own parking spot next to the lead's, marked on the F10 map with its number (1-1, 1-2, …); a helicopter flight gets a landing spot or FARP pad each (*"make straight-in to pads 1 and 2"*). *Single ship* leaves the flight. |

   **Voice:** with *voice requests* on (Settings, or the console's `--transcribe`), the same calls can be made by talking on the frequency with your normal SRS
   push-to-talk ("Al Dhafra Ground, Hangman 1-2, request taxi"): [docs/voice.md](docs/voice.md#voice-requests).

   **AI departures** (in your missions and Liberation / Retribution campaigns): when an AI plane starts its engines
   at a parking spot of an airfield with chart data, ATC decides whether it may go, players first: never while a
   player is lined up, taking off, landing or arriving within 8 NM; while a player taxis out or waits at the holding
   point, for at most 5 minutes; at most 2 AI groups taxiing at once. If it must wait it is **held**: DCS spawns it
   again on the same spots with its engines off (you see them shut down), and starts it when its turn comes (about
   two minutes from start to rolling). Ground calls it as it starts to roll (*"Enfield 2-1, Al Dhafra Ground, taxi
   to holding point runway 31R"*) and Tower clears it for takeoff as it reaches the runway; DCS picks its taxi route
   and runway (from the wind, as we do). AI spawned by scripts (not in the mission file) cannot be held and goes
   when DCS wants; AI whose engines already ran when DigiTower connected is left alone; if DigiTower stops, every
   held AI is started. *Handle AI traffic* in Settings (the console's `--no-ai`) turns AI handling off in
   DigiTower; a server that does not want it turns it off for everyone: *DigiTower handles AI traffic* on the
   Install page, `.\bridge\install.ps1 -Instance <instance> -NoAi`, or `manageAi = false` in the bridge settings
   file, then restart DCS.

   **AI arrivals**: an AI flight whose mission route ends with a landing at an airfield near a player is taken over as
   it comes back within 30 NM (Liberation and Retribution routes end that way; AI spawned by scripts stays plain
   traffic). Players first: while a player is landing, on approach, inbound close in, on the runway or about to take
   off, the AI **holds** in the same stack as players, over the TACAN (or the field) from 7000 ft up (*"Colt 2-1, Al
   Dhafra Approach, radar contact, 14 miles north of Al Dhafra. Hold over the Al Dhafra TACAN, right turns, maintain
   7000 feet."*), and AI comes in one at a time. When its turn comes it leaves the hold for a point 10 NM out on final
   (*"leave the hold, proceed to final runway 31R, descend and maintain 3000 feet"*) and Tower clears it once it is on
   final. An AI never holds longer than 10 minutes (fuel). With nobody in the way it comes straight in. DCS flies the
   approach and the landing itself, so there are no vectors and no go-arounds for AI. The same switches as for AI
   departures turn it off, and AI still holding is let go when DigiTower stops.

   **AI pilots on the radio**: with voice on and AI traffic handled (in DigiTower and on the server), AI pilots
   talk too, in their own voice (Settings, *AI pilot voice*; by default one that is not the controllers'), with a
   cockpit sound so you can tell them from the controllers. They call first and read back: *"Al Dhafra Approach, Colt
   2-1, 14 miles north of Al Dhafra, inbound for landing."* … *"Hold over the TACAN, 7000 feet, Colt 2-1."*; *"Al
   Dhafra Ground, Enfield 1-1, request taxi."*; *"Al Dhafra Tower, Colt 2-1, final runway 31R."* … *"Cleared to land
   runway 31R, Colt 2-1."*

   **Nobody is stepped on**: nothing DigiTower says starts while anyone (players included) is talking on that
   frequency, or until it has been quiet for half a second; airfields sharing a frequency take turns; an AI exchange
   (call, answer, readback) is one turn; and after a call to a player the frequency stays quiet for the readback
   (until you have read back, at most 4 seconds).

   **Traffic on the ground**: while you taxi, Ground watches everything else on the airfield's taxiways (players
   and AI) about 40 seconds ahead. If your paths would meet, the one giving way is told so, with the other aircraft
   by type and where it comes from: *"Hangman 1-2, Al Dhafra Ground, hold short of B, give way to the Hornet from
   the left"*, or for opposite directions *"…, hold short of A, give way to the F-16 opposite direction on A"* (a
   red HOLD line on the F10 map marks where), then *"continue taxi"* once it has passed. Catching up with an
   aircraft ahead: *"follow the F-16 ahead"*. Who gives way: AI and players without a taxi clearance cannot be
   told, so you give way to them; between two cleared flights the one taxiing out goes first, otherwise the rules
   of the air (the one with the other on its right gives way); and whoever is already on the shared stretch or in
   the junction keeps going. Roll on past your hold and you hear *"hold position"*. Members of a declared flight
   are never traffic to each other; helicopters are left out.

   Every aircraft counts as traffic for Tower, AI included. Airfields without chart data get routes and
   map drawings without taxiway names; airfields DCS gives no ATC frequency (a few small ones) get text messages only. Callsigns come from the player name before "|"
   ("Hangman 1-1 | DiGii" → "Hangman 1-1"), otherwise the whole name; flights need the
   "Name 1-1" form.

## Troubleshooting

- **Nothing happens in DCS.** The bridge loads when DCS starts: restart DCS (or the server) after installing or
  updating it. *DCS BRIDGE* on the Operations page shows whether DigiTower is connected. Look for `DigiTower` lines
  in `Saved Games\<instance>\Logs\dcs.log`; `mission injection failed` means the `autoexec.cfg` block is missing
  (install the bridge again). Installing the bridge copies two Lua files, writes the settings file
  `Scripts\DigiTower\DigiTowerConfig.lua` if there is none, and adds a marked block to the instance's
  `Config\autoexec.cfg`; uninstalling removes the files and the block and keeps the settings. An install from before
  the rename (DCS-ATC) is moved over.
- **No voice.** Start the SRS server first; *SRS* and *VOICE* on the Operations page show the connection and the
  voice in use. Without a downloaded voice a Windows voice speaks.
- **What does DigiTower see?** F10 → Other → ATC → *Debug* → *Where am I?*; the Log page shows every call and event.
- **Airbase data for labelling** (`data/dcs/<airbase>.json`, runways and parking spots):
  `DigiTowerConsole --dump "Al Dhafra AFB"`.
- **Voice requests not understood.** *VOICE REQUESTS* on the Operations page shows the recognizer in use; the Log page
  (category VOICE IN) shows what it heard. Use the standard phrases from the F10 menu; the recognizer is set in
  *Settings → Voice requests*.
- **The connection keeps dropping.** Only one DigiTower can work a DCS: a second one that connects (another PC, a
  test run) takes over the bridge.
- **Reporting a problem:** [open an issue](https://github.com/AtrixZockt/DigiTower/issues/new/choose) and add the
  day's log file, `%LOCALAPPDATA%\DigiTower\logs\digitower-<date>.log` (Windows key + R, paste the folder, Enter).

## For developers

Building DigiTower from source, testing it, and the tools for the taxi networks.

### Layout

| Path | What |
|---|---|
| `bridge/Scripts/Hooks/DigiTowerHook.lua` | Runs inside DCS. Talks to the app over 127.0.0.1:42700 and injects the mission side. |
| `bridge/Scripts/DigiTower/DigiTowerMission.lua` | Runs in the mission: unit states, events, F10 menus, text, airbase data. |
| `bridge/install.ps1` | Installs/uninstalls the bridge in a DCS Saved Games folder (the control panel's Install page does the same). |
| `bridge/tests/` | Lua tests and a **fake DCS** that runs the real hook without the game. |
| `src/DcsAtc.Bridge` | C# client for the bridge. |
| `src/DcsAtc.Airfields` | Airfield data: reader for DCS taxi networks (`.rn5`), routing graph, runways and holding points, taxi routing. |
| `data/dcs/` | Airbase data dumped from DCS by the app (`--dump`). |
| `data/airfields/<theatre>/` | Hand-labelled airfields: taxiway names, areas, frequencies ([docs/labeling.md](docs/labeling.md)). |
| `src/DcsAtc.Atc` | The ATC logic: ground control, tower, flights, phraseology, airfield catalog. |
| `src/DcsAtc.Srs` | Headless SRS client: the controllers' radio stations (TCP sync, UDP Opus voice). |
| `src/DcsAtc.Voice` | The ATC voice: how text is said, TTS engines (Kokoro/Piper via sherpa-onnx, Windows), clip cache, stitching. |
| `src/DcsAtc.Recognition` | Speech recognition for voice requests: Whisper (whisper.cpp) and Parakeet (sherpa-onnx), transcript normalising, test calls ([docs/voice.md](docs/voice.md#speech-recognition)). |
| `data/voice/` | `voices.json` (the voices), `recognition.json` (the recognizers), `test-calls.txt` (the recognition test), `train-calls.txt` (practice calls for tuning it); `models/` and `cache/` are downloaded / generated, not in git ([docs/voice.md](docs/voice.md)). |
| `src/DcsAtc.Host` | Runs DigiTower for both programs: connects to DCS, F10 menus, runs the controllers, speaks on SRS; the settings file, the log, the bridge installer, model downloads. |
| `src/DcsAtc.Gui` | The control panel, `DigiTower.exe` (WPF): status, flight strips, log, settings, install. |
| `src/DcsAtc.App` | The console program, `DigiTowerConsole.exe`: the same without a window (servers, scheduled tasks). |
| `tools/DcsAtc.Rn5Tool` | Inspects `.rn5` files, renders them as SVG, scans whole DCS installs. |
| `tools/DcsAtc.VoiceTool` | Voice audition, test transmissions, SRS check without DCS, model download, speech-recognition bake-off. |
| `tools/Convert-WhisperModel.ps1` | Converts a Whisper model from Hugging Face (a fine-tune) to whisper.cpp format for the app. |
| `tools/Train-Whisper.ps1`, `tools/whisper-train/` | Tunes the speech recognizer on your own voice (overnight): [docs/voice.md](docs/voice.md#tuning-the-recognizer-on-your-voice). |
| `tests/DcsAtc.Tests` | C# tests. |
| `docs/bridge-protocol.md` | The JSON protocol between DCS and the app. |
| `docs/labeling.md` | How to add taxiway names for an airfield. |
| `docs/voice.md` | The ATC voice on SRS: how it works, setup, choosing the voice, the SRS protocol. |
| `docs/releasing.md` | Making a release: `tools/Publish-Release.ps1`, the installer and updates (Velopack), uploading to GitHub. |

### What you need

- .NET 10 SDK
- For the Lua tests and the fake DCS: Lua 5.1 with LuaSocket (e.g. Lua for Windows). DCS itself uses Lua 5.1.

### Build and test

```powershell
dotnet build DcsAtc.slnx
dotnet test DcsAtc.slnx                       # integration tests skip themselves if Lua or DCS is missing
lua bridge/tests/test_bridge.lua              # Lua tests (hook + mission side)
```

### Try it without DCS

```powershell
lua bridge/tests/fake_dcs.lua --static --click "Request taxi"   # terminal 1: fake mission at Al Dhafra (real layout)
dotnet run --project src/DcsAtc.Gui                             # terminal 2: the control panel (or src/DcsAtc.App, the console)
```

### Taxi networks

```powershell
dotnet run --project tools/DcsAtc.Rn5Tool -- info "D:\DCS\DCS World\Mods\terrains\PersianGulf\AirfieldsTaxiways\Dhafra_AFB.rn5"
dotnet run --project tools/DcsAtc.Rn5Tool -- svg  "<file.rn5>" out\dhafra.svg 0.33
dotnet run --project tools/DcsAtc.Rn5Tool -- scan "D:\DCS\DCS World\Mods\terrains"

# with the DCS airbase dump from the app (--dump): runways, holding points, parking spots
dotnet run --project tools/DcsAtc.Rn5Tool -- airfield "<file.rn5>" "data\dcs\Al Dhafra AFB.json" out\dhafra.svg
# taxi route from a parking spot (DCS Term_Index) to a runway's departure holding point
dotnet run --project tools/DcsAtc.Rn5Tool -- route "<file.rn5>" "data\dcs\Al Dhafra AFB.json" 106 13L out\route.svg [--steps]
# every taxi network edge that has a taxiway name (from the airfield's labels file)
dotnet run --project tools/DcsAtc.Rn5Tool -- names "<file.rn5>"
```

## License

DigiTower is free to download and use for any noncommercial purpose, including on DCS servers you run. You may
change it for your own use and send your changes to this project (pull requests), but you may not distribute it
or your changed versions of it. The terms are the [PolyForm Strict License 1.0.0](https://polyformproject.org/licenses/strict/1.0.0)
with two additional permissions; see [LICENSE](LICENSE).

Third-party parts keep their own licenses:

| Part | License |
|---|---|
| [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) (text-to-speech engine, with ONNX Runtime) | Apache-2.0 |
| [Concentus](https://github.com/lostromb/concentus) (Opus codec) | BSD-style (Opus license) |
| System.Speech (Windows voices) | MIT |
| [Whisper.net](https://github.com/sandrohanea/whisper.net) and [whisper.cpp](https://github.com/ggml-org/whisper.cpp) (speech recognition) | MIT |
| Speech-recognition models (downloaded on the Install page or by `VoiceTool fetch --recognition`, not part of this repository): OpenAI Whisper; NVIDIA [Parakeet TDT 0.6B v2](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2) | MIT; CC-BY-4.0 |
| Voice models (downloaded on the Install page or by `VoiceTool fetch`, not part of this repository) | per voice: [docs/voice.md](docs/voice.md#voices) |
| [DejaVu fonts](https://dejavu-fonts.github.io/) (the control panel's font, as in DCS) | Bitstream Vera license, changes public domain (`src/DcsAtc.Gui/Fonts/LICENSE-DejaVu.txt`) |
| [Velopack](https://github.com/velopack/velopack) (installer and updates) | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) (control panel) | MIT |
| DCS World and its files (taxi networks, beacons, cloud presets: read from your installation, not included) | Eagle Dynamics |

Credits: the SRS client follows the protocol of [DCS-SimpleRadioStandalone](https://github.com/ciribob/DCS-SimpleRadioStandalone)
(GPL-3.0; implemented from its documentation and source, no SRS code is included). The Persian Gulf taxiway names come
from the Master Arms charts ([wiki.masterarms.se](https://wiki.masterarms.se/index.php/Persian_Gulf_Charts)) and
CombatWombat's airfield diagrams for the Persian Gulf (each labels file names its chart).
The maps' time zones, and the way AI arrivals are held (a new route with an orbit that ends on a flag), follow
[MOOSE](https://github.com/FlightControl-Master/MOOSE).

---

I had help from AI while making DigiTower: Claude by Anthropic (in Claude Code) helped with the design and the
programming, and with testing and troubleshooting. Most of the work I did myself.
