using DcsAtc.Airfields;
using DcsAtc.Airfields.Taxi;
using DcsAtc.Atc;
using DcsAtc.Bridge;

namespace DcsAtc.Host;

public enum StripKind
{
    Departure,
    Arrival,
}

/// <summary>One flight progress strip: who, which runway, what it is cleared for, since when.</summary>
/// <param name="Note">What it waits for or gives way to ("Giving way to Enfield 2-1", "Runway occupied").</param>
public sealed record Strip(StripKind Kind, string Callsign, string Type, string? Runway, string Clearance, string? Note, DateTime Since, bool Ai);

/// <summary>An airfield's (or FARP site's) strips by position, with the frequencies to show over them.</summary>
public sealed record AirfieldStrips(string Name, bool IsFarp, string? GroundFrequency, string? TowerFrequency, string? ApproachFrequency, string? Atis,
    IReadOnlyList<Strip> Ground, IReadOnlyList<Strip> Tower, IReadOnlyList<Strip> Approach)
{
    public int Count => Ground.Count + Tower.Count + Approach.Count;
}

/// <summary>What the controllers are doing right now, for the control panel.</summary>
public sealed record AtcSnapshot(DateTime At, bool Connected, string? Mission, string? Theatre, bool ManagingAi, bool? ServerMapDrawings,
    IReadOnlyList<AirfieldStrips> Airfields)
{
    public static readonly AtcSnapshot Empty = new(DateTime.MinValue, false, null, null, false, null, []);
}

public sealed partial class AtcApp
{
    // When each strip got its current clearance, so the panel can show how long it has been.
    private readonly Dictionary<string, DateTime> _stripSince = [];

