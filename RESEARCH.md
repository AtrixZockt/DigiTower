# DCS ATC Overhaul — Feasibility Research

*Researched 2026-10-18 · DCS 2.9.30.28738 (local install) · MOOSE `master-ng` @ `ba6091d` (2026-10-11)*

Goal: replace the stock DCS ATC (on the ground and in the air) with something that behaves like real ATC:
clearance → startup → taxi with real routes → hold short → line up / takeoff → departure → approach → landing → taxi to parking.
Preferably **without AI**.

---

## 0. Short answer

| Question | Answer |
|---|---|
| Is it possible? | **Yes.** A working precedent exists: **AI_ATC** (Nellis AFB) does Clearance / Ground / Tower / Departure / Approach with ~3,000 pre-recorded sound files, a Lua mission script built on MOOSE, F10 menus and a VoiceAttack profile. No AI at runtime. |
| Is it possible without AI? | **Yes.** Without AI, every word ATC says has to be pre-recorded, and the pilot talks through menus or a VoiceAttack phrase list (VoiceAttack uses classic Windows speech recognition, not generative AI). See §5 and §6. |
| Do we need Tacview? | **No.** The DCS mission scripting environment already knows the exact position, speed and heading of every aircraft and vehicle, AI and human, at any polling rate we want. Tacview is only useful as a debugging/replay tool. See §3. |
| What is actually hard? | Not the code. (1) **Airfield data**: DCS has no taxiway names anywhere, so names, hold-short points and procedures must be authored for each airfield. (2) **AI traffic**: DCS will not let a script choose an AI aircraft's taxi path or runway. (3) **Audio production**: hundreds to thousands of clips. |
| Risk | Eagle Dynamics' 2026 roadmap lists a "complete overhaul of ATC and radio communications" (no date). Keep our data (taxi graphs, phraseology, clips) engine-independent so the work survives either way. |

---

## Decisions so far (2026-10-18)

| Topic | Decision | Consequence |
|---|---|---|
| Where it runs | **Own DCS server with friends** ("DiGiis Server", private) | The ATC app runs on the machine that hosts the server. Friends need only SRS. Today that is your gaming PC: DCS client + DCS server + SRS server + ATC app on one machine (Ryzen 5 5600X 6C/12T, 32 GB, Radeon RX 9060 XT, VR via Virtual Desktop). |
| "No AI" rule | **No AI at runtime for ATC logic and ATC voice.** Clips may be generated once, offline, with any TTS. | ATC speech = stitched sound files. No TTS or LLM while flying, except the speech-recognition exception below. |
| Pilot input | **F10 menu + real voice over SRS** | F10 is the guaranteed path. Voice = pilots press their normal SRS PTT on the ATC frequency. |
| Speech recognition | **Local and offline**, behind a pluggable interface. Candidates: **Whisper** (whisper.cpp via Whisper.net) and **NVIDIA Parakeet TDT** (sherpa-onnx); the S5 bake-off on your recorded calls picks one. The Windows grammar engine is out (your choice, 2026-10-19). | A contained exception to the no-AI rule, for listening only. See §6. |
| Architecture | **External app + thin Lua bridge** | §10 option B. |
| Language | **C# / .NET** | SRS is C#; Whisper.net, System.Speech and NAudio are native .NET. |
| Phraseology | **ICAO base + military procedures** (overhead break, closed traffic, formation takeoff, …) | Defines the clip list. |
| First airfield | **Al Dhafra AB (OMAM)**, Persian Gulf | Big field with parallel runways and dedicated helicopter stands. Labeling is done in stages (§4). |
| SRS | **2.4.1** (client, server and ExternalAudio, all 2026-09-11) | Spike S2 targets the 2.4.1 protocol. |
| AI traffic | **ATC meters AI departures and arrivals, FLIGHTCONTROL-style, best effort** | Simple hold/release logic only. It does not have to be perfect (§9). |
| Helicopters | **In scope from M1** | Ground and air taxi, helicopter stands, departure from present position, VFR arrival/departure (§8). |
| Test setup | **Own mission at Al Dhafra, spawn in a jet** (later a helicopter and some AI) | §11. |
| ATC voice | **One voice for all positions, free.** Candidates: Kokoro, Piper, Google TTS free tier, Windows voices. You pick by ear after an audition (spike S6). | §5. |
| Altimeter | **QNH in hPa (ICAO), plus inches of mercury for US aircraft**, e.g. "QNH 1013, altimeter 29.92" | Decided per aircraft type (§8). |

---

## 1. Context: stock ATC and what ED is doing

Stock ATC has no taxi routes ("taxi to runway" only), no clearance delivery, departure or approach control, no sequencing worth the name, no runway-incursion or wrong-turn detection, and AI aircraft regularly jam taxiways and runways.

**ED's own plans (2026 roadmap):**
- "A complete overhaul of ATC and radio communications" is listed, but the roadmap says some items may land after 2026. No date.
- A new interface for all radio operators (ATC, AWACS, tanker, JTAC, own flight).
- They are testing an **AI-based voice-over system** to add voice lines quickly.
- Their built-in voice chat is getting radios that can sit on any entity (tower, truck, radar), with line-of-sight and frequency simulation, so that players can act as human ATC. A lightweight comms-only client is planned (with LotATC integration mentioned).
- Back in March 2025 ED reported an "ATC milestone" and were looking for real controllers for voice work.

**Implication:** ED may ship something in the next year or two. We do not know whether it will do proper taxi routing or approach control. Our design should keep its **airfield data and audio separate from the DCS glue code**, so that if ED's system lands we can drop the parts it covers and keep the rest.

---

## 2. What a script can and cannot do in DCS (verified)

### Can do

