using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DcsAtc.Host;

/// <summary>A DCS Saved Games folder: the game, the open beta, the dedicated server, or one added by hand.</summary>
public sealed record DcsInstance(string Name, string Path);

public enum BridgeState
{
    NotInstalled,
    /// <summary>The same hook and mission script as this version's.</summary>
    Installed,
    /// <summary>Different files, an old (DCS-ATC) install, or a part missing.</summary>
    UpdateAvailable,
}

/// <summary>The bridge settings of one DCS (Scripts\DigiTower\DigiTowerConfig.lua).</summary>
public sealed record BridgeConfig(bool ManageAi = true, bool MapDrawings = true, int Port = 42700);

/// <param name="Detail">Why an update is due, or that it is current.</param>
/// <param name="OtherHooks">Other hooks in Scripts\Hooks: with the bridge installed, they can run code in missions too.</param>
public sealed record BridgeStatus(BridgeState State, string Detail, BridgeConfig Config, IReadOnlyList<string> OtherHooks);

/// <summary>
/// Installs the bridge into a DCS Saved Games folder, like bridge\install.ps1: the hook and the mission script into
/// Scripts, the settings file (written once, then kept), and the autoexec.cfg block that lets the hook reach the mission.
/// An install from before the rename (DCS-ATC) is moved over.
/// </summary>
public sealed class BridgeInstaller(string scriptsDirectory)
{
    public const string HookFile = @"Hooks\DigiTowerHook.lua", MissionFile = @"DigiTower\DigiTowerMission.lua", ConfigFile = @"DigiTower\DigiTowerConfig.lua";
    private const string Begin = "-- >>> DigiTower bridge", End = "-- <<< DigiTower bridge";
    private static readonly string[] Files = [HookFile, MissionFile];
    private static readonly string[] OldFiles = [@"Hooks\DcsAtcBridgeHook.lua", @"DcsAtc\DcsAtcMission.lua"];
    private const string OldConfig = @"DcsAtc\DcsAtcConfig.lua", OldBegin = "-- >>> DCS-ATC bridge", OldEnd = "-- <<< DCS-ATC bridge";
    private static readonly UTF8Encoding NoBom = new(false);

    /// <summary>The bridge's Scripts folder that is installed (bridge\Scripts of this version).</summary>
    public string ScriptsDirectory { get; } = scriptsDirectory;

