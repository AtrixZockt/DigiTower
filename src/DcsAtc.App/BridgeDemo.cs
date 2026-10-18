using System.Text.Json;
using DcsAtc.Bridge;

namespace DcsAtc.App;

/// <summary>
/// Spike S1: shows that the bridge works end to end. Prints traffic and events, gives every player
/// group an F10 test menu, answers it with text messages, and optionally dumps airbase data from DCS
/// and silences the stock ATC.
/// </summary>
public sealed class BridgeDemo(BridgeClient client, DemoOptions options)
{
    private const string MenuRoot = "ATC (test)";

    private readonly Dictionary<string, UnitState> _units = [];
    private readonly HashSet<int> _menuGroups = [];
    private IReadOnlyList<AirbaseSummary> _airbases = [];
    private DateTime _lastStatus = DateTime.MinValue;

    public async Task RunAsync(CancellationToken ct)
    {
        await foreach (var message in client.Messages.ReadAllAsync(ct))
        {
            try
            {
                await HandleAsync(message, ct);
            }
            catch (Exception ex) when (ex is BridgeCommandException or InvalidOperationException or IOException or TimeoutException)
            {
                Log($"command failed: {ex.Message}");
            }
        }
    }

    private async Task HandleAsync(BridgeMessage message, CancellationToken ct)
    {
        switch (message)
        {
            case ConnectionChangedMessage { Connected: true }:
                Log("connected to DCS");
                break;
            case ConnectionChangedMessage c:
                Log($"disconnected from DCS ({c.Reason})");
                ResetMission();
                break;
            case HelloMessage h:
                Log($"bridge protocol {h.Protocol}, mission '{h.Mission}', running: {h.MissionRunning}, multiplayer: {h.Multiplayer}");
                if (h.MissionRunning) await OnMissionRunningAsync(h.Info, ct);
                break;
            case MissionStartedMessage m:
                Log($"mission started: '{m.Mission}'");
                await OnMissionRunningAsync(m.Info, ct);
                break;
            case MissionStoppedMessage:
                Log("mission stopped");
                ResetMission();
                break;
            case UnitsMessage u:
                await OnUnitsAsync(u, ct);
                break;
            case DcsEventMessage e:
                Log($"event {e.Event,-16} {e.Unit} ({e.UnitType}){(e.Player is null ? "" : $" player {e.Player}")}{(e.Place is null ? "" : $" at {e.Place}")}");
                break;
            case MenuSelectedMessage m:
                await OnMenuAsync(m, ct);
                break;
            case BridgeErrorMessage e:
                Log($"bridge error: {e.Error}");
                break;
        }
    }