| Capability | How | Notes |
|---|---|---|
| Know where everything is | `Unit:getPoint()`, `getVelocity()`, `getPosition()` in the mission environment; poll 1–10 Hz | Covers AI and every client in MP. This is the "Tacview" data we need, already inside DCS. |
| React to events | `S_EVENT_BIRTH`, `PLAYER_ENTER_UNIT`, `ENGINE_STARTUP`, `TAKEOFF`, `RUNWAY_TAKEOFF` (54), `LAND`, `RUNWAY_TOUCH` (55), `ENGINE_SHUTDOWN`, `CRASH`… | `RUNWAY_TAKEOFF` / `RUNWAY_TOUCH` are the newer, more precise wheels-off and wheels-on events. |
| Runway data | `Airbase:getRunways()`, wrapped by MOOSE as `AIRBASE.Runway` (name, true/mag heading, length, width, threshold & end coordinates, L/R) | MOOSE also has `GetRunwayIntoWind`, `SetActiveRunwayLanding/Takeoff`. |
| Parking data | `Airbase:getParking()` → spot positions, terminal type (size class), free/occupied | MOOSE notes some DCS quirks (a spot reads "not free" until the aircraft is airborne). |
| **Silence the stock ATC** | `Airbase:setRadioSilentMode(true)` (MOOSE `AIRBASE:SetRadioSilentMode`) | The stock airfield stays in the comms menu but no longer answers. |
| Pilot requests | F10 "Other" radio menu, per group (`missionCommands.addCommandForGroup`) | The menu can be rebuilt to show only what makes sense in the current phase. |
| Text / subtitles | `trigger.action.outTextForGroup/Unit` | |
| In-game audio on a radio frequency | `trigger.action.radioTransmission(file, point, mod, loop, freq, power)` | Heard on the cockpit radio if tuned. **The file must be inside the .miz.** Clips are not queued, so each clip's exact duration must be known (MOOSE `RADIOQUEUE` handles this). FC3 aircraft hear every frequency. |
| In-game audio, not on a radio | `outSound`, `outSoundForCoalition/Group/Unit` | Also limited to files inside the .miz. |
| Audio via SRS | MOOSE `MSRS` → `DCS-SR-ExternalAudio.exe` (TTS **and** playing sound files from disk), **HoundTTS** (TTS only, no popup window, local Piper voices), **DCS-gRPC** (TTS only) | SRS handles frequency, radio effect and line-of-sight. Works in SP too. |
| Control AI | Spawn, late activation, "uncontrolled" parked aircraft + start command, routes, orbit/holding tasks, land at airbase | MOOSE FLIGHTCONTROL uses exactly this to meter AI departures and arrivals. |
| Run in *every* mission without editing it | A GameGUI hook in `Saved Games\DCS\Scripts\Hooks\` loads automatically and can inject code into the mission environment | `net.dostring_in('mission', …)` must be enabled in `Saved Games\DCS\Config\autoexec.cfg` (`net.allow_dostring_in = { "mission" }`), according to the `API/Sim_ControlAPI.md` that ships with DCS. **DCS bug (2.9.18 to at least 2.9.30):** `a_do_script` passes return values back shifted (nil prepended, last value dropped). The bridge works around it; see `docs/bridge-protocol.md`. |
| Talk to an external program | `luasocket` is available in hooks and in `Export.lua` | Mission scripts cannot `require` (sanitized). The usual route is mission ↔ hook ↔ socket ↔ app. |

### Cannot do (or only with workarounds)

| Limitation | Impact | Workaround |
|---|---|---|
| **AI taxi path and runway choice are internal to DCS.** Once an AI starts taxiing or lands, "we completely lose control" (MOOSE FLIGHTCONTROL docs). The AI runway follows the wind. | We cannot route AI along our taxi graph or give it a runway other than the wind's. | Pick the active runway from the wind, as the AI does. Decide **when** AI may start or taxi (uncontrolled spawn, "parking guards"), hold AI airborne in our holding stacks, and limit how many move at once. Play AI "readbacks" over the radio to keep the frequency alive. |
| No taxiway names in DCS data or API | Cannot say "via Alpha, Bravo" without authoring | See §4. |
| A mission script cannot read which frequency the player has tuned | Realism ("you are on the wrong frequency") | SRS knows the tuned frequency. On the client, `Export.lua` can read cockpit radios, but this differs per module. |
| F10 menus are per group, not per unit | Two players in one group share a menu | Same limitation as FLIGHTCONTROL; most mission makers use single-ship client groups anyway. |
| In-game sound functions only play files inside the .miz | A "works with any mission" mod cannot use in-game radio audio | Use SRS, play locally from a companion app, or patch the files into each .miz. |

---

## 3. Position data: Tacview vs. alternatives

| Source | What it gives | Cost | Fit |
|---|---|---|---|
| **In-mission Lua** (`Unit:getPoint()` etc.) | Everything, authoritative, any rate, AI included | Free | **Best.** No extra software. |
| **Own socket bridge** (hook + luasocket → our app) | Whatever we choose to send (e.g. JSON at 2–5 Hz) | Free | Best if the "brain" lives outside DCS. |
| **DCS-gRPC** (open source, Rust) | Unit stream, events, calls into the scripting API, SRS TTS | Free | Good off-the-shelf bridge. Latest tagged release is 0.7.x; repo still gets dependency updates in 2026. |
| `Export.lua` `LoGetWorldObjects()` | All objects, client side | Free | Multiplayer servers can block it (`allow_object_export`). Not needed. |
| **Tacview real-time telemetry** (TCP 42674, ACMI text stream) | Positions and attitudes of everything | Real-time streaming is documented as a **Tacview Advanced** feature | SkyEye (GCI bot) uses it. For us it only duplicates what DCS already gives. |
| Tacview (free) recordings | `.acmi` file after each flight | Free | **Useful for debugging**: replay a session and see exactly where the taxi monitor thought you were. |

**Conclusion:** Tacview is not part of the runtime design. Use it as a debugging and replay tool.

---

## 4. Airfield and taxiway data: the real work

### What DCS ships

- `D:\DCS\DCS World\Mods\terrains\<Map>\AirfieldsTaxiways\<Airfield>.rn4 / .rn5`: the taxi network the AI uses. Airfields with this data on your install:

  | Map | Airfields |
  |---|---|
  | Caucasus | 21 |
  | Persian Gulf | 30 |
  | Marianas | 8 |
  | Marianas WWII | 11 |
  | Germany Cold War | 227 (includes small strips and air-racing courses) |

  (Kola and Sinai only have `radio.lua` here. Their terrain files are not in this folder.)

- The files are **binary and undocumented** (`t5::rn5file`; `.rn4` is an older `landscape4::lRoadNetwork` copy). **Decoded enough for routing** (`src/DcsAtc.Airfields/Rn5/Rn5Reader.cs`). All **297 airfields** on your install parse with exact edge counts (108 of the Germany CW ones are empty).
  - **Header:**
    - A list of **edge types** with widths: `taxiway_22m`, `vpp_60`/`runway_60m` (ВПП = runway).
    - A list of **junction/stand kinds**: `stand_end_helicopters`, `marking_cross`, … These are not yet linked to nodes.
  - **Edges:** `(type, nodeA, portA, nodeB, portB, 0)` followed by a polyline of `(x, 0, z)` doubles in DCS map coordinates, then spline data (skipped).
  - **Ports:** each **(node, port)** is used by exactly one edge, so a port is one arm of a junction. Junction size is the distance between its ports: usually a few tens of metres, up to ~400 m at high-speed exits.
  - **Runway names** are stored as `"<n>#<name>"`, e.g. `1#13L`, `2#13R` at Al Dhafra; not found for every airfield. DCS `getRunways()` gives them at runtime anyway.
  - **Stands:** often "through" stands, not dead ends. They will be matched to DCS parking spots by position.
  - **Overlapping edges:** junctions are not points. At Al Dhafra the parallel taxiway is a chain of *overlapping* edges, each ending up to ~250 m past the start of the next. The routing graph therefore splits edges where another arm of the junction touches them, and the router forbids U-turns. Otherwise routes run back and forth: the first version had 57 reversals and double length on one route.
  - **No taxiway names** anywhere. These are our labeling job.
  - **Spot names:** the F10 map's parking spot names (e.g. "98") are not DCS's `Term_Index`. A mapping is still needed for phraseology like "parking spot 98".
- **Official DCS aerodrome charts** *do* have the names:
  - Caucasus kneeboard: `Mods\terrains\Caucasus\Kneeboard\*_GND_*.png`, one ground chart for each of the 21 airfields.
  - `D:\DCS\DCS World\Doc\Charts\DCS_GND_Charts.pdf`.
  - Example, Kutaisi (UGKO): taxiways A, B, C, D, E, N, R, S, W; stands N22–N58 and W17–W21; holding positions P1 (C) and P2 (B) **with lat/lon coordinates**.
- Other maps: DCS models real airports, so real charts work (UAE AIP for the Persian Gulf, FAA diagrams for Guam/Andersen, etc.).
- **Caution:** in-game taxiway markings do not always match real-world charts. A 2018 Al Dhafra chart project found a taxiway letter that differed in game, and forum users report wrong labels even on ED's own Caucasus kneeboards. **The labeling tool must be checked against in-game signs and markings** (external view or a quick drive-around).

### Persian Gulf (chosen first map)

- No official ED ground charts for PG on this install.
- Community charts exist:
  - Master Arms wiki, PG chart section
  - VNSAWC kneeboard pack
  - The 2018 ED-forum chart project
- Real-world UAE AIP aerodrome charts exist for the civil airports. The military bases are not in the public AIP.
- Taxi network size, from the `.rn5` probe:

  | Airfield | `.rn5` size | Polylines | Points | Total line length | Extent |
  |---|---|---|---|---|---|
  | **Al Minhad AB** (OMDM) | 485 KB | 416 | 3,586 | 29.9 km | 0.6 × 3.9 km |
  | Al Dhafra AB (OMAM) | 2,305 KB | 2,126 | 17,309 | 122.3 km | 5.6 × 4.8 km |
  | *Kutaisi (for comparison)* | 124 KB | 127 | 864 | 19.3 km | 2.2 × 2.8 km |
  | *Batumi (for comparison)* | 35 KB | 33 | 246 | 5.5 km | 1.2 × 1.8 km |

  (The line length includes stand lead-in lines and markings, not only taxiway centerlines.)
