using DcsAtc.Host;

namespace DcsAtc.Gui;

/// <summary>The settings file the control panel works with: what is saved, and saving it.</summary>
public sealed class SettingsStore
{
    public SettingsStore(string? path = null)
    {
        Path = path ?? DigiTowerSettings.DefaultPath;
        try
        {
            Current = DigiTowerSettings.Load(Path);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Current = new DigiTowerSettings();
            LoadError = ex.Message;
        }
    }

    public string Path { get; }

    /// <summary>The settings as saved (never edited in place: the Settings page edits a copy).</summary>
    public DigiTowerSettings Current { get; private set; }

    /// <summary>Why the file could not be read: the defaults are used, and the file is left alone until the Settings page saves.</summary>
    public string? LoadError { get; private set; }

    /// <summary>Saved (on the UI thread).</summary>
    public event Action? Changed;

    public void Save(DigiTowerSettings settings)
    {
        settings.Save(Path);
        Current = settings.Clone();
        LoadError = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// A change from the Log page or the Install page, saved at once with the rest as it was. While the file could not
    /// be read it is kept in memory only: saving now would replace the broken file without asking.
    /// </summary>
    public void Update(Action<DigiTowerSettings> change)
    {
        var s = Current.Clone();
        change(s);
        if (LoadError is not null)
        {
            Current = s;
            Changed?.Invoke();
            return;
        }
        Save(s);
    }
}
