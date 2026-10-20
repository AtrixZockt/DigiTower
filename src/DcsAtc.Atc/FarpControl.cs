using DcsAtc.Airfields;
using DcsAtc.Bridge;

namespace DcsAtc.Atc;

public enum FarpPhase
{
    /// <summary>Asked to depart, not yet cleared.</summary>
    WaitingForDeparture,
    ClearedForTakeoff,
    /// <summary>Off the pad, still with the site until clear of it.</summary>
    Departed,
    ClearedToLand,
}

/// <summary>A helicopter (or declared flight) a FARP site is working: departing from a pad, or landing on one.</summary>
public sealed class FarpFlight
{
    public required Party Party { get; init; }
    public required FarpSite Site { get; init; }
    public FarpPhase Phase { get; set; }
    public DepartureIntent Intent { get; init; }
    /// <summary>Which way a departure goes (degrees true).</summary>
    public double Bearing { get; init; }
    /// <summary>The pad a landing was given (null: all pads were occupied).</summary>
    public FarpPad? Pad { get; init; }
    public string LastText { get; set; } = "";
    internal string? LastReason { get; set; }

    public int Key => Party.Key;
    public string Callsign => Party.Callsign;
    /// <summary>The unit whose position drives the flight: the aircraft itself, or the flight lead.</summary>
    public string UnitName => Party.LeadUnit;
    public string DrawingKey => $"pad-{Key}";
}

/// <summary>
/// The controller of each FARP site (<see cref="FarpSite"/>): start up approved; departures from the pad a helicopter
/// stands on, as at airfields ("from present position, make right turn for north departure, …, cleared for takeoff",
/// held while another helicopter takes off or lands within 200 ft); landing on a free pad it assigns (the nearest,
/// never within 200 ft of a pad another helicopter is landing on or leaving), marked on the F10 map; "frequency change
/// approved" once clear of the site. It calls itself by the site's name and talks on the mission's frequency.
/// </summary>
public sealed class FarpControl(IDcsCommands dcs, FlightRegistry flights, Action<string>? log = null, IAtcRadio? radio = null)
{
    private const double Nm = 1852, Ft = 0.3048;

    /// <summary>A FARP site takes landing requests up to this far (metres).</summary>
    public const double LandingRange = 15 * Nm;

    /// <summary>A departure this far from the site, or this high, gets "frequency change approved" (metres).</summary>
    public const double ClearDistance = 2 * Nm, ClearHeight = 1500 * Ft;

    private static readonly double[] PadColor = [0, 0.9, 0.2, 1];

    private readonly Dictionary<int, FarpFlight> _flights = [];
    private readonly SpotMarks _marks = new(dcs);

    /// <summary>The drawing keys of the wingmen's pads marked now.</summary>
    public IReadOnlyCollection<string> PadMarkKeys => _marks.Keys;
    private readonly Dictionary<int, (FarpSite Site, string Unit, int Coalition, string Text)> _startups = [];
    private IReadOnlyList<FarpSite> _sites = [];

    public IReadOnlyList<FarpSite> Sites => _sites;

    /// <summary>Flights by party key (the lead's group id).</summary>
    public IReadOnlyDictionary<int, FarpFlight> Flights => _flights;

    /// <summary>A new mission's FARP sites.</summary>
    public void SetSites(IReadOnlyList<FarpSite> sites)
    {
        _sites = sites;
        _flights.Clear();
        _startups.Clear();
        _marks.Reset();
    }

    /// <summary>The mission ended: its drawings went with it.</summary>
    public void Reset() => SetSites([]);

    public FarpSite? Site(int id) => _sites.FirstOrDefault(s => s.Id == id);

    /// <summary>The site an aircraft is at: on the ground, or a helicopter low over it.</summary>
    public FarpSite? SiteAt(UnitState unit) =>
        Helicopters.OnField(unit) ? _sites.Where(s => s.Covers(Pos(unit))).MinBy(s => s.Center.DistanceTo(Pos(unit))) : null;

    public FarpFlight? FlightFor(UnitState unit, IReadOnlyDictionary<string, UnitState> units) => _flights.GetValueOrDefault(flights.PartyFor(unit, units).Key);