    /// <summary>
    /// The strips of every airfield with traffic or on the air. Call it from <see cref="Changed"/> (the app's loop): the
    /// controllers are not thread-safe.
    /// </summary>
    public AtcSnapshot Snapshot()
    {
        var now = DateTime.Now;
        var seen = new HashSet<string>();
        var byField = new Dictionary<string, (bool Farp, AirfieldStations? Stations, int? Id, List<Strip> Ground, List<Strip> Tower, List<Strip> Approach)>();

        List<Strip> Column(string field, bool farp, AirfieldStations? stations, int? id, int column)
        {
            if (!byField.TryGetValue(field, out var f)) byField[field] = f = (farp, stations, id, [], [], []);
            return column switch { 0 => f.Ground, 1 => f.Tower, _ => f.Approach };
        }

        void Add(string field, bool farp, AirfieldStations? stations, int? id, int column, string key, StripKind kind, string callsign, string type,
            string? runway, string clearance, string? note, bool ai = false)
        {
            string since = $"{field}|{column}|{key}|{clearance}";
            seen.Add(since);
            if (!_stripSince.TryGetValue(since, out var at)) _stripSince[since] = at = now;
            Column(field, farp, stations, id, column).Add(new Strip(kind, callsign, type, runway, clearance, note, at, ai));
        }

        string TypeOf(string unitName, string? fallback = null) =>
            ShortType(_units.TryGetValue(unitName, out var u) ? u.Type : fallback ?? "");

        // Ground: start up, taxi out and in; AI departures held or taxiing.
        foreach (var s in _ground.Startups.Values)
            Add(s.Airfield.Airbase.Name, false, s.Airfield.Frequencies, s.Airfield.Airbase.Id, 0, $"start{s.Key}", StripKind.Departure,
                s.Party.Callsign, TypeOf(s.UnitName), null, "Start up approved", null);
        foreach (var c in _ground.Clearances.Values)
        {
            var layout = c.Airfield.Layout;
            string clearance = c.Phase == TaxiPhase.HoldingShort
                ? $"Holding short {c.Departure?.End.Name}"
                : c.Departure is not null
                    ? $"{(c.Helicopter ? "Air-taxi" : "Taxi")}{Via(layout, c.Route)}"
                    : $"{(c.Helicopter ? "Air-taxi" : "Taxi")} to {(c.Spot is { } spot ? layout.AreaNear(spot.Position) ?? "parking" : "parking")}";
            string? note = c.TrafficHold is { } hold ? $"Giving way to {CallsignOf(hold.OtherUnit)}" : null;
            Add(c.Airfield.Airbase.Name, false, c.Airfield.Frequencies, c.Airfield.Airbase.Id, 0, $"taxi{c.Key}",
                c.Departure is not null ? StripKind.Departure : StripKind.Arrival, c.Party.Callsign, TypeOf(c.UnitName), c.Departure?.End.Name, clearance, note);
        }
        if (ManagingAi)
        {
            foreach (var d in _ai.Departures.Values)
            {
                var lead = _units.Values.FirstOrDefault(u => u.Group == d.Group);
                Add(d.Airfield.Airbase.Name, false, d.Airfield.Frequencies, d.Airfield.Airbase.Id, 0, $"ai{d.Group}", StripKind.Departure,
                    d.Callsign.Length > 0 ? d.Callsign : d.Group, ShortType(lead?.Type ?? ""), null,
                    d.Phase == AiDeparturePhase.Held ? "Held at parking" : "Taxiing (DCS route)", d.Phase == AiDeparturePhase.Held ? d.HeldFor : null, ai: true);
            }
        }

        // Tower: departures and arrivals on its runways (and helicopters at spots).
        foreach (var f in _tower.Flights.Values)
        {
            bool departing = f.Phase is TowerPhase.WaitingForDeparture or TowerPhase.LineUpAndWait or TowerPhase.ClearedForTakeoff or TowerPhase.Departed;
            string runway = f.Spot is not null ? "SPOT" : f.FromPresentPosition ? "PP" : f.End.Name;
            string clearance = f.Phase switch
            {
                TowerPhase.WaitingForDeparture => "Ready for departure",
                TowerPhase.LineUpAndWait => f.Backtrack ? "Backtrack and line up" : "Line up and wait",
                TowerPhase.ClearedForTakeoff => "Cleared for takeoff",
                TowerPhase.Departed => "Departed",
                TowerPhase.Inbound => f.InPattern ? "Closed traffic" : f.Overhead ? "Overhead, report initial" : "Inbound",
                TowerPhase.ClearedToLand => f.Spot is not null ? "Cleared to land at the spot" : "Cleared to land",
                TowerPhase.Landed => "Landed, vacating",
                _ => f.Phase.ToString(),
            };
            if (f.Emergency is { } emergency) clearance = $"{Phraseology.Word(emergency).ToUpperInvariant()}: {clearance}";
            Add(f.Airfield.Airbase.Name, false, f.Airfield.Frequencies, f.Airfield.Airbase.Id, 1, $"twr{f.Key}",
                departing ? StripKind.Departure : StripKind.Arrival, f.Callsign, TypeOf(f.UnitName, f.UnitType), runway, clearance, Capitalize(f.LastReason));
        }

        // Approach: arrivals vectored or holding, departures under radar service.
        foreach (var a in _approach.Flights.Values)
        {
            string clearance = a.Phase switch
            {
                ApproachPhase.Holding => $"Holding at {a.HoldAltitude:0} ft",
                ApproachPhase.Overhead => "Overhead, to the initial point",
                ApproachPhase.RadarService => "Radar service",
                _ => $"Vectors {a.Type switch { ApproachType.Ils => "ILS", ApproachType.Tacan => "TACAN", ApproachType.Visual => "visual", _ => "" }} {a.End.Name} · {a.Leg.ToString().ToLowerInvariant()}",
            };
            string? miles = _units.TryGetValue(a.UnitName, out var u)
                ? $"{new Vec2(u.X - a.Airfield.Airbase.X, u.Z - a.Airfield.Airbase.Z).Length / 1852:0} NM" : null;
            Add(a.Airfield.Airbase.Name, false, a.Airfield.Frequencies, a.Airfield.Airbase.Id, 2, $"app{a.Key}",
                a.IsArrival ? StripKind.Arrival : StripKind.Departure, a.Callsign, TypeOf(a.UnitName, a.UnitType), a.End.Name, clearance,
                a.Extended ? $"Extended · {miles}" : miles);
        }

        // AI arrivals: with Approach until Tower clears them to land.
        if (ManagingAi)
        {
            foreach (var a in _arrivals.Arrivals.Values)
            {
                var lead = _units.Values.Where(u => u.Group == a.Group).MinBy(u => u.Id);
                string? miles = lead is null ? null : $"{new Vec2(lead.X - a.Airfield.Airbase.X, lead.Z - a.Airfield.Airbase.Z).Length / 1852:0} NM";
                (int column, string clearance) = a.Phase switch
                {
                    AiArrivalPhase.Holding => (2, $"Holding at {a.HoldAltitude:0} ft"),
                    _ when a.LandingCalled => (1, "Cleared to land"),
                    AiArrivalPhase.Cleared => (2, "Left the hold, to final"),
                    _ => (2, "Straight in"),
                };
                Add(a.Airfield.Airbase.Name, false, a.Airfield.Frequencies, a.Airfield.Airbase.Id, column, $"aiarr{a.Group}", StripKind.Arrival,
                    a.Callsign, ShortType(a.UnitType), a.End?.Name, clearance, a.Phase == AiArrivalPhase.Holding ? a.HeldFor : miles, ai: true);
            }
        }

        // FARP sites: one controller each.
        foreach (var f in _farps.Flights.Values)
        {
            bool landing = f.Phase == FarpPhase.ClearedToLand;
            string clearance = f.Phase switch
            {
                FarpPhase.ClearedToLand => f.Pad is { } pad ? $"Cleared to land, pad {pad.Number}" : "Cleared to land",
                FarpPhase.WaitingForDeparture => "Ready for departure",
                FarpPhase.ClearedForTakeoff => "Cleared for takeoff",
                _ => "Departed",
            };
            Add(f.Site.Name, true, null, f.Site.Id, 1, $"farp{f.Key}", landing ? StripKind.Arrival : StripKind.Departure, f.Callsign,
                TypeOf(f.UnitName), f.Pad is { } p ? $"PAD {p.Number}" : null, clearance, Capitalize(f.LastReason));
        }

        // Airfields on the air without traffic still get a tab.
        foreach (var airbase in _airfields.Airbases.Where(a => a.Category == 0 && _onAir.Any(o => o.Id == a.Id)))
            Column(airbase.Name, false, _airfields.StationsFor(airbase), airbase.Id, 0);
        foreach (var site in _farps.Sites.Where(s => _onAir.Any(o => o.Id == s.Id)))
            Column(site.Name, true, null, site.Id, 1);

        foreach (var key in _stripSince.Keys.Where(k => !seen.Contains(k)).ToList()) _stripSince.Remove(key);

        var airfields = byField
            .Select(kv =>
            {
                var (farp, stations, id, ground, tower, approach) = kv.Value;
                string? siteFrequency = farp ? _farps.Sites.FirstOrDefault(s => s.Name == kv.Key) is { } site && site.Frequencies.Shared.Count > 0
                    ? Phraseology.Frequency(site.Frequencies.Shared[0]) : null : null;
                return new AirfieldStrips(kv.Key, farp,
                    Frequency(stations, "Ground"), farp ? siteFrequency : Frequency(stations, "Tower"), Frequency(stations, "Approach"),
                    AtisLine(stations, id), ground, tower, approach);
            })
            .OrderByDescending(f => f.Count).ThenBy(f => f.Name)
            .ToList();
        return new AtcSnapshot(now, Connected, Mission, Theatre, ManagingAi, ServerMapDrawings, airfields);
    }

