using DcsAtc.Airfields;
using DcsAtc.Bridge;

namespace DcsAtc.Atc;

/// <summary>An F10 menu entry: its submenu path under F10 → Other, its label, and the id the app dispatches on.</summary>
public sealed record MenuEntry(IReadOnlyList<string> Path, string Label, string Id);

/// <summary>
/// The ATC calls a pilot can make right now, as F10 menu entries (menus are per group in DCS, so every flight sees
/// its own). Ground on the ground at an airfield: taxi, or cancel / say again once cleared, taxi to parking when not
/// parked. Tower at a runway: ready for departure; in the air near an airfield: request landing; while Tower works
/// the flight: report final, say again. Flight: declare a flight (numbered callsigns only) or leave it.
/// </summary>
public sealed class AtcMenu(AirfieldCatalog airfields, GroundControl ground, TowerControl tower, FlightRegistry flights)
{
    public static readonly string[] Ground = ["ATC", "Ground"];
    public static readonly string[] Tower = ["ATC", "Tower"];
    public static readonly string[] Departure = ["ATC", "Tower", "Ready for departure"];
    public static readonly string[] Flight = ["ATC", "Flight"];
    public static readonly string[] Debug = ["ATC", "Debug"];

    /// <summary>Closer than this to a parking spot counts as parked (metres).</summary>
    public const double ParkedDistance = 25;

    /// <summary>The "Ready for departure" choices: menu id suffix, label, intent.</summary>
    public static readonly IReadOnlyList<(string Id, string Label, DepartureIntent Intent)> Departures =
    [
        ("own", "Own navigation", DepartureIntent.OwnNavigation),
        ("north", "Departure north", DepartureIntent.North),
        ("east", "Departure east", DepartureIntent.East),
        ("south", "Departure south", DepartureIntent.South),
        ("west", "Departure west", DepartureIntent.West),
        ("closed", "Closed traffic", DepartureIntent.ClosedTraffic),
    ];

    public static DepartureIntent? IntentOf(string menuId) =>
        Departures.FirstOrDefault(d => menuId == $"tower.ready.{d.Id}") is { Label: not null } d ? d.Intent : null;

    /// <summary>The menu for the group of <paramref name="unit"/> (a player), from its flight's situation.</summary>
    public async Task<IReadOnlyList<MenuEntry>> ForAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        var entries = new List<MenuEntry>();
        var party = flights.PartyFor(unit, units);
        var lead = units.GetValueOrDefault(party.LeadUnit) ?? unit;
        var position = new Vec2(lead.X, lead.Z);
        var taxi = ground.ClearanceFor(unit, units);
        var working = tower.Flights.GetValueOrDefault(party.Key);
        var here = lead.InAir ? null : await airfields.NearestAsync(position, ct: ct);

        // Ground.
        if (taxi is not null)
        {
            entries.Add(new(Ground, "Cancel taxi", "ground.cancel"));
            entries.Add(new(Ground, "Say again", "ground.repeat"));
        }
        else if (here is not null && working?.Phase is null or TowerPhase.Landed)
        {
            if (working is null) entries.Add(new(Ground, "Request taxi", "ground.taxi"));
            if (!here.Layout.Parking.Any(p => p.Position.DistanceTo(position) < ParkedDistance))
                entries.Add(new(Ground, "Request taxi to parking", "ground.parking"));
        }

        // Tower.
        if (working is not null)
        {
            if (working.Phase == TowerPhase.WaitingForDeparture) AddDepartures(entries);
            if (working.Phase == TowerPhase.Inbound && !working.ReportedFinal) entries.Add(new(Tower, "Report final", "tower.final"));
            if (working.LastText != "") entries.Add(new(Tower, "Say again", "tower.repeat"));
        }
        else if (lead.InAir)
        {
            if (airfields.Airbases.Any(a => a.Category == 0 && new Vec2(a.X, a.Z).DistanceTo(position) <= TowerControl.InboundRange))
                entries.Add(new(Tower, "Request landing", "tower.landing"));
        }
        else if (taxi?.Phase == TaxiPhase.HoldingShort
                 || (taxi is null && here is not null && here.Layout.Runways.Any(r => r.DistanceFrom(position) <= TowerControl.HoldingPointRange)))
        {
            AddDepartures(entries);
        }

        // Flight.
        if (flights.FlightOf(unit.Name) is not null)
        {
            entries.Add(new(Flight, "Single ship", "flight.single"));
        }
        else if (FlightRegistry.Split(Phraseology.Callsign(unit)) is not null)
        {
            for (int n = 2; n <= 4; n++) entries.Add(new(Flight, $"Flight of {n}", $"flight.{n}"));
        }

        entries.Add(new(Debug, "Where am I?", "debug.where"));
        return entries;
    }

    private static void AddDepartures(List<MenuEntry> entries)
    {
        foreach (var (id, label, _) in Departures) entries.Add(new(Departure, label, $"tower.ready.{id}"));
    }
}
