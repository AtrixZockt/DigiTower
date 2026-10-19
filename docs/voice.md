# ATC voice on SRS

The controllers speak on the real radio frequencies through SRS, in one voice, without AI while flying:
transmissions are stitched from short clips that a local text-to-speech engine made once and that are kept
on disk. The text messages stay as they are (they work as subtitles).

## How it works

```
Ground / Tower ──text──► SpokenText ──clips──► AtcVoice ──16 kHz PCM──► SrsRadio station ──Opus/UDP──► SRS server ──► pilots tuned in
                 "31R"     "three one right"     cache: data/voice/cache/<voice>/   "Al Dhafra Ground" on 250.0
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
3. **Warm-up** (`Phraseology.Samples`, `SrsRadio.Prepare`). At mission start the app prepares one sample of
   every transmission Ground and Tower make at each labelled airfield, with its runways, taxiways, parking
   areas, frequencies and the day's weather; a pilot's callsign is prepared when they spawn, ahead of the
   rest. This runs one clip at a time on a low-priority thread that steps aside while a call is being
   rendered, so a call normally only stitches cached clips. A test checks that every clip of a full sortie
   is covered. The very first mission with a new voice needs about 100 clips for Al Dhafra (about 1.5 minutes
   in the background); a call made before that is done makes its missing clips first (about 0.6 s each).
4. **SRS** (`src/DcsAtc.Srs`). Every position is an SRS client of its own ("Al Dhafra Ground",
   "Al Dhafra Tower"), with the airfield's coalition, listening on its own frequency (for voice requests,
   later). A station says its calls one after another, 0.7 s apart; a call that waited longer than 60 s (a
   backlog after SRS was away) is dropped. Audio goes out as 40 ms Opus frames at the pace they play.

Frequencies come from the airfield's labels (`data/airfields/<theatre>/*.labels.json`). At Al Dhafra:
**Ground 250.0, Tower 270.0, Approach 257.0** (AM), and the **ATIS on 270.5**: a broadcast station of its own that
repeats its text, 3 s apart, while any SRS client has a radio on that frequency (SRS tells every client the others'
radios), and is silent otherwise.

## Setup

1. Download the model of the voice in use (Kokoro, about 330 MB; once):
   ```powershell
   dotnet run --project tools/DcsAtc.VoiceTool -- fetch
   ```
   `fetch --voice <id>` gets another voice's model, `fetch --all` all of them (about 600 MB). Without a model the
   app falls back to a Windows voice.
2. Run the SRS server (2.4.x) as usual. The app connects to `127.0.0.1:5002`; use `--srs host:port`
   otherwise, `--voice <id>` for another voice, `--no-voice` for text only.
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

- **For your machine:** create `data/voice/voices.local.json` (not in git, so updates never overwrite it):
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

Notes from the measurements: the int8 Kokoro model is about 2.5× **slower** than fp32 on this CPU (no
AVX-512 VNNI), so fp32 is used; more than 2–4 threads hardly helps. The Piper "joe" voice crashes
sherpa-onnx 1.13.8 and was dropped.

## Voice tool

| Command | What |
|---|---|
| `units "<text>"` | The clips a transmission is made of. |
| `say "<text>" [--voice id] [--out f.wav] [--srs --freq 250.0 --port 5002]` | Render (and transmit). |
| `audition [--voices a,b] [--out dir]` | Compare voices. |
| `fetch [--voice id \| --model name \| --all]` | Download the default voice's model (or another voice's, one model, all). |
| `listen [--freq 250.0,270.0] [--port 5002] [--seconds n]` | Listen like a pilot and record every transmission heard (`out/heard/*.wav`, with the sender). |
| `srs-test [--port 5002] [--pair]` | SRS check without DCS: transmit on a server test frequency (247.2) and record the echo, or with `--pair` transmit on 250.0 to a second client and check the sender is identified. |

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

## Not yet

- Speech recognition: pilots talking to the controllers on the frequency (spike S5); the stations already
  receive what is said on their frequency.
- Line of sight from the tower (a position for the stations), ATIS, Approach speaking, slight voice
  differences per position.