    private async Task OnMissionRunningAsync(MissionInfo? info, CancellationToken ct)
    {
        ResetMission();
        Log($"theatre {info?.Theatre ?? "?"}, mission date {info?.Date?.Year}-{info?.Date?.Month:00}-{info?.Date?.Day:00}");
        var pong = await client.PingAsync(ct);
        Log($"ping ok, mission time {pong?.GetProperty("t").GetDouble():F1} s");
        _airbases = await client.GetAirbasesAsync(ct);
        Log($"{_airbases.Count} airbases in this mission");

        foreach (var name in options.DumpAirbases)
        {
            var info2 = await client.GetAirbaseAsync(name, ct);
            if (info2 is null) continue;
            Directory.CreateDirectory(options.DumpDirectory);
            string path = Path.Combine(options.DumpDirectory, $"{name}.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(info2, new JsonSerializerOptions { WriteIndented = true }), ct);
            Log($"{name}: {info2.Runways.Count} runways ({string.Join(", ", info2.Runways.Select(r => $"{r.Name} {r.TrueHeading:F0}°T {r.Length:F0} m"))}), {info2.Parking.Count} parking spots -> {path}");
        }

        foreach (var name in options.SilenceAirbases)
        {
            bool silent = await client.SetRadioSilentAsync(name, true, ct);
            Log($"{name}: stock ATC silenced: {silent}");
        }
    }

    private async Task OnUnitsAsync(UnitsMessage snapshot, CancellationToken ct)
    {
        _units.Clear();
        foreach (var u in snapshot.Units) _units[u.Name] = u;

        foreach (var u in snapshot.Units.Where(u => u.IsPlayer && u.GroupId is not null))
        {
            int groupId = u.GroupId!.Value;
            if (!_menuGroups.Add(groupId)) continue;
            await client.AddMenuAsync(groupId, [MenuRoot], "Ping", "ping", ct);
            await client.AddMenuAsync(groupId, [MenuRoot], "Where am I?", "whereami", ct);
            Log($"added F10 test menu for {u.Player} ({u.Group})");
        }

        if (DateTime.UtcNow - _lastStatus >= options.StatusInterval)
        {
            _lastStatus = DateTime.UtcNow;
            PrintStatus(snapshot);
        }
    }

    private async Task OnMenuAsync(MenuSelectedMessage m, CancellationToken ct)
    {
        var unit = _units.Values.FirstOrDefault(u => u.GroupId == m.GroupId);
        Log($"menu '{m.Id}' selected by group {m.GroupId} ({unit?.Player ?? "?"})");
        string text = m.Id switch
        {
            "ping" => "DCS-ATC: pong. The app received your menu selection.",
            "whereami" when unit is not null => DescribePosition(unit),
            _ => $"DCS-ATC: unknown menu entry '{m.Id}'.",
        };
        await client.ShowTextAsync(m.GroupId, text, 15, ct: ct);
    }

    private string DescribePosition(UnitState u)
    {
        string where = "no airbase known";
        var nearest = _airbases.Where(a => a.Category == 0).MinBy(a => Distance(u, a));
        if (nearest is not null)
        {
            double dx = u.X - nearest.X, dz = u.Z - nearest.Z;
            double bearingFromField = (Math.Atan2(dz, dx) * 180 / Math.PI + 360) % 360;
            where = $"{Distance(u, nearest) / 1852:F1} NM, bearing {bearingFromField:000}° from {nearest.Name}";
        }
        return $"DCS-ATC: {u.Callsign ?? u.Name}, {where}. Heading {u.Hdg:000}°, ground speed {u.GroundSpeed * 1.94384:F0} kt, " +
               $"{u.Agl * 3.28084:F0} ft AGL, {(u.InAir ? "airborne" : "on the ground")}.";
    }

    private static double Distance(UnitState u, AirbaseSummary a) => Math.Sqrt((u.X - a.X) * (u.X - a.X) + (u.Z - a.Z) * (u.Z - a.Z));

    private void PrintStatus(UnitsMessage snapshot)
    {
        var players = snapshot.Units.Where(u => u.IsPlayer).ToList();
        Log($"t={snapshot.Time:F0}s  {snapshot.Units.Count} aircraft ({players.Count} players)");
        foreach (var p in players)
        {
            Log($"    {p.Player,-14} {p.Type,-12} {p.Callsign,-12} hdg {p.Hdg:000}  gs {p.GroundSpeed * 1.94384,4:F0} kt  {p.Agl * 3.28084,6:F0} ft AGL  {(p.InAir ? "air" : "ground")}");
        }
    }

    private void ResetMission()
    {
        _units.Clear();
        _menuGroups.Clear();
        _airbases = [];
    }

    private static void Log(string text) => Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {text}");
}

public sealed record DemoOptions
{
    public IReadOnlyList<string> DumpAirbases { get; init; } = [];
    public IReadOnlyList<string> SilenceAirbases { get; init; } = [];
    public string DumpDirectory { get; init; } = Path.Combine("data", "dcs");
    public TimeSpan StatusInterval { get; init; } = TimeSpan.FromSeconds(5);
}
