using DcsAtc.Airfields;
using DcsAtc.Airfields.Rn5;
using DcsAtc.Bridge;

namespace DcsAtc.Atc;

/// <summary>An airbase of the current mission with its ground layout.</summary>
public sealed record GroundAirfield(AirbaseSummary Airbase, Airfield Layout)
{
    public string Facility => Phraseology.Facility(Airbase.Name);
}

/// <summary>
/// Finds and caches the ground layout of each airbase in the current mission: the DCS taxi network
/// (matched by position, since .rn5 file names differ from airbase names), chart labels from
/// <c>&lt;data&gt;/airfields/&lt;theatre&gt;/&lt;rn5 name&gt;.labels.json</c> when they exist, and runways and
/// parking spots from DCS.
/// </summary>
public sealed class AirfieldCatalog(IDcsCommands dcs, string? dcsDirectory, string? dataDirectory, Action<string>? log = null)
{
    /// <summary>A .rn5 network whose centre is further than this from the airbase is not that airbase's.</summary>
    public const double MatchDistance = 3000;

    private readonly Dictionary<string, Task<GroundAirfield?>> _cache = [];
    private Task<List<(string Path, Vec2 Center)>>? _index;
    private string? _theatre;
    private IReadOnlyList<AirbaseSummary> _airbases = [];

    public IReadOnlyList<AirbaseSummary> Airbases => _airbases;

    /// <summary>Starts a new mission: forgets everything cached for the previous one.</summary>
    public void SetMission(string? theatre, IReadOnlyList<AirbaseSummary> airbases)
    {
        if (theatre != _theatre) _index = null;
        _theatre = theatre;
        _airbases = airbases;
        _cache.Clear();
    }

    /// <summary>The nearest airdrome within <paramref name="maxDistance"/> metres that has a taxi network.</summary>
    public async Task<GroundAirfield?> NearestAsync(Vec2 position, double maxDistance = 4000, CancellationToken ct = default)
    {
        foreach (var airbase in _airbases.Where(a => a.Category == 0).OrderBy(a => Distance(a, position)))
        {
            if (Distance(airbase, position) > maxDistance) break;
            if (await GetAsync(airbase, ct) is { } airfield) return airfield;
        }
        return null;
    }

    public Task<GroundAirfield?> GetAsync(AirbaseSummary airbase, CancellationToken ct = default)
    {
        if (!_cache.TryGetValue(airbase.Name, out var task)) _cache[airbase.Name] = task = LoadAsync(airbase, ct);
        return task;
    }

    /// <summary>The labels file for an airbase, if there is one (matched on the airbase name in the file).</summary>
    public AirfieldLabels? LabelsFor(string airbaseName)
    {
        if (dataDirectory is null || _theatre is null) return null;
        string dir = Path.Combine(dataDirectory, "airfields", _theatre);
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, "*.labels.json").Select(AirfieldLabels.Load).FirstOrDefault(l => l.Airfield == airbaseName);
    }

    private async Task<GroundAirfield?> LoadAsync(AirbaseSummary airbase, CancellationToken ct)
    {
        var index = await (_index ??= Task.Run(BuildIndex, ct));
        var position = new Vec2(airbase.X, airbase.Z);
        var match = index.Where(i => i.Center.DistanceTo(position) <= MatchDistance).OrderBy(i => i.Center.DistanceTo(position)).FirstOrDefault();
        if (match.Path is null)
        {
            log?.Invoke($"no taxi network found for {airbase.Name}");
            return null;
        }

        var net = await Task.Run(() => Rn5Reader.Read(match.Path), ct);
        AirfieldLabels? labels = null;
        if (dataDirectory is not null && _theatre is not null)
        {
            string path = Path.Combine(dataDirectory, "airfields", _theatre, $"{net.Name}.labels.json");
            if (File.Exists(path)) labels = AirfieldLabels.Load(path);
        }
        var info = await dcs.GetAirbaseAsync(airbase.Name, ct);
        var layout = await Task.Run(() => Airfield.Build(net,
            info?.Runways.Select(r => r.Name).OfType<string>(),
            info?.Parking.Select(p => new ParkingSpot(p.Index, p.TermType, new Vec2(p.X, p.Z))),
            labels), ct);
        log?.Invoke($"{airbase.Name}: taxi network {net.Name}, runways {string.Join(", ", layout.Runways.Select(r => r.Name))}, " +
                    $"{(labels is null ? "no taxiway names" : $"{layout.TaxiwayNames.Values.Distinct().Count()} taxiway names")}");
        foreach (var warning in layout.LabelWarnings) log?.Invoke($"  label warning: {warning}");
        return new GroundAirfield(airbase, layout);
    }

    private List<(string Path, Vec2 Center)> BuildIndex()
    {
        var result = new List<(string, Vec2)>();
        if (dcsDirectory is null || _theatre is null) return result;
        string dir = Path.Combine(dcsDirectory, "Mods", "terrains", _theatre, "AirfieldsTaxiways");
        if (!Directory.Exists(dir))
        {
            log?.Invoke($"taxi networks not found: {dir}");
            return result;
        }
        foreach (var file in Directory.EnumerateFiles(dir, "*.rn5"))
        {
            try
            {
                var net = Rn5Reader.Read(file);
                var pts = net.Edges.SelectMany(e => e.Points).ToList();
                if (pts.Count == 0) continue;
                result.Add((file, new Vec2((pts.Min(p => p.X) + pts.Max(p => p.X)) / 2, (pts.Min(p => p.Z) + pts.Max(p => p.Z)) / 2)));
            }
            catch (InvalidDataException ex)
            {
                log?.Invoke($"skipping {Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return result;
    }

    private static double Distance(AirbaseSummary a, Vec2 p) => new Vec2(a.X, a.Z).DistanceTo(p);
}
