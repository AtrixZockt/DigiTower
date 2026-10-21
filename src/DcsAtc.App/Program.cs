using DcsAtc.Host;

// DigiTower console: ATC for DCS World without a window (DigiTowerConsole.exe). The control panel is DigiTower.exe; both
// read the same settings file, the options below override it for this run.
//
//   dotnet run --project src/DcsAtc.App -- [options]
//       [--settings <file>]          settings file (default %APPDATA%\DigiTower\settings.json)
//       [--host 127.0.0.1] [--port 42700]   the DCS bridge
//       [--dcs "D:\DCS\DCS World"]   DCS install (for the taxi networks); default: DIGITOWER_DCS_DIR (or the older
//                                    DCSATC_DCS_DIR) or common locations
//       [--data <dir>]               folder containing airfields\<theatre>\*.labels.json and voice\; default: next to
//                                    the program or above it (the repository's data folder)
//       [--dump "Al Dhafra AFB"]...  write DCS airbase data to data\dcs\<name>.json
//       [--silence "<airbase>"]...   silence the stock ATC at more airbases
//       [--keep-stock-atc]           do not silence the stock ATC where we provide ground control
//       [--srs 127.0.0.1:5002]       SRS server the controllers speak on
//       [--voice <id>]               ATC voice from data\voice\voices.json (default: its "default", else a Windows voice)
//       [--no-voice]                 text messages only
//       [--ai-voice <id>]            AI pilots' voice (default: one that is not the controllers')
//       [--no-ai-voice]              AI pilots stay silent
//       [--no-ai]                    leave AI departures to DCS (never held)
//       [--no-drawings]              draw nothing on the F10 map (taxi routes, landing spots: DCS shows them to the whole side)
//       [--no-emergencies]           pilots cannot declare a Mayday or Pan Pan (--emergencies: they can, the default)
//       [--transcribe [<id>]]        voice requests: what pilots say to the controllers on SRS is recognized and answered like
//                                    the F10 menu, written to the console and recorded (out\heard); recognizer from
//                                    data\voice\recognition.json
//       [--recordings out\heard]     where --transcribe keeps what it heard (one folder per day)
//       [--debug]                    also debug lines (clips prepared, status every 10 seconds)

string? settingsFile = null;
for (int i = 0; i + 1 < args.Length; i++)
    if (args[i] == "--settings") settingsFile = args[i + 1];

DigiTowerSettings settings;
try
{
    settings = DigiTowerSettings.Load(settingsFile);
}
catch (InvalidDataException ex)
{
    Console.Error.WriteLine($"settings: {ex.Message}");
    return 1;
}
var dump = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--settings": Next(); break;
        case "--host": settings.BridgeHost = Next(); break;
        case "--port": settings.BridgePort = int.Parse(Next()); break;
        case "--dcs": settings.DcsDirectory = Next(); break;
        case "--data": settings.DataDirectory = Next(); break;
        case "--dump": dump.Add(Next()); break;
        case "--silence": settings.SilenceAirbases.Add(Next()); break;
        case "--keep-stock-atc": settings.KeepStockAtc = true; break;
        case "--srs": settings.Srs = Next(); break;
        case "--voice": settings.VoiceId = Next(); break;
        case "--no-voice": settings.Voice = false; break;
        case "--ai-voice": settings.AiVoiceId = Next(); break;
        case "--no-ai-voice": settings.AiVoice = false; break;
        case "--no-ai": settings.ManageAi = false; break;
        case "--no-drawings": settings.MapDrawings = false; break;
        case "--no-emergencies": settings.Emergencies = false; break;
        case "--emergencies": settings.Emergencies = true; break;
        case "--transcribe":
            settings.VoiceRequests = true;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) settings.Recognizer = args[++i];
            break;
        case "--recordings": settings.RecordingsDirectory = Next(); break;
        case "--debug": settings.Log.Debug = true; break;
        default:
            Console.Error.WriteLine($"unknown argument: {args[i]}");
            return 1;
    }
}

var log = new AtcLog();
log.Written += e =>
{
    if (!settings.Log.Shows(e)) return;
    string level = e.Level switch { LogLevel.Warning => "warning: ", LogLevel.Error => "error: ", _ => "" };
    Console.WriteLine($"{e.Time:HH:mm:ss}  {level}{e.Text}");
};

await using var host = new AtcHost(log);
using var stopped = new SemaphoreSlim(0);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopped.Release();
};
await host.StartAsync(settings, new StartOptions { DumpAirbases = dump });
if (!host.Running) return 1;
Console.WriteLine("DigiTower: running (Ctrl+C to quit)");
await stopped.WaitAsync();
await host.StopAsync();
return 0;