- **Types in the PG files:**

  | Airfield | Edge types | Junction/stand kinds |
  |---|---|---|
  | Al Dhafra | `taxiway_22m` ×2098, `vpp_60` ×28 | `stand_end`, `stand_end_24m`, `stand_end_all`, `stand_40x40m_end`, `stand_52x60m_end`, `stand_through`, `stand_through_24m`, `stand_through_a10`, **`stand_end_helicopters`**, `stand_vpp_through`, `marking_*`, `end_marking` |
  | Al Minhad | `taxiway_22m`, `vpp_60` | Same set, **without** helicopter stands |

- **Decision: Al Dhafra first.**
  - It is where most PG missions fly from.
  - It has the parallel-runway case (13L/31R and 13R/31L, to be confirmed in game with `getRunways()`).
  - It is the only one of the two with dedicated **helicopter stands**.
- **Cost:** its network is ~4× Al Minhad's. To keep M1 small, label in stages:
  1. Both runways and their hold-short points.
  2. The parallel taxiways and the connectors to them.
  3. One parking area for jets and the helicopter stands.
  4. Everything else later.

  Unlabeled edges simply stay out of routing until they are labeled.
- **Taxiway names:** from the community charts (2018 ED-forum Al Dhafra chart, Master Arms, VNSAWC), checked against in-game signs, since the 2018 project already found at least one letter that differs in game.
- Al Minhad becomes the second airfield (quick win once the tooling exists).

### How AI_ATC did it (for comparison)

- Each taxiway is hand-surveyed as a polygon of world coordinates: `TAXIWAY_ALPHA = { {x=…, z=…}, … }`.
- Each parking spot has a **hard-coded route per runway**: `["01"] = { Taxi = { ["03L"] = {"Juliett","Foxtrot","Alpha"} } }`.
- This works, but it is a lot of manual work for each airfield and cannot handle anything off-script (closed taxiways, give way, alternative routes).

### Proposed approach

1. **Extract** the geometry from `.rn5` (finish decoding how the polylines connect, or snap endpoints by distance).
2. **Label** it in a small web editor: chart image + extracted geometry overlaid. Click edges to assign taxiway names; click to place hold-short points, runway entries/exits and named holding positions; map DCS parking `Term_Index` → stand names.
3. **Export** one JSON file per airfield:
   - `nodes`: stand / intersection / hold-short / runway entry
   - `edges`: taxiway name, one-way flag, max wingspan
   - `runways`: ends, hold-short nodes, exits
   - frequencies, and later SIDs, STARs and approaches as waypoint lists
4. **Route at runtime** with A*/Dijkstra:
   - Any spot to any runway gives "*Taxi to runway 25 via November, Delta, hold short runway 25*" automatically.
   - Runway crossings are detected from the graph ("*cross runway 07*").
   - The same graph powers **conformance monitoring**: wrong turn, passing a hold-short line without clearance, entering a runway, taxi speed.

Rough guess: with the tool, about 1–3 hours per airfield. Without it, much more.

---

## 5. Voice output without AI

### Options

| Option | AI involved? | Sound | Cost / effort | Notes |
|---|---|---|---|---|
| **Human-recorded clips, stitched** | None | Best voice, stitched rhythm | High: someone records hundreds or thousands of clips | AI_ATC's approach. |
| **Clips generated once, offline, with any TTS, shipped as files** | Only at authoring time; none while playing | Good, consistent | Low | MOOSE's own ATIS clips (180+) were made this way. Regenerate whenever phraseology changes. |
| Windows SAPI TTS via SRS / HoundTTS | Classic voices: no. (The newer Windows 11 "natural" voices are neural.) | Robotic | Free | MOOSE default. Handles any text, which is good as a fallback for odd words. |
| Piper via HoundTTS (local, offline) | Small local neural model | Good | Free, CPU only | No internet, no keys. |
| Cloud TTS (Google / Azure / AWS / ElevenLabs) | Yes, cloud | Best | Pay per use | You already have a Google key set up for SRS. |

### How many clips (estimate for one controller voice, v1 feature set)

| Group | Clips |
|---|---|
| Digits 0–9 / "niner", hundred, thousand, decimal, flight level, … (×2 intonations) | ~40 |
| NATO alphabet (taxiways, ATIS letters) ×2 intonations | ~52 |
| Callsigns: DCS Western names (Enfield, Springfield, Uzi, Colt, Dodge, Ford, Chevy, Pontiac + type-specific Hawg, Viper, Hornet, Dude, …) | ~70 |
| Airfield / facility names for one map ("Al Dhafra" + Ground/Tower/Approach/Departure) | ~30–60 |
| Phrases: clearance ~30, ground ~60, tower ~60, departure/approach ~80, misc ~50 | ~280 |
| **Total per voice** | **~500–700** |

Separate voices for ground, tower and approach, pilot-voice readbacks for AI traffic, and airfield-specific procedures push this towards **2,000–3,000**, which matches AI_ATC's ~3,000.

**Quality trick:** record numbers and letters in two versions, *continuing* (mid-sentence) and *final* (falling tone). Stitched sentences sound far more natural.

### How the audio reaches the pilot

1. **In-game radio** (`radioTransmission` + `RADIOQUEUE`): no extra software; files must be inside the .miz; clip durations must be known.
2. **SRS**: `ExternalAudio.exe` can play files from disk. An external app can also connect as an SRS client and stream audio (SkyEye does this). Correct frequency, radio effect and line-of-sight come for free. Works in SP and MP.
3. **Companion app plays on the PC speakers** (SP only): simplest, gapless stitching, our own radio effect. Ignores the tuned frequency unless we read it from SRS or `Export.lua`.

### Decision: one free voice, chosen by ear

The clips are generated once (and again whenever we add words), so the generator only needs to be **free and good enough**.

| Candidate | Runs | Cost | License / caveat |
|---|---|---|---|
| **Kokoro-82M** | Local (CPU OK), offline | Free | Apache-2.0 weights; ~54 voices including US and British English. Most promising free local option. |
| **Piper** | Local, offline | Free | Code is free; **each voice has its own license** (check its `MODEL_CARD`). Fine for private use. |
| **Google Cloud TTS** with your existing key | Cloud, only while generating | Free tier: reportedly ~4 M chars/month (Standard/WaveNet), ~1 M (Neural2, Chirp 3 HD). Third-party figures; check Google's pricing page. | The whole clip set is ~30–60 k characters, far inside the free tier. Set a budget alert/quota so it can never bill. |
| **Windows voices** | Local | Free | Lowest quality; useful as a placeholder. |

**Audition (spike S6):**
1. Render 8–10 typical sentences with each candidate ("*Viper 1-1, Al Dhafra Ground, taxi to holding point runway 31L via …*").
2. Stitch them from single-word clips exactly as the app will.
3. Transmit them through SRS, which adds its radio effect and changes how voices sound.
4. You pick one voice. Different positions can later get a slight pitch or speed change for free.

Local generators (Kokoro, Piper) are preferred: regenerating after adding a new airfield name costs nothing and needs no account.

---

## 6. Pilot input without AI

| Method | AI? | Pros | Cons |
|---|---|---|---|
| **F10 menu**, context-sensitive | None | Always works, SP and MP; FLIGHTCONTROL and AI_ATC both use it | Clunky; not "talking" |
| **VoiceAttack** phrase list → F10 key presses | Classic grammar-based Windows speech recognition | Feels like talking; AI_ATC ships a `.vap` profile | Fragile when menu positions change |
| **VoiceAttack → UDP/HTTP to our app** | Same | Robust: phrase maps to a command, no menu walking | Needs the companion app |
| **Speak on the real SRS frequency + speech-to-text** | Whisper (neural) or Vosk with a fixed grammar (small neural) | Most immersive: press your real PTT on the tower frequency | Recognition errors; heaviest to build. Later phase. |