    /// <summary>Whether the unit's party has its startup approval from this site.</summary>
    public bool HasStartup(UnitState unit, IReadOnlyDictionary<string, UnitState> units, FarpSite site) =>
        _startups.TryGetValue(flights.PartyFor(unit, units).Key, out var s) && s.Site == site;

    /// <summary>"Viper 1-1, FOB Laos, start up approved, wind 310 degrees, 10 knots, QNH 1013, altimeter 29.92."</summary>
    public async Task RequestStartupAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, FarpSite? at = null, CancellationToken ct = default)
    {
        if (await AtSiteAsync(unit, units, at, ct) is not { } who) return;
        var (party, lead, site) = who;
        string text = Phraseology.StartupApprovedBy(party.Callsign, site.Name, await WeatherAsync(site, ct), lead.Type);
        _startups[party.Key] = (site, lead.Name, lead.Coal, text);
        await SayAsync(party, site, text, ct);
    }

    /// <summary>A departure from the pad the helicopter stands on, in the direction it wants.</summary>
    public async Task RequestDepartureAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, DepartureIntent intent = DepartureIntent.OwnNavigation,
        FarpSite? at = null, CancellationToken ct = default)
    {
        if (await AtSiteAsync(unit, units, at, ct) is not { } who) return;
        var (party, lead, site) = who;
        var direction = intent == DepartureIntent.ClosedTraffic ? DepartureIntent.OwnNavigation : intent;
        var flight = new FarpFlight
        {
            Party = party,
            Site = site,
            Phase = FarpPhase.WaitingForDeparture,
            Intent = direction,
            Bearing = Helicopters.Bearing(direction) ?? lead.Hdg,
        };
        _flights[party.Key] = flight;
        await EvaluateAsync(flight, units, ct, requested: true);
    }

    /// <summary>Landing: a free pad (the nearest), marked on the F10 map, and cleared straight in.</summary>
    public async Task RequestLandingAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, FarpSite? at = null, CancellationToken ct = default)
    {
        if (unit.GroupId is null) return;
        var party = flights.PartyFor(unit, units);
        var lead = units.GetValueOrDefault(party.LeadUnit) ?? unit;
        if (!lead.InAir)
        {
            await TextAsync(party, Phraseology.OnTheGround(party.Callsign), ct);
            return;
        }
        var position = Pos(lead);
        var site = at ?? _sites.MinBy(s => s.Center.DistanceTo(position));
        if (site is null || site.Center.DistanceTo(position) > LandingRange)
        {
            await TextAsync(party, Phraseology.NoFarpWithin(party.Callsign, LandingRange / Nm), ct);
            return;
        }
        if (_flights.TryGetValue(party.Key, out var cleared) && cleared.Phase == FarpPhase.ClearedToLand && cleared.Site == site)
        {
            await SayAsync(cleared, cleared.LastText, ct);
            return;
        }
        await RemoveAsync(party.Key, ct);

        var weather = await WeatherAsync(site, ct);
        var free = FreePads(site, party, units);
        var pad = free.MinBy(p => p.Position.DistanceTo(position));
        var flight = new FarpFlight { Party = party, Site = site, Phase = FarpPhase.ClearedToLand, Pad = pad };
        _flights[party.Key] = flight;
        var numbers = new List<int>();
        if (pad is not null)
        {
            numbers.Add(pad.Number);
            var c = pad.Position;
            Vec2[] square = [c + new Vec2(-12, -12), c + new Vec2(-12, 12), c + new Vec2(12, 12), c + new Vec2(12, -12), c + new Vec2(-12, -12)];
            string label = party.IsFlight ? $"PAD {pad.Number} {SpotMarks.Label(Phraseology.Callsign(lead))}" : $"PAD {pad.Number}";
            await dcs.DrawRouteAsync(flight.DrawingKey, lead.Coal, square.Select(p => (p.X, p.Z)), PadColor, label, ct);

            // The wingmen in the air: the free pads nearest the lead's, each marked with its number until it is down on it.
            await _marks.RemoveAsync($"pad-{party.Key}-", ct);
            var left = free.Where(p => p != pad).OrderBy(p => p.Position.DistanceTo(pad.Position)).ToList();
            foreach (var member in SpotMarks.Members(party, units).Where(m => m.Name != lead.Name && m.InAir))
            {
                if (left.Count == 0) break;
                var own = left[0];
                left.RemoveAt(0);
                numbers.Add(own.Number);
                string number = SpotMarks.Label(Phraseology.Callsign(member));
                await _marks.AddAsync($"pad-{party.Key}-{number}", member.Name, lead.Coal, own.Position, 12, PadColor, $"PAD {own.Number} {number}", ct);
            }
        }
        await SayAsync(flight, pad is null ? Phraseology.PadsOccupied(party.Callsign, site.Name)
            : Phraseology.LandAtSpot(party.Callsign, site.Name, Phraseology.Pads(numbers), weather), ct);
    }

    /// <summary>Repeats the last call to the flight, or its startup approval.</summary>
    public async Task SayAgainAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        var party = flights.PartyFor(unit, units);
        if (_flights.TryGetValue(party.Key, out var f) && f.LastText != "") await SayAsync(f.Party, f.Site, f.LastText, ct);
        else if (_startups.TryGetValue(party.Key, out var s)) await SayAsync(party, s.Site, s.Text, ct);
    }

    public async Task OnUnitsAsync(IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        await _marks.OnUnitsAsync(units, ct);
        // A startup approval lasts while the helicopter is at its site.
        foreach (var (key, s) in _startups.ToList())
        {
            if (!units.TryGetValue(s.Unit, out var u) || !Helicopters.OnField(u) || !s.Site.Covers(Pos(u))) _startups.Remove(key);
        }
        foreach (var f in _flights.Values.ToList()) await EvaluateAsync(f, units, ct, requested: false);
    }

    public async Task OnEventAsync(DcsEventMessage e, CancellationToken ct = default)
    {
        if (e.Event is not ("crash" or "dead" or "unit_lost" or "player_leave_unit" or "pilot_dead" or "ejection")) return;
        foreach (var f in _flights.Values.Where(f => f.UnitName == e.Unit).ToList()) await RemoveAsync(f.Key, ct);
        foreach (var (key, _) in _startups.Where(s => s.Value.Unit == e.Unit).ToList()) _startups.Remove(key);
    }

    private async Task EvaluateAsync(FarpFlight f, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct, bool requested)
    {
        if (!units.TryGetValue(f.UnitName, out var unit))
        {
            await RemoveAsync(f.Key, ct);
            return;
        }
        var position = Pos(unit);
        switch (f.Phase)
        {
            case FarpPhase.WaitingForDeparture:
            {
                if (Airborne(unit))
                {
                    f.Phase = FarpPhase.Departed;
                    break;
                }
                string? reason = DepartureBlocker(f, unit, units);
                if (reason is null)
                {
                    var weather = await WeatherAsync(f.Site, ct);
                    double? bearing = Helicopters.Bearing(f.Intent);
                    string? direction = bearing is null ? null : f.Intent.ToString().ToLowerInvariant();
                    string? turn = bearing is double b ? Phraseology.TurnTo(unit.Hdg, b) : null;
                    f.Phase = FarpPhase.ClearedForTakeoff;
                    await SayAsync(f, Phraseology.HelicopterDeparture(f.Callsign, f.Site.Name, turn, direction, weather), ct);
                }
                else if (requested || f.LastReason != reason)
                {
                    f.LastReason = reason;
                    await SayAsync(f, Phraseology.HoldPosition(f.Callsign, f.Site.Name, reason), ct);
                }
                break;
            }
            case FarpPhase.ClearedForTakeoff:
                if (Airborne(unit)) f.Phase = FarpPhase.Departed;
                break;

            case FarpPhase.Departed:
                if (f.Site.Center.DistanceTo(position) > ClearDistance || unit.Agl > ClearHeight)
                {
                    await RemoveAsync(f.Key, ct);
                    await SayAsync(f, Phraseology.ContactAfterDeparture(f.Callsign, null), ct);
                }
                break;

            case FarpPhase.ClearedToLand:
                // Down (on its pad or where it chose), or gone: done.
                if (!unit.InAir || f.Site.Center.DistanceTo(position) > LandingRange + 5 * Nm) await RemoveAsync(f.Key, ct);
                break;
        }
    }

    /// <summary>Another helicopter cleared to land within 200 ft of the departing one, or taking off from there.</summary>
    private string? DepartureBlocker(FarpFlight f, UnitState unit, IReadOnlyDictionary<string, UnitState> units)
    {
        var position = Pos(unit);
        foreach (var o in _flights.Values.Where(o => o != f && o.Site == f.Site))
        {
            Vec2? point = o.Phase == FarpPhase.ClearedToLand ? o.Pad?.Position
                : o.Phase == FarpPhase.ClearedForTakeoff && units.TryGetValue(o.UnitName, out var u) && !Airborne(u) ? Pos(u)
                : null;
            if (point is Vec2 p && p.DistanceTo(position) < Helicopters.Separation) return Phraseology.HelicopterTraffic;
        }
        return null;
    }

    /// <summary>
    /// The pads with nobody on them, not within 200 ft of a pad another helicopter is cleared to land on (or a wingman's)
    /// or of a helicopter taking off.
    /// </summary>
    private List<FarpPad> FreePads(FarpSite site, Party party, IReadOnlyDictionary<string, UnitState> units)
    {
        var standing = units.Values.Where(u => !party.Units.Contains(u.Name) && Helicopters.OnField(u)).Select(Pos).ToList();
        var busy = _flights.Values.Where(o => o.Key != party.Key && o.Site == site)
            .Select(o => o.Phase == FarpPhase.ClearedToLand ? o.Pad?.Position
                : o.Phase == FarpPhase.ClearedForTakeoff && units.TryGetValue(o.UnitName, out var u) && !Airborne(u) ? Pos(u) : (Vec2?)null)
            .OfType<Vec2>()
            .Concat(_marks.TakenExcept($"pad-{party.Key}-"))
            .ToList();
        return site.Pads
            .Where(p => !standing.Any(q => q.DistanceTo(p.Position) < AtcMenu.ParkedDistance) && !busy.Any(q => q.DistanceTo(p.Position) < Helicopters.Separation))
            .ToList();
    }

    /// <summary>On the site's ground, or a helicopter low over it; tells the pilot otherwise.</summary>
    private async Task<(Party Party, UnitState Lead, FarpSite Site)?> AtSiteAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, FarpSite? at, CancellationToken ct)
    {
        if (unit.GroupId is null) return null;
        var party = flights.PartyFor(unit, units);
        var lead = units.GetValueOrDefault(party.LeadUnit) ?? unit;
        if (!Helicopters.OnField(lead))
        {
            await TextAsync(party, Phraseology.AlreadyAirborne(party.Callsign), ct);
            return null;
        }
        var site = at is null ? SiteAt(lead) : at.Covers(Pos(lead)) ? at : null;
        if (site is null)
        {
            await TextAsync(party, Phraseology.NotAtFarp(party.Callsign), ct);
            return null;
        }
        return (party, lead, site);
    }

    private async Task RemoveAsync(int key, CancellationToken ct)
    {
        if (_flights.Remove(key, out var f) && f.Pad is not null) await dcs.ClearDrawingAsync(f.DrawingKey, ct);
    }

    private Task<AirbaseWeather?> WeatherAsync(FarpSite site, CancellationToken ct) => dcs.GetAirbaseWeatherAsync(site.Farps[0], ct);

    private static bool Airborne(UnitState unit) => unit.InAir && unit.Agl > Helicopters.AirTaxiHeight;

    private static Vec2 Pos(UnitState unit) => new(unit.X, unit.Z);

    private async Task SayAsync(FarpFlight f, string text, CancellationToken ct)
    {
        f.LastText = text;
        await SayAsync(f.Party, f.Site, text, ct);
    }

    /// <summary>Text to every member; on the site's frequency too.</summary>
    private async Task SayAsync(Party party, FarpSite site, string text, CancellationToken ct)
    {
        foreach (var g in party.Groups) await dcs.ShowTextAsync(g, text, 20, ct: ct);
        if (radio is not null && site.Call(party.Coalition, text) is { } call) radio.Transmit(call);
        log?.Invoke($"{party.Callsign}: {text}");
    }

    private async Task TextAsync(Party party, string text, CancellationToken ct)
    {
        foreach (var g in party.Groups) await dcs.ShowTextAsync(g, text, 10, ct: ct);
    }
}
