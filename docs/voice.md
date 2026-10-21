# ATC voice on SRS

The controllers, and AI pilots in a voice of their own, speak on the real radio frequencies through SRS, all made on
your PC: transmissions are stitched from short clips that a local text-to-speech engine made once and that are kept
on disk. The text messages stay as they are (they work as subtitles).

## How it works

```
Ground / Tower ──text──► SpokenText ──clips──► AtcVoice ──16 kHz PCM──► SrsRadio station ──Opus/UDP──► SRS server ──► pilots tuned in
                 "31R"     "three one right"     cache: data/voice/cache/<voice>/   "Al Dhafra ATC" on 250.0, 126.5, 251.1
```

1. **What is said** (`src/DcsAtc.Voice/SpokenText.cs`). A controller's text becomes clips, the ICAO way:
   numbers digit by digit ("wind three one zero degrees, one zero knots"), *niner*, runway designators
   ("three one right"), frequencies with *decimal*, the altimeter setting without it, taxiways in the
   phonetic alphabet ("November four"). Words between numbers form one clip ("taxi to holding point runway");
   each number reading is a clip of its own. Commas and full stops become short pauses.
   `DcsAtc.VoiceTool units "<text>"` shows the result.
2. **Clips** (`AtcVoice`). A clip not in the cache is synthesized once, resampled to 16 kHz, trimmed of
   silence, levelled, and saved as a WAV file. The fixed phrases and the number readings come from small
   sets, so after a short warm-up everything is cached; only new callsign words ever need the TTS.
3. **Warm-up** (`Phraseology.Samples`, `SrsRadio.Prepare`). When an airfield goes on the air (a player within
   60 NM) the app prepares one sample of every transmission its controllers make, with its runways, taxiways,
   parking areas, frequencies and the day's weather; a pilot's callsign is prepared when they spawn, ahead of the
   rest. This runs one clip at a time on a low-priority thread that steps aside while a call is being
   rendered, so a call normally only stitches cached clips. A test checks that every clip of a full sortie
   is covered. The very first mission with a new voice needs about 100 clips for Al Dhafra (about 1.5 minutes
   in the background); a call made before that is done makes its missing clips first (about 0.6 s each).