You have **VAICOM PRO** installed (`Saved Games\DCS\Mods\tech\VAICOMPRO`). VAICOM is built around the stock ATC, so for our ATC we would most likely write our own VoiceAttack profile; VAICOM may need its ATC features turned off to avoid conflicts. VAICOM has a community edition on GitHub (Penecruz/VAICOMPRO-Community).

### Decision: speech recognition for "real voice over SRS"

**Choice:** a local recognizer behind a pluggable interface (`ISpeechRecognizer`), picked by the S5 bake-off between **Whisper** (whisper.cpp through the **Whisper.net** NuGet package; CPU and Vulkan backends, GGML model files) and **NVIDIA Parakeet TDT 0.6B** (through sherpa-onnx, which already makes the ATC voice). The Windows grammar engine (`System.Speech`) was dropped before the bake-off (your choice, 2026-10-19).

**Research for S5 (2026-10-19):**
- **Whisper.net 1.9.1** (June 2026): CPU and Vulkan runtimes side by side, one per process. The Vulkan runtime needs loader 1.4.321+; your AMD driver has 1.4.341, and it **works on the RX 9060 XT** (small.en 1.0–2.4 s per call with DCS running, against ~4.5 s on 4 CPU threads). Grammar-constrained decoding (`WithGrammar`) and Parakeet support exist only on Whisper.net's unreleased main branch.
- **Parakeet TDT 0.6B v2** (English, CC-BY-4.0): runs int8 on the CPU in sherpa-onnx 1.13.8. Its work grows with the length of the call; Whisper always encodes 30 s, which sets a floor under its time per call. Since February 2026 sherpa-onnx supports **hotwords** (contextual biasing) for NeMo/TDT models with beam search: "Al Dhafra" comes out right with them, "Aldafra" / "Al-Dafa" without.
- **Whisper on real ATC radio** does poorly out of the box (24–63 % word error rate in papers on ATCO2 / ATCOSIM); fine-tuned models do much better. Apache-2.0 / MIT fine-tunes exist (e.g. `jlvdoorn/whisper-small.en-atcosim`, `tclin/whisper-large-v3-turbo-atcosim-finetune`; ATCOSIM was recorded at EUROCONTROL with German, Swiss and French controllers). Tried on your calls: worse than the stock model (S5 status).
- **Prompts** can make Whisper invent text on silence: near-silent transmissions are skipped, the prompt is short, and every engine is measured with and without its vocabulary.
- **SRS adds radio effects and cockpit noise on the receiving client** (`ClientAudioProvider`, SRS source): the stations get the pilot's microphone after the pilot's own SRS AGC and noise reduction. So test calls recorded at the desk in External AWACS Mode (enabled on your server) sound to the recognizer like calls from the cockpit.

**Why not the Windows grammar engine as the default:**
- Its en-US acoustic model is weak with a **strong German accent**.
- SRS audio is low-bandwidth, Opus-compressed radio audio, which makes it worse.
- Whisper was trained on large amounts of accented, noisy English and handles both much better.

**How the "no AI" spirit is kept:**
1. **Whisper only turns speech into text.** Everything after that is deterministic code, with no LLM:
   - a phrase grammar plus fuzzy matching turns the text into an intent with slots (e.g. `REQUEST_TAXI{callsign}`);
   - the expected phrases depend on the flight's current phase (a parked jet can only request clearance, startup or taxi), which removes most ambiguity.
