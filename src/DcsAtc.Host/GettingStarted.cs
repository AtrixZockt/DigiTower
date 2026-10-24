namespace DcsAtc.Host;

/// <summary>Where a getting-started step is done in the control panel.</summary>
public enum SetupPlace
{
    Install,
    Settings,
}

/// <summary>One step of getting started: what to do, how, whether it is done; optional steps do not keep the list open.</summary>
public sealed record SetupStep(string Title, string Hint, bool Done, SetupPlace Place, bool Optional = false);

/// <summary>
/// What a new user has to do before DigiTower works (from the first-time pass): the bridge in DCS, the controllers'
/// voice, a running SRS server, and, optional, voice requests. The control panel shows these until all the essential
/// ones were done once.
/// </summary>
public static class GettingStarted
{
    /// <param name="bridgeInstalled">The bridge is in one of this PC's DCS Saved Games folders (or DCS is connected).</param>
    /// <param name="srsReachable">The SRS server answers (null: not known yet).</param>
    public static IReadOnlyList<SetupStep> Steps(DigiTowerSettings settings, bool bridgeInstalled, bool? srsReachable, ModelStore models)
    {
        var steps = new List<SetupStep>
        {
            new("Install the bridge into DCS",
                bridgeInstalled ? "Installed. DCS loads it when it starts."
                    : "On the Install page: your DCS server's Saved Games folder, or the game's for single player and hosting from the game. Then restart DCS.",
                bridgeInstalled, SetupPlace.Install),
        };
        if (settings.Voice)
        {
            var voices = models.Voices;
            var voice = voices?.Find(settings.VoiceId ?? voices.Default);
            bool voiceReady = voice is not null && voices!.IsAvailable(voice);
            steps.Add(new("Download the controllers' voice",
                voiceReady ? $"{voice!.Label}."
                    : $"On the Install page{(voice is null ? "" : $": {voice.Label}")}. Without it a Windows voice speaks.",
                voiceReady, SetupPlace.Install));
            var (host, port) = SrsProbe.Parse(settings.Srs);
            string server = $"{host}:{port}";
            steps.Add(new("Start your SRS server",
                srsReachable switch
                {
                    true => $"Found at {server}.",
                    false => $"No SRS server answers at {server}. DigiTower joins it as the controllers; it does not start it. In single player too, with your SRS client connected to it.",
                    null => $"Looking for it at {server}…",
                },
                srsReachable == true, SetupPlace.Settings));

            var recognizers = models.Recognizers;
            var recognizer = recognizers?.Find(settings.Recognizer ?? recognizers.Default);
            bool recognizerReady = recognizer is not null && recognizers!.IsAvailable(recognizer);
            bool talking = settings.VoiceRequests && recognizerReady;
            steps.Add(new("Talk to the controllers (optional)",
                talking ? $"Voice requests on, with {recognizer!.Label}."
                    : !recognizerReady ? "Download a speech recognizer on the Install page, then turn on voice requests in Settings. Without them every call is in the F10 menu."
                    : "Turn on voice requests in Settings. Without them every call is in the F10 menu.",
                talking, recognizerReady ? SetupPlace.Settings : SetupPlace.Install, Optional: true));
        }
        return steps;
    }

    /// <summary>Every essential step is done.</summary>
    public static bool Complete(IEnumerable<SetupStep> steps) => steps.Where(s => !s.Optional).All(s => s.Done);

    /// <summary>Whether the bridge is installed in one of this PC's DCS Saved Games folders (found, or added on the Install page).</summary>
    public static bool BridgeInstalled(DigiTowerSettings settings)
    {
        if (BridgeInstaller.FindScripts() is not { } scripts) return false;
        var installer = new BridgeInstaller(scripts);
        var folders = BridgeInstaller.FindInstances(BridgeInstaller.SavedGames()).Select(i => i.Path).Concat(settings.DcsFolders.Where(Directory.Exists));
        foreach (var folder in folders)
        {
            try
            {
                if (installer.Status(folder).State != BridgeState.NotInstalled) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return false;
    }
}
