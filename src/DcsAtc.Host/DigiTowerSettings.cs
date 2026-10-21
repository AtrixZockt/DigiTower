using System.Text.Json;
using System.Text.Json.Serialization;

namespace DcsAtc.Host;

/// <summary>Which log categories are shown (console and control panel) and written to the log file.</summary>
public sealed class LogSettings
{
    /// <summary>Categories not shown in the console or the control panel's log.</summary>
    public HashSet<LogCategory> Hidden { get; set; } = [];
    /// <summary>Categories not written to the log file.</summary>
    public HashSet<LogCategory> NotInFile { get; set; } = [];
    /// <summary>Also debug lines (clips prepared, status every 10 seconds).</summary>
    public bool Debug { get; set; }
    public bool WriteFile { get; set; } = true;
    /// <summary>Log files older than this many days are deleted.</summary>
    public int KeepDays { get; set; } = 14;
    /// <summary>Where the log files go; null: the default (see <see cref="DigiTowerSettings.ResolvedLogDirectory"/>).</summary>
    public string? Directory { get; set; }

    public bool Shows(LogEntry e) => !Hidden.Contains(e.Category) && (Debug || e.Level != LogLevel.Debug);
    public bool Files(LogEntry e) => WriteFile && !NotInFile.Contains(e.Category) && (Debug || e.Level != LogLevel.Debug);
}

/// <summary>
/// Everything DigiTower is set up with: one file (<see cref="DefaultPath"/>) for the control panel and the console. The
/// console's options override it for one run.
/// </summary>
public sealed class DigiTowerSettings
{
    /// <summary>The DCS install (taxi networks, beacons); null: found in the usual places.</summary>
    public string? DcsDirectory { get; set; }
    /// <summary>The folder with airfields\ and voice\; null: found next to the program or above it.</summary>
    public string? DataDirectory { get; set; }

    public string BridgeHost { get; set; } = "127.0.0.1";
    public int BridgePort { get; set; } = 42700;

    /// <summary>The SRS server the controllers speak on ("host:port").</summary>
    public string Srs { get; set; } = "127.0.0.1:5002";
    /// <summary>False: text messages only.</summary>
    public bool Voice { get; set; } = true;
    /// <summary>The controllers' voice (an id from data\voice\voices.json); null: its default.</summary>
    public string? VoiceId { get; set; }
    /// <summary>AI pilots talk on the radio (calls and readbacks), when DigiTower handles AI traffic.</summary>
    public bool AiVoice { get; set; } = true;
    /// <summary>The AI pilots' voice; null: one that is not the controllers' (<see cref="AiVoiceChoices"/>).</summary>
    public string? AiVoiceId { get; set; }

    /// <summary>The AI pilots' voice when none is set: the first of these that is downloaded and not the controllers'.</summary>
    public static readonly IReadOnlyList<string> AiVoiceChoices = ["kokoro-am_michael", "kokoro-am_adam", "kokoro-bm_george", "piper-ryan", "sapi-david", "sapi-zira"];

    /// <summary>Pilots can talk to the controllers on SRS (speech recognition).</summary>
    public bool VoiceRequests { get; set; }
    /// <summary>The recognizer (an id from data\voice\recognition.json); null: its default.</summary>
    public string? Recognizer { get; set; }
    /// <summary>Where what was heard is kept; null: the default (see <see cref="ResolvedRecordingsDirectory"/>).</summary>
    public string? RecordingsDirectory { get; set; }
    /// <summary>Wrong readbacks are corrected ("negative, I say again, runway 31R").</summary>
    public bool CheckReadbacks { get; set; } = true;

    /// <summary>Pilots may declare a Mayday or Pan Pan (voice, F10): a priority landing at the nearest airfield.</summary>
    public bool Emergencies { get; set; } = true;

    /// <summary>Hold AI departures at parking while players taxi, take off or land.</summary>
    public bool ManageAi { get; set; } = true;
    /// <summary>Taxi routes, hold lines, spots and pads on the F10 map.</summary>
    public bool MapDrawings { get; set; } = true;
    /// <summary>Keep the stock DCS ATC where DigiTower works (it is silenced by default).</summary>
    public bool KeepStockAtc { get; set; }
    /// <summary>Silence the stock ATC at these airbases too.</summary>
    public List<string> SilenceAirbases { get; set; } = [];

    /// <summary>Arrivals may ask Approach for an approach within this distance of the airfield (NM).</summary>
    public double ApproachRequestRange { get; set; } = 60;
    /// <summary>Departures get radar service up to this distance from the airfield (NM), then "radar service terminated".</summary>
    public double ApproachServiceRadius { get; set; } = 40;