    /// <summary>bridge\Scripts next to the program or above it (the repository's); null if there is none.</summary>
    public static string? FindScripts()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "bridge", "Scripts");
                if (File.Exists(Path.Combine(candidate, HookFile))) return candidate;
            }
        }
        return null;
    }

    /// <summary>The user's Saved Games folder (where Windows has it, even when moved).</summary>
    public static string SavedGames()
    {
        if (OperatingSystem.IsWindows() && SHGetKnownFolderPath(new Guid("4C5C32FF-BB9D-43b0-B5B4-2D72E54EAAA4"), 0, IntPtr.Zero, out var p) == 0)
        {
            try
            {
                if (Marshal.PtrToStringUni(p) is { Length: > 0 } path) return path;
            }
            finally
            {
                Marshal.FreeCoTaskMem(p);
            }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games");
    }

    /// <summary>The DCS folders in <paramref name="savedGames"/> (DCS, DCS.openbeta, DCS.dcs_serverrelease, …) that DCS has used.</summary>
    public static IReadOnlyList<DcsInstance> FindInstances(string savedGames)
    {
        if (!Directory.Exists(savedGames)) return [];
        return Directory.EnumerateDirectories(savedGames, "DCS*")
            .Where(d => Directory.Exists(Path.Combine(d, "Config")) || Directory.Exists(Path.Combine(d, "Scripts")))
            .Select(d => new DcsInstance(NameOf(d), d))
            .OrderBy(i => i.Name.Contains("server", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(i => i.Name)
            .ToList();
    }

    /// <summary>"DCS server", "DCS", "DCS open beta", or the folder's name.</summary>
    public static string NameOf(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path)).ToLowerInvariant() switch
    {
        "dcs" => "DCS",
        "dcs.openbeta" => "DCS open beta",
        "dcs.dcs_serverrelease" => "DCS server",
        "dcs.server" => "DCS server",
        _ => Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
    };

    /// <summary>Whether DCS or the DCS server is running (it reads the hook and settings only when it starts).</summary>
    public static bool DcsRunning() =>
        Process.GetProcessesByName("DCS").Concat(Process.GetProcessesByName("DCS_server")).Any();

    public BridgeStatus Status(string target)
    {
        string scripts = Path.Combine(target, "Scripts");
        var config = ReadConfig(Path.Combine(scripts, ConfigFile));
        var others = OtherHooks(target);
        bool old = OldFiles.Any(f => File.Exists(Path.Combine(scripts, f)));
        bool any = Files.Any(f => File.Exists(Path.Combine(scripts, f)));
        if (!any && !old) return new(BridgeState.NotInstalled, "Not installed.", config, others);
        if (!any) return new(BridgeState.UpdateAvailable, "The old DCS-ATC bridge: update to move it over to DigiTower (your settings are kept).", config, others);

        var missing = Files.Where(f => !File.Exists(Path.Combine(scripts, f))).ToList();
        if (missing.Count > 0) return new(BridgeState.UpdateAvailable, $"Incomplete: {string.Join(", ", missing)} missing.", config, others);
        if (!HasBlock(target)) return new(BridgeState.UpdateAvailable, "Incomplete: the autoexec.cfg block is missing.", config, others);
        if (Files.Any(f => !SameFile(Path.Combine(ScriptsDirectory, f), Path.Combine(scripts, f))))
            return new(BridgeState.UpdateAvailable, "An other version than this DigiTower's: update it.", config, others);
        if (old) return new(BridgeState.UpdateAvailable, "Old DCS-ATC files are still there: update to remove them.", config, others);
        return new(BridgeState.Installed, "Up to date.", config, others);
    }

    /// <summary>Installs or updates the bridge in <paramref name="target"/>; <paramref name="config"/> sets its settings (null: keep them). Returns what was done.</summary>
    public IReadOnlyList<string> Install(string target, BridgeConfig? config = null)
    {
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException($"DCS folder not found: {target}");
        var done = new List<string>();
        string scripts = Path.Combine(target, "Scripts");
        RemoveOld(target, done);

        // The old settings file becomes the new one, with its changes.
        string configPath = Path.Combine(scripts, ConfigFile), oldConfig = Path.Combine(scripts, OldConfig);
        if (File.Exists(oldConfig) && !File.Exists(configPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.Move(oldConfig, configPath);
            done.Add($"moved the settings from {oldConfig} to {configPath}");
        }
        string oldDir = Path.GetDirectoryName(oldConfig)!;
        if (Directory.Exists(oldDir) && !Directory.EnumerateFileSystemEntries(oldDir).Any()) Directory.Delete(oldDir);

        foreach (var f in Files)
        {
            string dest = Path.Combine(scripts, f);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(Path.Combine(ScriptsDirectory, f), dest, overwrite: true);
            done.Add($"installed {dest}");
        }

        if (!File.Exists(configPath))
        {
            File.WriteAllText(configPath, Crlf(ConfigTemplate), NoBom);
            done.Add($"created the settings file {configPath}");
        }
        if (config is not null) WriteConfig(configPath, config);

        string autoexec = Path.Combine(target, "Config", "autoexec.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(autoexec)!);
        string existing = File.Exists(autoexec) ? File.ReadAllText(autoexec) : "";
        if (!existing.Contains(Begin, StringComparison.Ordinal))
        {
            if (existing.Length > 0)
            {
                File.Copy(autoexec, autoexec + ".bak-digitower", overwrite: true);
                if (!existing.EndsWith('\n')) existing += "\r\n";
            }
            File.WriteAllText(autoexec, existing + Crlf(AutoexecBlock), NoBom);
            done.Add($"added the DigiTower block to {autoexec}");
        }
        return done;
    }

    /// <summary>Removes the hook, the mission script and the autoexec.cfg block; the settings file is kept.</summary>
    public IReadOnlyList<string> Uninstall(string target)
    {
        var done = new List<string>();
        RemoveOld(target, done);
        foreach (var f in Files)
        {
            string path = Path.Combine(target, "Scripts", f);
            if (!File.Exists(path)) continue;
            File.Delete(path);
            done.Add($"removed {path}");
        }
        string autoexec = Path.Combine(target, "Config", "autoexec.cfg");
        if (File.Exists(autoexec))
        {
            string text = File.ReadAllText(autoexec), clean = RemoveBlock(text, Begin, End);
            if (clean != text)
            {
                File.WriteAllText(autoexec, clean, NoBom);
                done.Add($"removed the DigiTower block from {autoexec}");
            }
        }
        return done;
    }

    /// <summary>The settings in a DigiTowerConfig.lua; the defaults for what it does not set (or if there is none).</summary>
    public static BridgeConfig ReadConfig(string path)
    {
        if (!File.Exists(path)) return new();
        string text = File.ReadAllText(path);
        bool Flag(string name) => !Regex.IsMatch(text, $@"(?m)^\s*{name}\s*=\s*false");
        var port = Regex.Match(text, @"(?m)^\s*port\s*=\s*(\d+)");
        return new(Flag("manageAi"), Flag("mapDrawings"), port.Success ? int.Parse(port.Groups[1].Value) : 42700);
    }

    /// <summary>Sets the settings in a DigiTowerConfig.lua, keeping its comments and anything else in it.</summary>
    public static void WriteConfig(string path, BridgeConfig config)
    {
        string text = File.ReadAllText(path);
        text = Set(text, "manageAi", config.ManageAi ? "true" : "false", "true|false");
        text = Set(text, "mapDrawings", config.MapDrawings ? "true" : "false", "true|false");
        text = Set(text, "port", config.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), @"\d+");
        File.WriteAllText(path, text, NoBom);

        string Set(string text, string name, string value, string pattern)
        {
            var setting = new Regex($@"(?m)^(\s*){name}\s*=\s*({pattern})");
            if (setting.IsMatch(text)) return setting.Replace(text, $"${{1}}{name} = {value}", 1);
            var table = new Regex(@"return\s*\{");
            if (table.IsMatch(text)) return table.Replace(text, $"return {{\r\n  {name} = {value},", 1);
            throw new InvalidDataException($"cannot set {name} in {path} (it does not return a table {{ ... }}); set {name} = {value} there yourself");
        }
    }

    private static void RemoveOld(string target, List<string> done)
    {
        // The old hook goes on install and on uninstall: two hooks would both listen on the port.
        foreach (var f in OldFiles)
        {
            string path = Path.Combine(target, "Scripts", f);
            if (!File.Exists(path)) continue;
            File.Delete(path);
            done.Add($"removed the old (DCS-ATC) {path}");
        }
        string autoexec = Path.Combine(target, "Config", "autoexec.cfg");
        if (!File.Exists(autoexec)) return;
        string text = File.ReadAllText(autoexec), clean = RemoveBlock(text, OldBegin, OldEnd);
        if (clean == text) return;
        File.WriteAllText(autoexec, clean, NoBom);
        done.Add($"removed the old (DCS-ATC) block from {autoexec}");
    }

    private static bool HasBlock(string target)
    {
        string autoexec = Path.Combine(target, "Config", "autoexec.cfg");
        return File.Exists(autoexec) && File.ReadAllText(autoexec).Contains(Begin, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> OtherHooks(string target)
    {
        string hooks = Path.Combine(target, "Scripts", "Hooks");
        return !Directory.Exists(hooks) ? []
            : Directory.EnumerateFiles(hooks, "*.lua").Select(f => Path.GetFileName(f)).Where(n => n is not "DigiTowerHook.lua" and not "DcsAtcBridgeHook.lua").Order().ToList();
    }

    private static bool SameFile(string a, string b) => File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

    private static string RemoveBlock(string text, string from, string to) =>
        Regex.Replace(text, "(?ms)^" + Regex.Escape(from) + ".*?^" + Regex.Escape(to) + @"[^\r\n]*\r?\n?", "");

    private static string Crlf(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");

    private const string AutoexecBlock = """
        -- >>> DigiTower bridge (added by DigiTower; uninstall the bridge there to remove it)
        -- Lets Scripts\Hooks run code in the mission scripting environment (needed by DigiTowerHook.lua).
        net = net or {}
        net.allow_unsafe_api = net.allow_unsafe_api or {}
        table.insert(net.allow_unsafe_api, "userhooks")
        net.allow_dostring_in = net.allow_dostring_in or {}
        table.insert(net.allow_dostring_in, "mission")
        -- <<< DigiTower bridge

        """;

    private const string ConfigTemplate = """
        -- DigiTower bridge settings for this DCS. DCS reads them when it starts: restart DCS (or the server) after a change.
        -- Installing the bridge again keeps this file.
        return {
          -- true:  DigiTower holds AI departures at their parking spots and releases them in turn, players first.
          -- false: AI is left to DCS. The ATC still talks to players and counts AI as traffic.
          -- (DigiTower's Install page, or bridge\install.ps1 -NoAi / -Ai, switches this.)
          manageAi = true,

          -- true:  taxi routes and helicopter landing spots are drawn on the F10 map while they are in use. DCS shows map
          --        drawings to everyone on that side, not to one player.
          -- false: nothing is drawn.
          -- (DigiTower's Install page, or bridge\install.ps1 -NoMapDrawings / -MapDrawings, switches this.)
          mapDrawings = true,

          -- The port the DigiTower app connects to (on this PC only).
          port = 42700,
        }

        """;

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}