    /// <summary>"F-16C_50" → "F-16C", "FA-18C_hornet" → "FA-18C".</summary>
    public static string ShortType(string dcsType) => dcsType.Split('_')[0];

    private string CallsignOf(string unitName) =>
        _units.TryGetValue(unitName, out var u) ? Phraseology.Callsign(u) : unitName;

    private static string Via(Airfield layout, TaxiRoute route)
    {
        var via = layout.Via(route).Where(v => !v.IsRunwayCrossing).Select(v => v.Name).ToList();
        return via.Count == 0 ? "" : $" via {string.Join(", ", via)}";
    }

    private static string? Frequency(AirfieldStations? stations, string role) =>
        stations is null ? null : stations.Own(role) is double own ? Phraseology.Frequency(own)
            : stations.Serves(role) && stations.Shared.Count > 0 ? Phraseology.Frequency(stations.Shared[0]) : null;

    private string? AtisLine(AirfieldStations? stations, int? id)
    {
        if (stations is null || stations.Atis.Count == 0) return null;
        string line = $"ATIS {Phraseology.Frequency(stations.Atis[0])}";
        return id is int i && _atis.Current.TryGetValue(i, out var current) ? $"{line} · information {current.Letter}" : line;
    }

    private static string? Capitalize(string? s) => string.IsNullOrEmpty(s) ? null : char.ToUpperInvariant(s[0]) + s[1..];
}