2. **Who is talking comes from the SRS packet** (the sender's client/unit), not from recognizing the callsign.
3. **Prompting:** each recognition gets a short vocabulary prompt (callsigns on frequency, taxiway names, runway numbers, airfield name). SkyEye uses the same technique.
4. **Fallback:** if parsing fails, ATC says "*say again*", as real ATC would. The F10 menu always works.
5. **Hardware:** everything shares one PC today:
   - **Machine:** Ryzen 5 5600X (6 cores / 12 threads), 32 GB RAM, Radeon RX 9060 XT.
   - **Load:** DCS client + DCS server + SRS server + ATC app, in **VR** (Virtual Desktop).
   - **No CUDA** (AMD GPU). The GPU option is Whisper.net's **Vulkan** runtime; reports on AMD under Windows conflict, so S5 must test it.
   - **Starting point:** CPU, quantized `base.en`, 2–4 threads, below-normal priority, run only on finished PTT transmissions (no always-on listening). This should cost the sim little: recognition is a short burst a few times a minute.
   - **Upgrades if needed:** `small.en` if accuracy is poor. Vulkan only if it does not cause VR frame-time spikes.
   - **Reference:** SkyEye reports ~3 s for `small.en` on a 4-core cloud VM. 1–3 s is about a real controller's reaction time.
   - **In VR, voice is the most comfortable input** (no menus to walk), so it is worth getting right.
6. **Measure, don't guess:** Spike S5 (§11) records your own transmissions through SRS and compares the candidates (Whisper base.en / small.en / large-v3-turbo on CPU and Vulkan, Parakeet with and without hotwords) before we commit.

---

## 7. Existing work to reuse or learn from

| Project | What it is | Use for us |
|---|---|---|
| **MOOSE `Ops.FlightControl`** (in your `E:\GitHub\MOOSE_Newest`) | Airfield ATC for AI and players. Departure/arrival queues, limits on simultaneous taxi/landing, holding stacks, parking guards, player taxi speeding, runway-destroyed handling, F10 menus by flight status, SRS TTS. Taxi clearance is only "*taxi to runway 25, hold short*"; no routes. | Reference (or base) for AI metering, holding stacks and the flight-status state machine. |
| **MOOSE `Ops.ATIS`** | ATIS from 180+ clips or SRS TTS | Use directly. |
| **MOOSE `ATC_GROUND_UNIVERSAL`** | Taxi speed / off-runway monitoring, kick | Reference. |
| **MOOSE `RADIOQUEUE` / `USERSOUND` / `MSRS` / `AIRBOSS`** | Audio plumbing. AIRBOSS = carrier ATC built on recorded clips | Reuse the audio side. |
| **AI_ATC** (Avalanche110, GitHub, **CC0**: public domain) | Nellis / Creech / Silverbow. ~21k-line Lua on MOOSE, ~3,000 clips, VoiceAttack profile, F10 menus: Clearance / Ground / Tower / Departure / Approach / Other agencies, wingman commands, SIDs (DREAM 7, FYTTR 7…), runway exit instructions | **Closest thing to the goal.** Proves the no-AI approach. CC0 lets us reuse code and possibly clips. Weak point: hand-coded routes, one map. |
| **SkyEye** (dharmab, Go, open source) | GCI/AWACS bot: Tacview telemetry in, SRS out, local Whisper speech-to-text, Piper TTS. "Production ready" | **Blueprint for an external bot** that is a real SRS radio participant. |
| **OverlordBot** (2020) | GCI bot. Its ATC branch needed hand-mapped taxi graphs; the developer asked for volunteers | Shows the same data bottleneck. Appears dormant. |
| **DCS-gRPC** | API bridge into DCS + SRS TTS | Off-the-shelf bridge option. |
| **HoundTTS** (uri_ba) | Native DLL: SRS TTS without popup; Windows, Piper, Supertonic, cloud | TTS fallback. |
| **Digital Kneeboard & Comms Script** (DCS user files, 2026) | SP immersion script: startup/taxi/departure/approach flow | Shows demand. No taxi monitoring. |

---

## 8. What "realistic" should cover (feature scope)

**v1 = one airfield (Al Dhafra), your own server, a handful of players, jets and helicopters.**

| Phase | v1 | Later |
|---|---|---|
| ATIS | Information letter, runway, wind, altimeter (MOOSE ATIS) | |
| Clearance Delivery | Request clearance → "cleared to X via [SID/radar vectors], climb and maintain…, departure frequency…, squawk…" | Real SIDs per airfield |
| Ground | Startup approval. Taxi with a **computed route**, hold short, runway crossing clearance. Monitoring: wrong turn, hold-short bust, runway incursion, speed. "Give way to…" | Pushback, progressive taxi, follow-me |
| Tower | Line up and wait, takeoff clearance with wind, pattern work (closed traffic, touch-and-go, overhead break), landing clearance, go-around, runway exit, "contact ground" | Wake turbulence spacing, intersection departures |
| Departure | Radar contact, climb, turn, frequency change | Handoff to a GCI/AWACS (SkyEye) |
| Approach | Vectors to ILS/TACAN/visual, descents, sequencing, holding, "cleared ILS approach", handoff to tower | STARs, PAR talk-down, holding stacks with AI |
| Traffic | Traffic advisories; best-effort AI departure/arrival metering (§9) | AI pilot readbacks on frequency |
| Helicopters | Ground taxi / air taxi to a departure point, "cleared for takeoff from present position", VFR departure and arrival via reporting points, landing at helicopter stands | Helicopter routes / lanes, FARPs |

### Helicopters

**Detection:** unit category `HELICOPTER` in `getDesc()`.

**Ground and air taxi:**
- Ground taxi uses the same graph as jets.
- Air taxi follows the same edges and is monitored for height (≲ 100 ft AGL) and speed.

**Al Dhafra data needs:**
- The `stand_end_helicopters` stands.
- One or two helicopter departure/arrival points.
- A handful of **VFR reporting points**. PG has none in DCS, so we define plausible ones, e.g. named landmarks.

**AI_ATC as a reference:** it already does helicopter taxi and helicopter VFR departures at Nellis (Gass Peak, Dry Lake, …).

**Clips:** adds roughly 40–60 clips: "air taxi", "hover taxi", "present position", "report", "helicopter stand", reporting-point names, …

### Altimeter settings

**Values:**
- QNH (hPa) is derived from DCS's atmosphere at the field. `atmosphere.getTemperatureAndPressure(point)` gives station pressure, reduced to sea level. MOOSE ATIS already implements this, so we port its math.
- inHg is a unit conversion of the same value.

**Who gets inHg:** decided by **aircraft type**, not country. Coalitions and countries in Liberation missions are arbitrary.
- A config list of US types gets "QNH 1013, **altimeter 29.92**": F-16C, F/A-18C, F-15C/E, A-10C, F-14, AV-8B, F-4E, F-5E, AH-64D, UH-1H, OH-58D, CH-47F, C-130J, …
- Everyone else gets hPa only.

Phraseology references: ICAO Doc 4444 / Doc 9432 (civil), FAA JO 7110.65 (US). Military style (USAF/USN/NATO) differs, so this choice affects the clip set.

---

## 9. Hard problems and mitigations

1. **AI traffic can't be steered on the ground.** Mitigation: control *when* AI moves (uncontrolled spawn + start, parking guards); pick the active runway from the wind, as the AI does; hold arriving AI in our own holding stacks; cap how many move at once (the FLIGHTCONTROL pattern). Accept that AI taxi routes won't match what ATC "said" to them.
2. **Taxiway data for each airfield.** Mitigation: `.rn5` extraction + labeling tool (§4). Start with Caucasus, where official charts exist for all 21 airfields.
3. **Audio volume.** Mitigation: generate clips once offline (no runtime AI), keep phraseology small and table-driven, and fall back to SAPI TTS for rare words.
4. **Stock ATC still in the comms menu.** Mitigation: `setRadioSilentMode(true)` on all airbases we manage.
5. **"Works in every mission" vs. "sound files must be inside the .miz".** Mitigation: deliver audio via SRS or a companion app, or write a small tool that patches script + clips into any .miz (useful for Liberation/Retribution campaigns, which you also have installed).
6. **Unknown tuned frequency.** Mitigation: SRS (it knows), or treat the controller you pick in the menu as "your frequency".
7. **ED ships its own ATC.** Mitigation: engine-independent data and audio, thin DCS glue.

### AI traffic plan (best effort, FLIGHTCONTROL-style)

The goal is only that AI doesn't make a mess of the players' traffic. It does not have to be perfect. These are the same tools MOOSE FLIGHTCONTROL uses, re-implemented as a few bridge commands.

**Departures:**
- **AI set "Uncontrolled" in the mission editor** sits parked. When its turn comes, the app sends `start(group)` (the DCS `Start` command; MOOSE `StartUncontrolled`).
- **AI spawned hot** gets a **parking guard**: a static or soldier placed a few metres in front of the nose, removed when released. FLIGHTCONTROL computes the spot from the aircraft size, with a special offset for shelters; we copy that.
- **Release rules:**
  - at most *N* AI groups taxiing at once;
  - no AI release while a player is cleared onto or across the runway;
  - players are not blocked by AI limits.

**Arrivals:**
- When AI enters the approach zone, the app pushes an **orbit task** at a holding fix and altitude (one altitude per aircraft: a holding stack).
- When it's that aircraft's turn, it restores a **land-at-Al-Dhafra** task (MOOSE `FLIGHTGROUP:ClearToLand`).

**What we accept:**
- AI picks its own taxi path and runway (by wind). ATC therefore picks the active runway from the wind too, so humans and AI agree.
- AI is not voiced in v1. Players get traffic advisories about it instead.

**Bridge commands needed:**
- `ai_start`
- `guard_spawn` / `guard_remove`
- `ai_hold(fix, alt, speed)`
- `ai_land(airbase)`
- `set_radio_silent(airbase)`

---

## 10. Architecture options

### A. Pure in-mission Lua (MOOSE-based, like AI_ATC / FLIGHTCONTROL)

```
 DCS mission (.miz) ─ MOOSE + our ATC Lua + taxi-graph data + sound clips
        │ F10 menu (optionally driven by VoiceAttack key presses)
        └─ radioTransmission / RADIOQUEUE  (or MSRS → SRS)
```
- ✅ Nothing to install for other players (in-game radio). Works on any MP server running the mission. Proven by AI_ATC.
- ❌ Lua-only development: slow edit/test loop (restart the mission), no real unit tests. Sound files bloat every .miz. Each mission must include the script, or be patched by a tool.

### B. External "ATC brain" + thin DCS bridge (like SkyEye)

```
 DCS ── hook (luasocket, 127.0.0.1) ──►  ATC app (C# / Python / Go)
   ▲   positions, events, menu picks      - state machines per flight
   │                                       - taxi graph + A* routing, conformance
   └── commands: menus, AI tasks, text     - phraseology → clip sequence
                                             │
 Pilot ◄── SRS (freq, radio FX, LOS) ◄───────┘   (or local speakers in SP)
 Pilot ──► F10 / VoiceAttack (UDP) / SRS voice ──► ATC app
```
- ✅ Real language, debugger, unit tests, replay from recorded sessions. Gapless audio stitching. Works with **any mission**, including Liberation/Retribution, with no .miz edits. Voice input fits naturally later.
- ❌ The app must run on the machine that hosts the mission (your PC in SP, the server in MP). Other players need only SRS. More moving parts.

### C. Hybrid

Brain as in B. The mission-side bridge script is also usable as a normal mission script for servers that don't allow hooks. Audio via SRS by default, in-game radio as a fallback.

### Decision: B, in C# / .NET

```
 DCS server ─┬─ Hooks\atc-bridge.lua  (luasocket, 127.0.0.1 only)
             │     └─ injects mission-side bridge: unit/event stream, F10 menus, AI commands,
             │        setRadioSilentMode on managed airbases
             │
             └──► ATC app (.NET, C#) on the same machine
                    ├─ Bridge client     (JSON over TCP)
                    ├─ World model       (flights, positions, phases)
                    ├─ Airfield data     (JSON graph per airfield, A* routing, conformance)
                    ├─ Controllers       (Clearance / Ground / Tower / Departure / Approach state machines)
                    ├─ Phraseology       (intent → word sequence → clip list)
                    ├─ Audio             (clip stitching → Opus → SRS)
                    ├─ SRS client        (connects to the SRS server like a player; TX + RX on ATC frequencies)
                    └─ Recognizer        (Whisper.net or Parakeet/sherpa-onnx → text → deterministic intent parser)

 Friends: DCS + SRS only. F10 menu or real PTT on the ATC frequency.
```

**Main libraries:**
- .NET 10 (LTS)
- Whisper.net + Whisper.net.Runtime (+ .Vulkan); sherpa-onnx (Parakeet, and the TTS)
- System.Speech (Windows voices)
- NAudio (resampling, mixing)
- Opus: Concentus (pure C#) or the `opus.dll` that SRS ships

**SRS protocol:**
- Reference implementations: SRS's own C# source (ciribob/DCS-SimpleRadioStandalone) and SkyEye's headless Go client.
- The protocol version must match the SRS server, so check it in spike S2.

**Lua side:** plain DCS API, no MOOSE dependency in the bridge. We read MOOSE (FLIGHTCONTROL, AIRBASE, RADIOQUEUE) and AI_ATC for proven algorithms and port what we need.

**Mission source:** the hook injects the bridge into every mission the server runs (your own missions and Liberation/Retribution campaigns), with no .miz editing. The server's `autoexec.cfg` needs `net.allow_dostring_in = { "mission" }`. A "DO SCRIPT FILE" version of the bridge stays possible for servers without hooks.

---

## 11. Suggested roadmap

**Test setup:**

**Mission:** your own test mission at Al Dhafra.
- Start: one client jet slot.
- Then: one client helicopter slot.
- Then: a few "Uncontrolled" AI flights and one inbound AI flight.

**Fast loop, single-player:**
- Hook in `C:\Users\atrix\Saved Games\DCS\Scripts\Hooks\`.
- Local SRS server.

**Real setup, your dedicated server:**
- Hook in `C:\Users\atrix\Saved Games\DCS.dcs_serverrelease\Scripts\Hooks\` (that folder is empty today).
- A new `C:\Users\atrix\Saved Games\DCS.dcs_serverrelease\Config\autoexec.cfg` containing `net.allow_dostring_in = { "mission" }` (the server has no `autoexec.cfg` yet).

**Debugging:**
- Tacview (free) records each session for replay.
- The ATC app logs its decisions with timestamps so the two can be compared.

**Spikes (prove the risky bits first):**

| # | Spike | Done when |
|---|---|---|
| S1 | **Bridge.** Server hook + injected mission script ↔ C# console app over 127.0.0.1. | All units at 2–5 Hz and all events arrive in the app; the app can add an F10 menu entry and show a text message. |
| S2 | **SRS client.** The C# app joins your SRS server as a headless client. | It transmits a stitched ~10-clip sentence on a frequency, receives a player's transmission on that frequency, and knows who sent it. Gaps and latency measured. |
| S3 | **Taxi data.** Decode the Al Dhafra `.rn5` into a graph and render it as SVG. | Overlaid on a chart; stage 1–2 labeled; A* routes from a stand to both runways. |
| S4 | **Silence stock ATC** at Al Dhafra and read the runway data. | `getRunways()` names confirmed; AI still starts, taxis, takes off and lands normally. |
| S5 | **Recognition bake-off.** Record ~50 of your own ATC calls through SRS. | Whisper `base.en` / `small.en` / large-v3-turbo (CPU, and Vulkan) vs. Parakeet compared: accuracy, latency and VR frame-time impact, while DCS is running. |
| S6 | **Voice audition.** Kokoro vs. Piper vs. Google free tier vs. Windows voice, stitched and sent through SRS. | You pick the ATC voice. |

**Status (2026-10-18):**

| Spike | Status |
|---|---|
| S1 | **Done, working in game.** Hook + mission script (Lua 5.1), C# client and test app, install script.<br>• The first in-game run hit a DCS bug: `a_do_script` loses return values. Worked around, and the fake DCS now reproduces the bug.<br>• Second run: F10 menu *Ping* and *Where am I?* work. The reported position (1.8 NM / 120° from the airfield reference point) matches parking spots 106/253. |
| S3 | **Routing done.** Reader, SVG renderer and a scan of all 297 airfields, plus:<br>• **Routing graph:** junction arms; edges split where an arm of the same junction touches them; connectors.<br>• **Router:** A* over (link, direction of travel); no U-turns, no turns sharper than 120°.<br>• **Runways:** detected and named, with L/R from the `.rn5` names.<br>• **Holding points:** automatic, 90 m from the centerline.<br>• **Departures:** full-length by default.<br>• **Al Dhafra, spot 106:** routes to 13L / 31R / 13R / 31L are 3.9 / 1.0 / 4.6 / 3.7 km, with no reversals, no runway use, and holding points within 42 m of the threshold.<br>**Next:** taxiway names (labeling tool), helicopter stands, mapping F10-map spot names to DCS parking indexes. |
| S4 | **Data dumped** (`data/dcs/Al Dhafra AFB.json`): 254 parking spots (227 medium, 22 large, 4 runway, 1 helicopter-only). DCS calls the two parallel runways "13" and "31", both with true heading 127.9°, so names come from the `.rn5` file. Stock ATC silence was sent; **still to confirm by ear in game.** |
| Labels | **Al Dhafra labelled** from the Master Arms OMAM chart (2026). The chart was georeferenced on the runway ends (4 m residuals) and laid over the DCS network; 30 taxiway paths, 27 names, plus 17 apron/parking areas and frequencies (`data/airfields/PersianGulf/Dhafra_AFB.labels.json`, method in `docs/labeling.md`).<br>• The chart's label circles sit beside their lines and parts of the chart are schematic, so names were matched **by topology**.<br>• C is not in the DCS network; N, N4 and U0 are left unnamed (ambiguous).<br>• Example clearances: spot 106 → 31R "via A, B"; → 13L "via A, F"; spot 1 → 31R "via P, Q, H, H1". |
| M1 (ground) | **First version built, tested against the fake DCS (real Al Dhafra layout); not yet run in game.** In game: F10 *ATC → Ground → Request taxi / Cancel taxi / Say again*.<br>• **Runway:** the active runway comes from the DCS wind (the parallel closer to the aircraft).<br>• **Route:** shortest taxi route to the holding point, drawn on the F10 map.<br>• **Clearance:** ICAO-style text, e.g. "Viper 1-1, Al Dhafra Ground, taxi to holding point runway 31R via A, B. Wind 310 degrees, 10 knots, QNH 1013, altimeter 29.92" (inHg for US types).<br>• **Monitoring:** off-route warnings, a 30 kt taxi speed limit, and "hold short, contact Tower" at the holding point.<br>• **Stock ATC** is silenced at labelled airfields.<br>• **Not yet:** helicopter air-taxi, startup/clearance delivery, AI, voice. |
| M1 update | Tested in game: routes "look perfect". Fixed afterwards: callsigns come from the player name before "|"; the route and map line end at the holding point 90 m from the runway (they ended on the runway at runway-end entries). |
| M2 (tower) | **Built, unit-tested (real Al Dhafra layout, simulated traffic) and flown in game in single player ("looks great").** In game: *ATC → Tower → Ready for departure / Request landing / Report final / Say again*, *ATC → Ground → Request taxi to parking*.<br>• **Runway rules:** one aircraft on a runway at a time; departures hold for traffic within 5 NM on final.<br>• **Departure:** *line up and wait* behind a departing aircraft, then *cleared for takeoff* as soon as the runway allows; *contact Approach* once clear of the field.<br>• **Climb-out** (your request after the first flight): with *Ready for departure* the pilot picks own navigation, a direction (N/E/S/W) or closed traffic; the clearance then carries the departure instructions from that and the traffic: unrestricted climb, a turn-out heading, *stay at or below 500 ft until 3 NM* under pattern traffic, *3000 ft until advised* behind a departing aircraft (released at 5 NM), closed traffic with touch-and-go. Altitude/heading busts get *check altitude / check heading*. Generic military numbers per airfield (`"departure"` in the labels file).<br>• **Menus:** each group's F10 menu shows only the calls it can make now (built from the controllers' state, rebuilt when that changes), under one submenu per airfield: the one you are at on the ground, the five nearest within 100 NM in the air, plus any working you; every call goes to the airfield picked. Arrival calls only while closing on the airfield. Each airfield also lists its frequencies, ILS and TACAN.<br>• **Arrival:** runway and *report final* on request; *cleared to land* within 6 NM; *go around* if the runway is blocked inside 1 NM; *vacate, contact Ground* after landing.<br>• **Taxi-in:** to the free spot quickest to reach, named by its apron.<br>• **Traffic:** all aircraft count, AI included; parallel runways are independent. |
| M8 (flights) | **Built and unit-tested; not yet flown in multiplayer.** Flights are declared, not automatic: *ATC → Flight → Flight of 2/3/4* makes a flight of the players sharing the declaring pilot's callsign prefix (lowest number leads; late spawns join until complete); *Single ship* leaves.<br>• **One clearance:** Ground and Tower address *"Hangman 1 flight"* and send to every member.<br>• **Lead drives:** the lead's position drives routes and runway decisions; speed warnings are per member.<br>• **No self-blocking:** members are never traffic to each other (no hold-short or go-around because of your own wingman).<br>• **Not yet:** a formation-takeoff option and per-member parking spots. |
| S2 (SRS client) | **Done against a real SRS 2.4.1 server, not yet heard in the cockpit.** Own headless client in C# (`src/DcsAtc.Srs`), protocol taken from the SRS 2.4.1 source: TCP sync, UDP ping/voice, Opus 16 kHz 40 ms frames (Concentus).<br>• **Loopback** on the server's test frequency 247.2: 45 of 45 packets back, nothing lost, the three test tones intact.<br>• **Two clients** on 250.0: everything received and the sender identified by name (the way ATC will know which pilot is talking).<br>• One SRS client per controller position ("Al Dhafra Ground", "Al Dhafra Tower"), each with a queue. |
| S6 (voice) | **Done: you picked Piper "Alan" (British male)**, now the default. Audition of 15 voices: Kokoro v1.0 (10), Piper (3), Windows (2), all local and free (`out/audition/index.html`). Clips are made once and cached; a new clip takes 0.4–0.8 s (Kokoro fp32, 4 threads; the int8 model is 2.5× slower on this CPU), 0.1–0.4 s (Piper). At mission start everything Ground and Tower say at Al Dhafra is prepared in the background; a callsign when its pilot spawns. Details: `docs/voice.md`. |
| M3 (approach) | **Built and tested with simulated pilots (real Al Dhafra layout and DCS navaids); not yet flown in game.** "Al Dhafra Approach" on 257.0.<br>• **Navaids** from the terrain's `beacons.lua` (any map): an ILS for each Al Dhafra runway end, MA TACAN 96X.<br>• **Arrivals:** ILS, TACAN, visual or overhead on request within 60 NM. Radar vectors: join, downwind (5 NM beside final), base, intercept (30°, or aimed for a straight-in), clearance within 15 NM, hand-off to Tower at 8 NM; visual after *field in sight*; overhead to the initial point, then Tower approves the break and clears on the downwind.<br>• **Spacing:** base turns wait until the traffic ahead (clients, Tower's, AI on final) is 5 NM (+1.5 NM for compression) ahead; *extend downwind*. More than three arrivals: a stack over the TACAN from 7000 ft, released in turn.<br>• **Departures:** check-in after Tower, held below descending arrivals within 10 NM, released when clear, service ends at 40 NM.<br>• **Traffic advisories** for every client (not between aircraft on the parallel finals); **check altitude / heading**.<br>• **Simulated pilots:** five arrivals (two holding) all reach final established ~8 NM out at ~2600 ft; on the same runway at least 90 s apart at 6 NM. |
| ATIS | **Built and tested (fake DCS, real SRS loop); not yet heard in game.** On the airfield's ATIS frequency (Al Dhafra 270.5), only while someone is tuned in (SRS tells every client the others' radios). Information letter, Zulu time (map time zones as in MOOSE), runway in use, expected approach (ILS / TACAN / visual from the navaids), wind, CAVOK or visibility with fog / mist / dust / rain / snow / thunderstorm, clouds from the DCS preset layers (`Config/Effects/clouds.lua`, moved with the mission's cloud base) or legacy clouds, temperature, QNH and altimeter. New letter on significant changes (runway, approach, sky, QNH, wind 30° / 5 kt) and every hour. Text for every airfield in the menu. Needs the updated bridge (`mission_weather`); with an old one, no time, visibility or clouds. Controllers do not refer to it (your choice). |
| S5 | **Bake-off done on your 51 calls** (recorded at the desk through SRS External AWACS Mode, PTT, headset mic). Understood = every `[key phrase]` came through exactly; tolerant = near-misses a parser could take ("one way three one right" for "runway three one right"; the tolerance accepted a phrase that was not said 2 times in about 4,000 checks):<br>• **Whisper large-v3-turbo + prompt, GPU (Vulkan): 33/51, tolerant 41/51, 13 % WER, 0.54 s per call, 0.25 CPU-s.** The same on the CPU: 21 s per call (60 % of the CPU): unusable.<br>• Whisper small.en + prompt: 25/51 (31 tolerant), 0.38 s on the GPU, 3.7 s on the CPU. Parakeet: 20/51 (26), 0.33 s, CPU. Whisper base.en: 14/51. Without the vocabulary prompt every Whisper is clearly worse.<br>• **ATC fine-tune tried, set aside:** large-v3-turbo fine-tuned on ATCO2 + ATCOSIM (tclin, MIT; converted with `tools/Convert-WhisperModel.ps1`) understood only 26-27/51 (25 % WER): it turns words into European airline callsigns ("Alitalia", "Sabena", "Hapag Lloyd", "England" for "Hangman").<br>• **What goes wrong is your pronunciation, consistently, in every model:** "runway" → "one way", "right" → "white", "cleared" → "hit / we had / did", "field in sight" → "healed inside", "Tower" → "Power", "heading" → "adding". Not clipping: every recording starts with 0.6-1.5 s of near-silence after the key press. The SRS mic noise reduction (background around -80 dB) may soften /h t k f/: A/B test pending.<br>**Decided: `whisper-turbo-vulkan`.** Flown with `--transcribe`: GPU usage spikes, no noticeable frame drops, 0.8–1.4 s per call in flight. |
| Voice requests | **Built, tested end to end (fake DCS, isolated SRS, a TTS pilot); not yet flown.** With `--transcribe`, ATC answers what pilots say on its frequencies like the F10 entries (docs/voice.md, Voice requests): the call is read in plain code (normalized, your mishearings corrected by `"aliases"`, near-misses accepted, "cleared …" always a readback), the sender comes from SRS, the station from the frequency. A request for another controller gets "contact Approach on 257.0"; a call to ATC not understood "say again"; readbacks and chatter nothing (your choices). Defaults: departure own navigation, approach ILS / TACAN / visual. Radio check and start up are answered. **On the 51 recorded calls ATC would react right to 50** (29 of 30 requests). Next: readback checking (M6); later a LoRA fine-tune on your own recordings, run overnight on your PC. |

**C# tests:** bridge client over TCP, integration with the fake DCS, `.rn5` reader (synthetic file, plus Batumi / Al Dhafra / Guam from your install). All passing.

**Milestones:**

| # | Milestone |
|---|---|
| M1 | Ground at Al Dhafra (stage-1–3 labeled area): clearance, startup, computed taxi route, hold short, runway crossing, conformance monitoring. Jets **and helicopters** (ground and air taxi). Altimeter in hPa, plus inHg for US types. F10 + voice. |
| M2 | Tower: line up / takeoff, pattern, landing, go-around, exit, taxi to parking. |
| M3 | Departure and approach: radar contact, vectors, ILS/TACAN, sequencing, holding, handoffs. |
| M4 | AI traffic, best effort (§9): uncontrolled start / parking guards, holding stacks, release-to-land, traffic advisories. |
| M5 | Scale: rest of Al Dhafra labeled; Al Minhad; then the remaining PG airfields. |
| M6 | Voice polish: readback checking, "say again" handling, multi-voice controllers, AI-pilot readbacks. |
| M7 | **Multiplayer traffic on the ground** (your request): predict conflicts between cleared routes and moving aircraft, e.g. head-on on one taxiway, crossing at a junction, closing from behind. Then instruct: *hold position*, *give way to …*, *follow …*, or a hold short at the junction before the conflict. |
| M8 | **Flights of 2–4** (your request): one clearance and route for the flight, addressed to the lead ("Hangman 1 flight"), formation takeoff, one landing sequence. Flight membership from the DCS group and/or a shared callsign prefix (Hangman 1-1, 1-2, …). |

---

## 12. Open questions

All planning questions are answered (see "Decisions so far"). Items that will be settled **by the spikes** rather than by discussion:

- **ATC voice:** picked by you after the S6 audition.
- **Speech recognition backend and model size:** from S5 measurements (accuracy with your accent, latency, VR impact).
- **Al Dhafra runway names, taxiway letters and helicopter stands:** confirmed in game (S3/S4) against the community charts.
- **SRS 2.4.1 headless-client protocol details:** S2.

---

## 13. Sources

**Local (verified on this machine):**
- `E:\GitHub\MOOSE_Newest\Moose Development\Moose\Ops\FlightControl.lua` — features and limitations quoted in §2, §7 and §9.
- MOOSE `Ops/ATIS.lua`, `Sound/SRS.lua`, `Sound/RadioQueue.lua`, `Sound/SoundOutput.lua`, `Wrapper/Airbase.lua`, `Core/Event.lua`, `DCS.lua`.
- `D:\DCS\DCS World\API\Sim_ControlAPI.md` — hooks, `net.dostring_in`, Export API restrictions.
- `D:\DCS\DCS World\Mods\terrains\*\AirfieldsTaxiways\*.rn4/.rn5`, `Caucasus\Kneeboard\*_GND_*.png`, `Doc\Charts\DCS_GND_Charts.pdf`.
- AI_ATC repo (shallow clone, Lua and docs only): `ATC_Data_Nellis.lua`, `AI_ATC_Nellis_v2.9.27.lua`, `AI_ATC Menu Structure.txt`.

**Web:**
- [Threshold — ED roadmap 2026 and beyond](https://www.thresholdx.net/news/dcs26r)
- [Stormbirds — ED 2026 roadmap](https://stormbirds.blog/2026/01/02/eagle-dynamics-provides-2026-roadmap/)
- [Stormbirds — ED ATC milestone, Mar 2025](https://stormbirds.blog/2025/03/24/eagle-dynamics-reports-atc-milestone-looking-for-real-world-controllers-to-assist/)
- [Stormbirds — ED built-in voice chat radios, Mar 2026](https://stormbirds.blog/2026/03/28/eagle-dynamics-expands-what-is-possible-with-built-in-voice-chat/)
- [AI_ATC Nellis AFB — GitHub](https://github.com/Avalanche110/AI_ATC_Nellis_AFB) · [ED forum thread](https://forum.dcs.world/topic/355600-ai_atc-nellis-afb/) · [DCS user files](https://files.digitalcombatsimulator.com/en/files/3344501/)
- [SkyEye — GitHub / docs](https://github.com/dharmab/skyeye/blob/main/docs/ADMIN.md) · [ED forum](https://forum.dcs.world/topic/345389-skyeye-ai-powered-gci-bot-released)
- [OverlordBot ATC script request (2020)](https://forum.dcs.world/topic/240174-overlordbot-script-request/)
- [DCS-gRPC releases](https://github.com/DCS-gRPC/rust-server/releases)
- [HoundTTS — ED forum](https://forum.dcs.world/topic/387654-houndtts-a-tts-via-srs-solution-for-dcs/)
- [Tacview real-time telemetry wiki](https://tacview.fandom.com/wiki/Real-Time_Telemetry)
- [DCS Digital Kneeboard and Comms Script](https://files.digitalcombatsimulator.com/fr/files/3350758/)
- [VAICOM community edition thread](https://forum.dcs.world/topic/316126-vaicom-281x-installer/)
- [Hoggit wiki — Airbase class](https://wiki.hoggitworld.com/view/DCS_Class_Airbase)
- [ED forum — PG aerodrome chart project (Al Dhafra, in-game vs. real taxiway letters)](https://forum.dcs.world/showthread.php?p=3606358)
- [ED forum — Caucasus kneeboard information is wrong](https://forum.dcs.world/topic/223859-caucasus-kneeboard-information-is-wrong)
- [Master Arms wiki — Charts (Persian Gulf)](https://wiki.masterarms.se/index.php/Charts)
- [VNSAWC Kneeboards (DCS user files)](https://www.digitalcombatsimulator.com/fr/files/3334727)
- [Whisper.net — NuGet](https://www.nuget.org/packages/Whisper.net) · [GitHub](https://github.com/sandrohanea/whisper.net) · [Vulkan runtime — NuGet](https://www.nuget.org/packages/whisper.net.runtime.vulkan)
- [sherpa-onnx NeMo transducer models (Parakeet)](https://k2-fsa.github.io/sherpa/onnx/pretrained_models/offline-transducer/nemo-transducer-models.html) · [hotwords for NeMo/TDT transducers (PR #3077)](https://github.com/k2-fsa/sherpa-onnx/pull/3077) · [NVIDIA Parakeet TDT 0.6B v2](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2)
- [Adapting ASR for accented ATC communications (arXiv 2502.20311)](https://arxiv.org/pdf/2502.20311) · [Whisper fine-tuned on ATCO2/ATCOSIM (jlvdoorn)](https://huggingface.co/jlvdoorn/whisper-small.en-atcosim) · [Investigation of Whisper hallucinations (arXiv 2501.11378)](https://arxiv.org/html/2501.11378v1)
- [SRS receive-side effects: `ClientAudioProvider.cs`](https://github.com/ciribob/DCS-SimpleRadioStandalone/blob/master/Common/Audio/Providers/ClientAudioProvider.cs)
- [Phoronix — whisper.cpp 1.8.3 Vulkan speed-up](https://www.phoronix.com/news/Whisper-cpp-1.8.3-12x-Perf) · [Whisper.net setup guide (AMD caveat)](https://en.pyvideotrans.com/whisper_net_setup)
- [Costbench — Google Cloud TTS free plan 2026](https://costbench.com/software/ai-voice-tools/google-cloud-text-to-speech/free-plan/) (third-party; verify on Google's pricing page)
- [Kokoro TTS guide](https://soniqo.audio/guides/kokoro) · [Kokoro-82M](https://together.ai/models/kokoro-82m)
- [Piper (voice licenses in each MODEL_CARD)](https://github.com/AJ92/piper)

**Local, added in round 2:** CPU/GPU/RAM via WMI; SRS file versions (2.4.1.0); `Saved Games\DCS.dcs_serverrelease` (logs, `serverSettings.lua`, no `autoexec.cfg`, empty Hooks); segment-type strings in `Dhafra_AFB.rn5` / `Minhad_AFB.rn5`; MOOSE `FlightControl.lua` (`OnEventBirth`, `_LandAI`, `_SpawnParkingGuard`) and `FlightGroup.lua` (`StartUncontrolled`, `ClearToLand`).