4. **SRS** (`src/DcsAtc.Srs`). Every airfield's controllers are one SRS client ("Al Dhafra ATC", per
   coalition), since its positions share frequencies: it says their calls one after another, each on all
   frequencies of the position calling, when it is their turn on the frequency ([Taking turns](#taking-turns-on-the-frequency)),
   and listens on all of the airfield's frequencies (who is talking; with voice requests, what). A call that waited
   longer than 60 s (a backlog after SRS was away) is dropped. Audio goes out as 40 ms Opus frames at the pace they
   play.

**Frequencies.** DCS gives every airfield a few ATC frequencies, the ones the briefing shows; the bridge reads
them from the terrain (`airbase_radios`). Ground, Tower and Approach all work on the VHF (118-137 MHz) and UHF
(225-400 MHz) AM ones, so VHF-only and UHF-only radios both reach them; HF and the 30-40 MHz one DCS also lists are
not used. Chart data (`data/airfields/<theatre>/*.labels.json`) can give a position a frequency of its own: at Al
Dhafra **Ground 250.0, Tower 270.0, Approach 257.0**, and DCS's 126.5 and 251.1 carry all three. Handoffs name a
position's own frequency ("contact Tower on 270.0"), and none on a shared one ("contact Tower"). The **ATIS** is a
broadcast station of its own that repeats its text, 3 s apart, while any SRS client has a radio on one of its
frequencies (SRS tells every client the others' radios), and is silent otherwise. Its frequencies: the chart's
(Al Dhafra 270.5), else the ATC UHF + 10 MHz and VHF + 1 MHz (VHF − 1 MHz at the top of the band), moved on by
25 kHz while another airfield's ATC or ATIS, or a guard frequency, has it. *Frequencies* in the F10 menu lists them.

## Setup

1. Download the model of the voice in use (Kokoro, about 330 MB; once) on the control panel's *Install* page, or:
   ```powershell
   dotnet run --project tools/DcsAtc.VoiceTool -- fetch
   ```
   `fetch --voice <id>` gets another voice's model, `fetch --all` all of them (about 600 MB). Without a model
   DigiTower falls back to a Windows voice.
2. Run the SRS server (2.4.x) as usual. DigiTower connects to `127.0.0.1:5002`; set another in Settings (the
   console: `--srs host:port`), and the voice there too (`--voice <id>`, `--no-voice` for text only).
3. In the aircraft, tune a radio to the Ground, Tower, Approach or ATIS frequency.

## Voices

The default is **Kokoro: Michael** (`kokoro-am_michael`). The voices to choose from:

| Id | Voice | Engine | New clip (Ryzen 5 5600X, 4 threads) | License of the model |
|---|---|---|---|---|
| `kokoro-am_michael` (default) | Kokoro: Michael, US male | Kokoro v1.0 | 0.4–0.8 s | Apache-2.0 |
| `kokoro-am_adam`, `kokoro-am_eric`, `kokoro-am_onyx`, `kokoro-am_fenrir` | Kokoro: Adam, Eric, Onyx, Fenrir, US male | Kokoro v1.0 | 0.4–0.8 s | Apache-2.0 |
| `kokoro-af_heart`, `kokoro-af_nicole` | Kokoro: Heart, Nicole, US female | Kokoro v1.0 | 0.4–0.8 s | Apache-2.0 |
| `kokoro-bm_george`, `kokoro-bm_lewis`, `kokoro-bf_emma` | Kokoro: George, Lewis (male), Emma (female), British | Kokoro v1.0 | 0.4–0.8 s | Apache-2.0 |
| `piper-alan` | Piper: Alan, British male | Piper | 0.12 s | trained on a Mycroft dataset marked "all rights reserved": private use at your own judgement |
| `piper-ryan` | Piper: Ryan, US male | Piper | 0.3 s | CC BY-NC-SA 4.0 (non-commercial) |
| `piper-lessac` | Piper: Lessac, US female | Piper | 0.3 s | Blizzard 2013 Lessac license (research use) |
| `sapi-david`, `sapi-zira` | Windows: David (male), Zira (female), US | Windows (built in, not neural) | < 0.01 s | comes with Windows |

All of them run locally and cost nothing. The models are downloaded from the sherpa-onnx releases on GitHub; they
and the clips made with them stay on your PC (`data/voice/models/`, `data/voice/cache/`, both outside git). The
Kokoro, Piper and Windows engines are free; the table only says what the voices' own licenses allow.

### Changing the voice

- **In the control panel:** *Settings → Controller voice* (*Test* plays a sample on your speakers); a voice that is
  not downloaded yet is downloaded on the *Install* page. It is saved in the settings file, which the console uses too.
- **For your machine, without the panel:** create `data/voice/voices.local.json` (not in git, so updates never
  overwrite it); the settings file's voice, if set, comes first:
  ```json
  { "default": "piper-alan" }
  ```
  It may also set `"threads"` (CPU threads for making new clips; default 2, which leaves the CPU to DCS). Then
  `fetch` downloads that voice's model.
- **For one run:** `dotnet run --project src/DcsAtc.App -- --voice kokoro-bm_george`.
- **To hear them first:**
  ```powershell
  dotnet run --project tools/DcsAtc.VoiceTool -- fetch --all
  dotnet run --project tools/DcsAtc.VoiceTool -- audition    # writes out/audition/index.html
  ```
  The audition renders the same four transmissions in every voice, stitched exactly as the app sends them,
  "clean" and with a rough radio sound (in the game, SRS adds its own). To hear one through SRS:
  `dotnet run --project tools/DcsAtc.VoiceTool -- say "Viper 1-1, runway 31R, cleared for takeoff." --srs --freq 250.0`.

Each voice has its own clip cache, so the first mission with a new voice spends a minute or two making its clips in
the background. Speed is per voice (`"speed"` in `voices.json`, default 1.2).

Notes from the measurements: the int8 Kokoro model is about 2.5× **slower** than fp32 on a Ryzen 5 5600X (no
AVX-512 VNNI), so fp32 is used; more than 2–4 threads hardly helps. The Piper "joe" voice crashes
sherpa-onnx 1.13.8 and is not offered.

## AI pilots

AI pilots talk when DigiTower handles AI traffic (its own setting and the server's `manageAi`) and the voice is on:
their calls before the controller's answer and their readbacks after it, said as one turn on the frequency. Their
voice is a setting of its own (*Settings → AI pilot voice*, or the console's `--ai-voice <id>`; `--no-ai-voice`
silences them); by default it is the first downloaded of Kokoro Michael, Kokoro Adam, Kokoro George, Piper Ryan and
the Windows voices that is not the controllers' voice. A cockpit sound is added to it (a headset microphone's band,
300–3400 Hz, lightly driven, over a low hiss), so pilots and controllers are told apart at once; SRS adds its radio
effect to both when they are received. *Test* beside the setting plays a pilot's call with that sound.

## Taking turns on the frequency

Every airfield's controllers are one SRS client with a radio on each of its frequencies, so they hear whoever talks
there. Before saying anything they wait until the frequency has been quiet for half a second and no other airfield
of DigiTower's is on it; after a call to a player they leave up to 4 seconds for the readback (less once the player
has read back). A call that could not be said within a minute is dropped.

## Voice tool

| Command | What |
|---|---|
| `units "<text>"` | The clips a transmission is made of. |
| `say "<text>" [--voice id] [--out f.wav] [--srs --freq 250.0 --port 5002]` | Render (and transmit). |
| `audition [--voices a,b] [--out dir]` | Compare voices. |
| `fetch [--voice id \| --model name \| --all]` | Download the default voice's model (or another voice's, one model, all). |
| `listen [--freq 250.0,270.0] [--port 5002] [--seconds n]` | Listen like a pilot and record every transmission heard (`out/heard/*.wav`, with the sender). |
| `srs-test [--port 5002] [--pair]` | SRS check without DCS: transmit on a server test frequency (247.2) and record the echo, or with `--pair` transmit on 250.0 to a second client and check the sender is identified. |
| `record [--from name] [--out out/s5/calls]` | Record the speech-recognition test calls through SRS, one prompt at a time; `--synthetic [--voice id]` makes them with a TTS voice instead. |
| `transcribe <file.wav ...> \| --corpus dir [--recognizer id] [--loop]` | Speech to text with one recognizer; `--loop` repeats a corpus as a load test. |
| `bakeoff [--recognizers a,b] [--rescore] [--misses]` | Every recognizer over the recorded calls: `out/s5/report.html`. |
| `fetch --recognition` / `fetch --recognizer id` | Download the speech-recognition models (about 1.4 GB for all). |

## Speech recognition

For voice requests (below) pilots talk to the controllers on the frequency with their normal push-to-talk. Who is
talking comes from SRS (the sender's client name and unit), what they said from a speech recognizer that runs
locally, on finished transmissions only. The recognizer only turns speech into text; what the call means is decided
by plain code (`PilotCalls.cs`). The F10 menu stays.

**Recognizers** (`data/voice/recognition.json`, chosen in Settings), all local and free:

| Id | Engine | Notes |
|---|---|---|
| `parakeet`, `parakeet-plain` | NVIDIA Parakeet TDT 0.6B v2 (int8) through sherpa-onnx, CPU | Work grows with the length of the call. `parakeet` favours the vocabulary (hotwords). |
| `whisper-base`, `whisper-small`, `whisper-turbo` (+ `-plain`) | OpenAI Whisper base.en / small.en / large-v3-turbo through whisper.cpp (Whisper.net), CPU | Always encodes 30 s of audio, however short the call. Without `-plain`, a prompt carries the vocabulary. |
| `whisper-small-vulkan`, `whisper-turbo-vulkan` (default) | the same on the graphics card (Vulkan) | Faster, but shares the GPU with DCS: watch VR frame times. |

On 51 test calls recorded through SRS (a non-native English speaker, headset microphone) the best was
**`whisper-turbo-vulkan`**: 33/51 understood exactly, 41/51 with near-misses accepted, 0.54 s per call. A Whisper
fine-tuned on other ATC speech (ATCOSIM) did worse; `tools/Convert-WhisperModel.ps1` converts such fine-tunes to
whisper.cpp format.

The vocabulary (airfield names, callsign names, a few ATC terms) is in `recognition.json`. Calls are scored after
normalising: numbers digit by digit ("31R", "thirty-one right" and "three one right" are the same), whole thousands
as "three thousand", decimals without the point, taxiways phonetic (`src/DcsAtc.Recognition/Transcript.cs`).

**Testing with your own voice, through SRS** (the bake-off)

1. Download the models (once): `dotnet run --project tools/DcsAtc.VoiceTool -- fetch --recognition`
2. Start the SRS server as usual. In the SRS client, connect in **External AWACS Mode** (EAM must be on in the
   server's `server.cfg`; the password is its `EXTERNAL_AWACS_MODE_BLUE_PASSWORD`) with the microphone and
   push-to-talk you fly with, and tune a radio to **250.0 AM** (270.0 or 257.0 work too). SRS adds its radio effects and cockpit noise on
   the receiving side, so this is the same audio the controllers get from the cockpit.
3. Record the 51 test calls of `data/voice/test-calls.txt`:
   ```powershell
   dotnet run --project tools/DcsAtc.VoiceTool -- record
   ```
   It shows one call at a time: key the mic, say it as you would on the radio, release. Each call is saved with its
   level (it warns when one is clipped or too quiet). `B` records the previous call again, `S` skips, `Q` quits;
   run it again to continue where you stopped. The recordings stay on your PC (`out/s5/calls`, not in git).
4. Compare:
   ```powershell
   dotnet run --project tools/DcsAtc.VoiceTool -- bakeoff
   ```
   Every recognizer runs in a process of its own at below-normal priority, as it would next to DCS. The report
   (`out/s5/report.html`) shows per recognizer how many calls ATC would have **understood** (every key phrase,
   `[bracketed]` in the script, came through), the word error rate, the time per call, CPU and memory, and then
   every call with its audio and what each recognizer heard. `--recognizers parakeet,whisper-small` runs only
   those; run the GPU ones while DCS is not running (their speed), and again while flying (step 6).
   `record --synthetic --out out/s5/tts` makes the same calls with a TTS voice: a native-speaker baseline.
5. **In a real flight:** `dotnet run --project src/DcsAtc.App -- --transcribe` (see [Voice requests](#voice-requests)).
6. **VR frame times:** with the app transcribing, or with a steady load from
   `dotnet run --project tools/DcsAtc.VoiceTool -- transcribe --corpus out/s5/calls --recognizer <id> --loop --interval 5`,
   compare Virtual Desktop's performance overlay (or the DCS frame counter) with and without it.

## SRS protocol (2.4.1)

From the SRS source (`ciribob/DCS-SimpleRadioStandalone`, tag 2.4.1.0, `Common/Models`, `Common/Network`):

- **TCP**, newline-delimited JSON `NetworkMessage { Client, MsgType, Clients, ServerSettings, Version }`.
  The client sends `SYNC` (2) with its `SRClientBase` (GUID, name, coalition, radios); the server answers
  `SYNC` with all clients and settings and then sends `UPDATE` (0), `RADIO_UPDATE` (3),
  `CLIENT_DISCONNECT` (5), `SERVER_SETTINGS` (4). Clients from protocol version 1.9.0.0 on are accepted.
- **UDP**, same port: a 22-byte packet (the client GUID) is a ping, echoed by the server, which also learns
  the client's UDP endpoint from it, but only once the client is known from `SYNC`. Voice packets:
  `u16 length, u16 audio length, u16 frequency-part length, Opus audio, per frequency (f64 Hz, u8 modulation,
  u8 encryption), u32 unit id, u64 packet number, u8 retransmissions, 22-byte transmission GUID,
  22-byte sender GUID`.
- **Audio:** Opus, 16 kHz mono, 40 ms frames (640 samples), VoIP mode.
- **Routing:** the server forwards a packet to every client with a radio on that frequency and modulation
  (within 500 Hz), of the same coalition if coalition security is on; on the server's test frequencies it
  echoes to the sender. Without a position (lat/lng 0) a client is never cut off by distance or line of sight.

## Voice requests

With voice requests on (the control panel's *Settings → Voice requests*, or the console's `--transcribe`), pilots
talk to the controllers on their frequencies with their normal push-to-talk, and the F10 menu keeps working alongside:

```powershell
dotnet run --project src/DcsAtc.App -- --transcribe        # or --transcribe <recognizer id>; --recordings <dir>
```

An airfield's Ground, Tower and Approach go on the air as players come within 60 NM. Every transmission on their
frequencies is recognized (`whisper-turbo-vulkan` by default: about 0.5 s on the graphics card, loaded and warmed up
at start), written to the log and saved with `heard.jsonl`, one folder per day (`%LOCALAPPDATA%\DigiTower\heard`
installed, `out/heard` from the repository; friends' calls too, on your PC only).
Then ATC reacts:

- **Who** is talking comes from SRS (the player name and its aircraft), **to whom** from the frequency; the callsign
  and the airfield name in the call are not needed. On a shared frequency the request decides which position
  answers (taxi: Ground; departure, landing: Tower; approach: Approach); a call without a request goes to the
  position named, else to the one for where the aircraft is (Ground on the ground, Tower within 10 NM, Approach).
  DCS gives a few airfields the same frequency (Al Maktoum and Sharjah: 118.6): the nearest answers.
- **A call it understands** is handled exactly like the F10 entry: request taxi / taxi to parking / cancel taxi,
  ready for departure (north, east, south, west, closed traffic, own navigation), request landing, checking in
  (also "airborne" with Approach), final, initial, request approach (ILS, TACAN, visual, overhead), field in sight,
  cancel approach, say again, request startup ("start up approved", wind and QNH); plus radio check ("read you
  five"). "Request takeoff" is ready for departure; "inbound" or "request landing" to Approach is an approach
  request. Without a direction, a departure is own navigation; without a type, an approach is ILS (else TACAN, else
  visual). In the wrong state the
  controller says so, as with the menu ("you are already airborne").
- **A request for another controller** on a position's own frequency is sent there: "Hangman 1-2, Al Dhafra Ground,
  contact Approach on 257.0."
- **Not understood** but aimed at ATC (a request word or the station's name): "Hangman 1-2, Al Dhafra Ground, say
  again." **Chatter** gets no answer.
- **Readbacks** ("cleared for takeoff runway 31 right, Hangman 1-2") are checked against the newest instruction to
  you from that airfield or FARP within a minute that has something to compare: runway, heading, altitude, frequency
  and QNH (ICAO Doc 4444 §4.5.7.5). Only a **different** value is corrected, by the station that gave it:
  "Hangman 1-2, negative, I say again, runway 31R." Missing or misheard parts are let go (Whisper drops "right" now
  and then, and writes "one way" for "runway"); "31" for "31R" counts as right; a correct readback, or none at all,
  gets no answer. Checked on the readbacks of the bake-off recordings (`tests/DcsAtc.Tests/ReadbackTests.cs`).

How a call is read (`src/DcsAtc.Recognition/PilotCalls.cs`): the text is normalized, known mishearings are
corrected (`"aliases"` in `recognition.json`: "one way" → "runway", "white" → "right", …: add your own), and it is
matched against a few ways of saying each call, accepting near-misses; the longest close match wins, and "cleared …"
without a request is always a readback. On the 51 recorded calls ATC would react right to 50 (29 of the 30 requests;
the miss: "final" heard as "kind of", no answer).

## Not yet

- Line of sight from the tower (a position for the stations), slight voice differences per position.
