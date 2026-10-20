using System.Text.Json;
using DcsAtc.Airfields;
using DcsAtc.Atc;
using DcsAtc.Bridge;

namespace DcsAtc.Tests;

/// <summary>Records what the controllers send to DCS and answers queries from canned data.</summary>
internal sealed class FakeDcs(IReadOnlyList<AirbaseSummary> airbases, AirbaseInfo? info, AirbaseWeather? weather) : IDcsCommands
{
    public List<(int Group, string Text)> Texts { get; } = [];
    public Dictionary<string, (List<(double X, double Z)> Points, string? Text)> Drawings { get; } = [];

    public Task ShowTextAsync(int groupId, string text, int seconds = 10, bool clear = false, CancellationToken ct = default)
    {
        Texts.Add((groupId, text));
        return Task.CompletedTask;
    }

    public Task AddMenuAsync(int groupId, IReadOnlyList<string> path, string label, string id, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveMenuAsync(int groupId, string id, CancellationToken ct = default) => Task.CompletedTask;
    public Task ClearMenusAsync(int groupId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<AirbaseSummary>> GetAirbasesAsync(CancellationToken ct = default) => Task.FromResult(airbases);
    public Task<AirbaseInfo?> GetAirbaseAsync(string name, CancellationToken ct = default) => Task.FromResult(info);
    public AirbaseWeather? Weather { get; set; } = weather;
    public Task<AirbaseWeather?> GetAirbaseWeatherAsync(string name, CancellationToken ct = default) => Task.FromResult(Weather);
    /// <summary>The mission's FARPs (none unless a test sets them).</summary>
    public List<FarpInfo> Farps { get; } = [];
    public Task<IReadOnlyList<FarpInfo>> GetFarpsAsync(CancellationToken ct = default) =>
        OldBridge ? throw new BridgeCommandException("unknown command: farp_list") : Task.FromResult<IReadOnlyList<FarpInfo>>(Farps);

    /// <summary>The airdromes' ATC frequencies DCS reports (none unless a test sets them).</summary>
    public List<AirbaseRadios> Radios { get; } = [];
    public Task<IReadOnlyList<AirbaseRadios>> GetAirbaseRadiosAsync(CancellationToken ct = default) =>
        OldBridge ? throw new BridgeCommandException("unknown command: airbase_radios") : Task.FromResult<IReadOnlyList<AirbaseRadios>>(Radios);
    public MissionWeather? MissionWeather { get; set; } = new() { Visibility = 80000, CloudPreset = "Preset1", CloudBase = 2520, AbsTime = 8 * 3600 };
    /// <summary>Answer mission_weather like a bridge from before it existed.</summary>
    public bool OldBridge { get; set; }
    public Task<MissionWeather?> GetMissionWeatherAsync(CancellationToken ct = default) =>
        OldBridge ? throw new BridgeCommandException("unknown command: mission_weather") : Task.FromResult(MissionWeather);
    public Task<bool> SetRadioSilentAsync(string name, bool silent, CancellationToken ct = default) => Task.FromResult(silent);

    public Task DrawRouteAsync(string key, int coalition, IEnumerable<(double X, double Z)> points, double[] rgba, string? text = null, CancellationToken ct = default)
    {
        Drawings[key] = (points.ToList(), text);
        return Task.CompletedTask;
    }

    public Task ClearDrawingAsync(string key, CancellationToken ct = default)
    {
        Drawings.Remove(key);
        return Task.CompletedTask;
    }

    /// <summary>AI groups held at their parking spots.</summary>
    public HashSet<string> Held { get; } = [];
    /// <summary>AI groups released, in order.</summary>
    public List<string> Released { get; } = [];
    /// <summary>Groups spawned by scripts: group_hold fails for them.</summary>
    public HashSet<string> NotInMission { get; } = [];

    public Task HoldGroupAsync(string group, CancellationToken ct = default)
    {
        if (OldBridge) throw new BridgeCommandException("unknown command: group_hold");
        if (NotInMission.Contains(group)) throw new BridgeCommandException($"not in the mission file: {group}");
        Held.Add(group);
        return Task.CompletedTask;
    }

    public Task ReleaseGroupAsync(string group, CancellationToken ct = default)
    {
        Held.Remove(group);
        Released.Add(group);
        return Task.CompletedTask;
    }
}

public class GroundControlTests
{
    private static (GroundControl Ground, FakeDcs Dcs, Func<DateTime, DateTime> SetTime)? Setup()
    {
        if (TestPaths.Terrains is null) return null;
        string dump = Path.Combine(TestPaths.RepoRoot, "data", "dcs", "Al Dhafra AFB.json");
        if (!File.Exists(dump)) return null;
        var info = JsonSerializer.Deserialize<AirbaseInfo>(File.ReadAllText(dump), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        // Wind from 310° at 10 knots (DCS gives the vector the wind blows towards).
        var weather = new AirbaseWeather { WindX = -5.14 * Math.Cos(310 * Math.PI / 180), WindZ = -5.14 * Math.Sin(310 * Math.PI / 180), Pressure = 101130, Elevation = 16, Temperature = 303 };
        var airbase = new AirbaseSummary { Name = "Al Dhafra AFB", Id = 4, Category = 0, Coalition = 2, X = info.X, Y = info.Y, Z = info.Z };
        var dcs = new FakeDcs([airbase], info, weather);
        var catalog = new AirfieldCatalog(dcs, Path.GetDirectoryName(Path.GetDirectoryName(TestPaths.Terrains)), Path.Combine(TestPaths.RepoRoot, "data"));
        catalog.SetMission("PersianGulf", [airbase]);
        var now = new DateTime(2026, 10, 18, 12, 0, 0);
        var ground = new GroundControl(dcs, catalog, new FlightRegistry(dcs), clock: () => now);
        return (ground, dcs, t => now = t);
    }

    private static UnitState Viper(double x, double z, double speed = 0, bool inAir = false) => new()
    {
        Id = 1001, Name = "Viper 1-1", Type = "F-16C_50", Cat = "plane", Coal = 2, Group = "Viper 1", GroupId = 1,
        Player = "Viper 1-1 | DiGii", Callsign = "", X = x, Y = 16, Z = z, Vx = speed, InAir = inAir,
    };

    private static Dictionary<string, UnitState> Units(UnitState u) => new() { [u.Name] = u };

    [SkippableFact]
    public async Task Clears_draws_monitors_and_hands_over_at_the_holding_point()
    {
        var setup = Setup();
        Skip.If(setup is null, "DCS install or Al Dhafra dump not available");
        var (ground, dcs, setTime) = setup!.Value;
        var spot106 = (X: -211546.62, Z: -171721.41);

        var viper = Viper(spot106.X, spot106.Z);
        await ground.RequestTaxiAsync(viper, Units(viper));
        Assert.Equal("Viper 1-1, Al Dhafra Ground, taxi to holding point runway 31R via A, B. Wind 310 degrees, 10 knots, QNH 1013, altimeter 29.92.", dcs.Texts[^1].Text);
        var drawing = dcs.Drawings["taxi-1"];
        Assert.Equal("HOLD SHORT 31R", drawing.Text);
        Assert.InRange(drawing.Points.Count, 5, 60);

        var departure = ground.Clearances[1].Departure!;
        var route = departure.Route.Geometry;
        // The route and its drawing end at the holding point, 90 m from the runway, not on it.
        Assert.Equal(90, departure.Runway.DistanceFrom(route[^1]), 1);
        Assert.Equal(90, departure.Runway.DistanceFrom(new Vec2(drawing.Points[^1].X, drawing.Points[^1].Z)), 0);
        var onRoute = route[route.Count / 2];

        // On the route and slow: nothing to say.
        int texts = dcs.Texts.Count;
        await ground.OnUnitsAsync(Units(Viper(onRoute.X, onRoute.Z, speed: 8)));
        Assert.Equal(texts, dcs.Texts.Count);

        // Off the route: one warning after three samples, then quiet for 30 s.
        for (int i = 0; i < 5; i++) await ground.OnUnitsAsync(Units(Viper(onRoute.X + 120, onRoute.Z)));
        Assert.Single(dcs.Texts.Skip(texts), t => t.Text.Contains("off your cleared taxi route"));

        // Too fast.
        await ground.OnUnitsAsync(Units(Viper(onRoute.X, onRoute.Z, speed: 20)));
        Assert.Contains(dcs.Texts, t => t.Text.EndsWith("reduce taxi speed."));

        // At the holding point: hold short, contact Tower, route removed.
        await ground.OnUnitsAsync(Units(Viper(route[^1].X, route[^1].Z)));
        Assert.Equal("Viper 1-1, Al Dhafra Ground, hold short runway 31R, contact Tower on 270.0 when ready for departure.", dcs.Texts[^1].Text);
        Assert.Equal(TaxiPhase.HoldingShort, ground.Clearances[1].Phase);
        Assert.False(dcs.Drawings.ContainsKey("taxi-1"));

        // Takeoff ends the clearance.
        await ground.OnEventAsync(new DcsEventMessage { Event = "runway_takeoff", Unit = "Viper 1-1" });
        Assert.Empty(ground.Clearances);
    }

    [SkippableFact]
    public async Task Startup_is_approved_with_wind_and_QNH_and_ends_at_takeoff()
    {
        var setup = Setup();
        Skip.If(setup is null, "DCS install or Al Dhafra dump not available");
        var (ground, dcs, _) = setup!.Value;

        var viper = Viper(-211546.62, -171721.41);
        await ground.RequestStartupAsync(viper, Units(viper));
        const string approved = "Viper 1-1, Al Dhafra Ground, start up approved, wind 310 degrees, 10 knots, QNH 1013, altimeter 29.92.";
        Assert.Equal(approved, dcs.Texts[^1].Text);
        Assert.NotNull(ground.StartupFor(viper, Units(viper)));

        // Before taxi, say again repeats it.
        await ground.SayAgainAsync(viper, Units(viper));
        Assert.Equal(approved, dcs.Texts[^1].Text);

        // Taxi works as before; takeoff ends it.
        await ground.RequestTaxiAsync(viper, Units(viper));
        Assert.StartsWith("Viper 1-1, Al Dhafra Ground, taxi to holding point runway", dcs.Texts[^1].Text);
        await ground.OnEventAsync(new DcsEventMessage { Event = "takeoff", Unit = "Viper 1-1" });
        Assert.Empty(ground.Startups);

        // Not on the ground at an airfield: no startup.
        var airborne = Viper(-211546.62, -171721.41, inAir: true);
        await ground.RequestStartupAsync(airborne, Units(airborne));
        Assert.Contains("you are airborne", dcs.Texts[^1].Text);
        Assert.Empty(ground.Startups);
    }

    [Fact]
    public void Startup_without_weather()
    {
        Assert.Equal("Viper 1-1, Al Dhafra Ground, start up approved.", Phraseology.StartupApproved("Viper 1-1", "Al Dhafra", null, "F-16C_50"));
    }

    [SkippableFact]
    public async Task Cancel_and_say_again()
    {
        var setup = Setup();
        Skip.If(setup is null, "DCS install or Al Dhafra dump not available");
        var (ground, dcs, _) = setup!.Value;

        var viper = Viper(-211546.62, -171721.41);
        await ground.RequestTaxiAsync(viper, Units(viper));
        string clearance = dcs.Texts[^1].Text;
        await ground.SayAgainAsync(viper, Units(viper));
        Assert.Equal(clearance, dcs.Texts[^1].Text);

        await ground.CancelTaxiAsync(viper, Units(viper));
        Assert.Contains("taxi clearance cancelled", dcs.Texts[^1].Text);
        Assert.Empty(dcs.Drawings);
        Assert.Empty(ground.Clearances);
    }

    [SkippableFact]
    public async Task Refuses_airborne_aircraft_and_unknown_places()
    {
        var setup = Setup();
        Skip.If(setup is null, "DCS install or Al Dhafra dump not available");
        var (ground, dcs, _) = setup!.Value;

        var airborne = Viper(-211546.62, -171721.41, inAir: true);
        await ground.RequestTaxiAsync(airborne, Units(airborne));
        Assert.Contains("you are airborne", dcs.Texts[^1].Text);
        var elsewhere = Viper(-150000, -100000);
        await ground.RequestTaxiAsync(elsewhere, Units(elsewhere));
        Assert.Contains("no ground control available", dcs.Texts[^1].Text);
        Assert.Empty(ground.Clearances);
    }
}
