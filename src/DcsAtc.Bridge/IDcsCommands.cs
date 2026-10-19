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
    /// <summary>The mission weather and clock (bridge protocol 1 with mission_weather; older bridges answer with an error).</summary>
    Task<MissionWeather?> GetMissionWeatherAsync(CancellationToken ct = default);
    Task<bool> SetRadioSilentAsync(string name, bool silent, CancellationToken ct = default);
    Task DrawRouteAsync(string key, int coalition, IEnumerable<(double X, double Z)> points, double[] rgba, string? text = null, CancellationToken ct = default);
    Task ClearDrawingAsync(string key, CancellationToken ct = default);
}
