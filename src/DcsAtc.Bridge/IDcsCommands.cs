namespace DcsAtc.Bridge;

/// <summary>The commands controllers send to DCS. Implemented by <see cref="BridgeClient"/>; tests use a fake.</summary>
public interface IDcsCommands
{
    Task ShowTextAsync(int groupId, string text, int seconds = 10, bool clear = false, CancellationToken ct = default);
    Task AddMenuAsync(int groupId, IReadOnlyList<string> path, string label, string id, CancellationToken ct = default);
    Task RemoveMenuAsync(int groupId, string id, CancellationToken ct = default);
    Task ClearMenusAsync(int groupId, CancellationToken ct = default);
    Task<IReadOnlyList<AirbaseSummary>> GetAirbasesAsync(CancellationToken ct = default);
    Task<AirbaseInfo?> GetAirbaseAsync(string name, CancellationToken ct = default);
    Task<AirbaseWeather?> GetAirbaseWeatherAsync(string name, CancellationToken ct = default);
    /// <summary>Every airdrome's ATC frequencies as the briefing shows them (bridges from before this command answer with an error).</summary>
    Task<IReadOnlyList<AirbaseRadios>> GetAirbaseRadiosAsync(CancellationToken ct = default);
    /// <summary>The FARPs of the mission file (bridges from before this command answer with an error).</summary>
    Task<IReadOnlyList<FarpInfo>> GetFarpsAsync(CancellationToken ct = default);
    /// <summary>The mission weather and clock (bridge protocol 1 with mission_weather; older bridges answer with an error).</summary>
    Task<MissionWeather?> GetMissionWeatherAsync(CancellationToken ct = default);
    Task<bool> SetRadioSilentAsync(string name, bool silent, CancellationToken ct = default);
    Task DrawRouteAsync(string key, int coalition, IEnumerable<(double X, double Z)> points, double[] rgba, string? text = null, CancellationToken ct = default);
    Task ClearDrawingAsync(string key, CancellationToken ct = default);
    /// <summary>Holds an AI group at its parking spots (spawned again uncontrolled); fails for groups not in the mission file.</summary>
    Task HoldGroupAsync(string group, CancellationToken ct = default);
    /// <summary>Releases a held AI group (DCS's Start command).</summary>
    Task ReleaseGroupAsync(string group, CancellationToken ct = default);
}
