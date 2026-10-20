using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DcsAtc.Host;

/// <summary>
/// "Start with Windows": the control panel starts in the notification area when the user signs in (the per-user Run
/// entry, as Discord or Steam do). An installed DigiTower registers its launcher beside <c>current\</c>, which stays
/// the same through updates.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStartup(string keyPath = WindowsStartup.RunKey, string valueName = "DigiTower")
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(valueName) is string s && s.Length > 0;
        }
    }

    /// <summary>The command it starts with (null: not set).</summary>
    public string? Command
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(valueName) as string;
        }
    }

    /// <summary>Starts <paramref name="exe"/> in the notification area at sign-in; <paramref name="settings"/> when not the default file.</summary>
    public void Enable(string exe, string? settings = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(valueName, CommandFor(exe, settings));
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    public static string CommandFor(string exe, string? settings = null) =>
        settings is null ? $"\"{exe}\" --minimized" : $"\"{exe}\" --minimized --settings \"{settings}\"";

    /// <summary>
    /// The program to start: for an installed DigiTower (…\DigiTower\current\DigiTower.exe with Update.exe and a
    /// DigiTower.exe one folder up) the launcher one folder up; otherwise this program.
    /// </summary>
    public static string LauncherFor(string processPath, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        string? dir = Path.GetDirectoryName(processPath);
        if (dir is null || !Path.GetFileName(dir).Equals("current", StringComparison.OrdinalIgnoreCase)) return processPath;
        string root = Path.GetDirectoryName(dir)!;
        string launcher = Path.Combine(root, Path.GetFileName(processPath));
        return fileExists(Path.Combine(root, "Update.exe")) && fileExists(launcher) ? launcher : processPath;
    }
}