    /// <summary>The control panel starts the ATC when it opens.</summary>
    public bool StartAtcOnOpen { get; set; } = true;
    /// <summary>Closing the control panel while the ATC runs keeps it running in the notification area.</summary>
    public bool MinimizeToTray { get; set; } = true;
    /// <summary>An installed control panel looks for a new release on GitHub (and offers it; nothing updates by itself).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>DCS Saved Games folders added on the Install page (a server whose folder is somewhere else).</summary>
    public List<string> DcsFolders { get; set; } = [];

    public LogSettings Log { get; set; } = new();

    // ---- where things are

    /// <summary>%APPDATA%\DigiTower\settings.json.</summary>
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DigiTower", "settings.json");

    /// <summary>%LOCALAPPDATA%\DigiTower: recordings and logs of an installed DigiTower (an update replaces the program's folder).</summary>
    public static string LocalDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DigiTower");

    public string? ResolvedDataDirectory => Given(DataDirectory) ?? FindDataDirectory();

    public string? ResolvedDcsDirectory => Given(DcsDirectory) ?? FindDcsDirectory();

    /// <summary>From the repository (a developer build): out\heard there, as before; installed: %LOCALAPPDATA%\DigiTower\heard.</summary>
    public string ResolvedRecordingsDirectory => Given(RecordingsDirectory) ?? Path.Combine(RepositoryOut() ?? LocalDirectory, "heard");

    /// <summary>From the repository: out\logs there; installed: %LOCALAPPDATA%\DigiTower\logs.</summary>
    public string ResolvedLogDirectory => Given(Log.Directory) ?? Path.Combine(RepositoryOut() ?? LocalDirectory, "logs");

    // A folder left empty in the file means the default.
    private static string? Given(string? path) => string.IsNullOrWhiteSpace(path) ? null : path;

    /// <summary>
    /// The voice and speech recognition models. From the repository: data\voice\models there, as before; installed:
    /// %LOCALAPPDATA%\DigiTower\models, so an update (which replaces the program's folder) keeps them.
    /// </summary>
    public string? ResolvedModelsDirectory => InRepository() is { } data ? Path.Combine(data, "voice", "models")
        : ResolvedDataDirectory is null ? null : Path.Combine(LocalDirectory, "models");

    /// <summary>The clips each voice has made: beside the models.</summary>
    public string? ResolvedVoiceCacheDirectory => InRepository() is { } data ? Path.Combine(data, "voice", "cache")
        : ResolvedDataDirectory is null ? null : Path.Combine(LocalDirectory, "voice-cache");

    /// <summary>The repository's out folder when the data folder is the repository's.</summary>
    private string? RepositoryOut() => InRepository() is { } data ? Path.Combine(Path.GetDirectoryName(data)!, "out") : null;

    /// <summary>The data folder when it is the repository's (DcsAtc.slnx beside it): a developer build.</summary>
    private string? InRepository()
    {
        if (ResolvedDataDirectory is not { } data) return null;
        data = Path.GetFullPath(data);
        return Path.GetDirectoryName(data) is { } root && File.Exists(Path.Combine(root, "DcsAtc.slnx")) ? data : null;
    }

    /// <summary>A data folder (with airfields\) next to the program, in the working directory, or above either.</summary>
    public static string? FindDataDirectory()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "data");
                if (Directory.Exists(Path.Combine(candidate, "airfields"))) return candidate;
            }
        }
        return null;
    }

    /// <summary>DIGITOWER_DCS_DIR (or the older DCSATC_DCS_DIR), else the usual install folders.</summary>
    public static string? FindDcsDirectory() =>
        Environment.GetEnvironmentVariable("DIGITOWER_DCS_DIR") ?? Environment.GetEnvironmentVariable("DCSATC_DCS_DIR")
        ?? new[] { @"D:\DCS\DCS World", @"C:\Program Files\Eagle Dynamics\DCS World", @"C:\Program Files\Eagle Dynamics\DCS World OpenBeta",
                   @"D:\Program Files\Eagle Dynamics\DCS World", @"C:\DCS World" }
            .FirstOrDefault(d => Directory.Exists(Path.Combine(d, "Mods", "terrains")));

    // ---- the file

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The settings in <paramref name="path"/> (default: <see cref="DefaultPath"/>); the defaults if there is no such file.</summary>
    /// <exception cref="InvalidDataException">The file is not valid settings.</exception>
    public static DigiTowerSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new DigiTowerSettings();
        try
        {
            return JsonSerializer.Deserialize<DigiTowerSettings>(File.ReadAllText(path), Json) ?? new DigiTowerSettings();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path}: {ex.Message}", ex);
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // Written next to it first, so a crash never leaves half a file.
        string temp = path + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>A deep copy (the control panel edits a copy until Save).</summary>
    public DigiTowerSettings Clone() => JsonSerializer.Deserialize<DigiTowerSettings>(JsonSerializer.Serialize(this, Json), Json)!;

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}
