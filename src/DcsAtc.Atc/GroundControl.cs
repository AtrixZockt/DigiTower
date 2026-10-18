using System.Globalization;
using DcsAtc.Airfields;
using DcsAtc.Airfields.Taxi;
using DcsAtc.Bridge;

namespace DcsAtc.Atc;

public enum TaxiPhase
{
    /// <summary>Cleared to a holding point and taxiing.</summary>
    Taxiing,
    /// <summary>At the holding point, waiting for Tower.</summary>
    HoldingShort,
}

/// <summary>A flight's current taxi clearance.</summary>
public sealed class TaxiClearance
{
    public required int GroupId { get; init; }
    public required string UnitName { get; init; }
    public required string Callsign { get; init; }
    public required int Coalition { get; init; }
    public required GroundAirfield Airfield { get; init; }
    public required DepartureRoute Departure { get; init; }
    public required string Text { get; init; }
    public TaxiPhase Phase { get; set; } = TaxiPhase.Taxiing;
    internal int OffRouteSamples { get; set; }
    internal DateTime LastOffRouteWarning { get; set; } = DateTime.MinValue;
    internal DateTime LastSpeedWarning { get; set; } = DateTime.MinValue;

    public string DrawingKey => $"taxi-{GroupId}";
}

/// <summary>
/// Ground control: taxi clearances to the active runway's holding point (route computed on the DCS
/// taxi network, named from chart labels, drawn on the F10 map) and monitoring while taxiing.
/// </summary>
public sealed class GroundControl(IDcsCommands dcs, AirfieldCatalog airfields, Action<string>? log = null, Func<DateTime>? clock = null)
{
    /// <summary>Further than this from the cleared route counts as off route (metres).</summary>
    public const double OffRouteDistance = 35;

    /// <summary>Taxi speed limit (m/s, 30 knots).</summary>
    public const double TaxiSpeedLimit = 15.4;

    /// <summary>Within this distance of the holding point the aircraft is holding short (metres).</summary>
    public const double HoldingPointReached = 30;

    public static readonly TimeSpan WarningInterval = TimeSpan.FromSeconds(30);
    private static readonly double[] RouteColor = [1, 0, 1, 1];

    private readonly Dictionary<int, TaxiClearance> _clearances = [];
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);

    public IReadOnlyDictionary<int, TaxiClearance> Clearances => _clearances;

    public async Task RequestTaxiAsync(UnitState unit, CancellationToken ct = default)
    {
        if (unit.GroupId is not int groupId) return;
        string callsign = Phraseology.Callsign(unit);
        if (unit.InAir)
        {
            await dcs.ShowTextAsync(groupId, $"{callsign}, you are airborne. Ground control is for aircraft on the ground.", 10, ct: ct);
            return;
        }
        var airfield = await airfields.NearestAsync(new Vec2(unit.X, unit.Z), ct: ct);
        if (airfield is null)
        {
            await dcs.ShowTextAsync(groupId, $"{callsign}, no ground control available at your position.", 10, ct: ct);
            return;
        }

        var weather = await dcs.GetAirbaseWeatherAsync(airfield.Airbase.Name, ct);
        var ends = airfield.Layout.ActiveEnds(weather?.WindFrom ?? 0, weather?.WindSpeed ?? 0);
        var position = new Vec2(unit.X, unit.Z);
        var departure = ends
            .Select(e => airfield.Layout.RouteToRunway(position, e.End.Name))
            .OfType<DepartureRoute>()
            .MinBy(d => d.Route.Length);
        if (departure is null)
        {
            await dcs.ShowTextAsync(groupId, $"{callsign}, {airfield.Facility} Ground, unable to find a taxi route from your position.", 15, ct: ct);
            return;
        }

        var via = airfield.Layout.Via(departure.Route);
        string text = Phraseology.TaxiClearance(callsign, airfield.Facility, departure.End, via, weather, unit.Type);
        var clearance = new TaxiClearance
        {
            GroupId = groupId,
            UnitName = unit.Name,
            Callsign = callsign,
            Coalition = unit.Coal,
            Airfield = airfield,
            Departure = departure,
            Text = text,
        };
        _clearances[groupId] = clearance;

        await dcs.ShowTextAsync(groupId, text, 30, ct: ct);
        var line = Polyline.Simplify(departure.Route.Geometry, 3).Select(p => (p.X, p.Z));
        await dcs.DrawRouteAsync(clearance.DrawingKey, unit.Coal, line, RouteColor, $"HOLD SHORT {departure.End.Name}", ct);
        log?.Invoke($"{callsign}: {text} ({departure.Route.Length:F0} m)");
    }

    public async Task CancelTaxiAsync(int groupId, CancellationToken ct = default)
    {
        if (!_clearances.Remove(groupId, out var c)) return;
        await dcs.ClearDrawingAsync(c.DrawingKey, ct);
        await dcs.ShowTextAsync(groupId, $"{c.Callsign}, {c.Airfield.Facility} Ground, taxi clearance cancelled, hold position.", 15, ct: ct);
    }

    public async Task SayAgainAsync(int groupId, CancellationToken ct = default)
    {
        if (_clearances.TryGetValue(groupId, out var c))
            await dcs.ShowTextAsync(groupId, c.Text, 30, ct: ct);
    }

    /// <summary>Checks every taxiing flight against its clearance.</summary>
    public async Task OnUnitsAsync(IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        foreach (var c in _clearances.Values.ToList())
        {
            if (!units.TryGetValue(c.UnitName, out var unit) || unit.InAir)
            {
                await EndAsync(c, ct);
                continue;
            }
            if (c.Phase != TaxiPhase.Taxiing) continue;

            var position = new Vec2(unit.X, unit.Z);
            var route = c.Departure.Route.Geometry;
            if (position.DistanceTo(route[^1]) <= HoldingPointReached)
            {
                c.Phase = TaxiPhase.HoldingShort;
                await dcs.ClearDrawingAsync(c.DrawingKey, ct);
                string tower = c.Airfield.Layout.Labels?.Frequencies?.Tower is double f ? $" on {f.ToString("0.0##", CultureInfo.InvariantCulture)}" : "";
                await dcs.ShowTextAsync(c.GroupId,
                    $"{c.Callsign}, {c.Airfield.Facility} Ground, hold short runway {c.Departure.End.Name}, contact Tower{tower} when ready for departure.", 20, ct: ct);
                continue;
            }

            var now = _now();
            if (Polyline.Project(route, position).Distance > OffRouteDistance)
            {
                c.OffRouteSamples++;
                if (c.OffRouteSamples >= 3 && now - c.LastOffRouteWarning >= WarningInterval)
                {
                    c.LastOffRouteWarning = now;
                    await dcs.ShowTextAsync(c.GroupId, $"{c.Callsign}, {c.Airfield.Facility} Ground, you are off your cleared taxi route. Check route and hold position if unsure.", 15, ct: ct);
                }
            }
            else
            {
                c.OffRouteSamples = 0;
            }

            if (unit.GroundSpeed > TaxiSpeedLimit && now - c.LastSpeedWarning >= WarningInterval)
            {
                c.LastSpeedWarning = now;
                await dcs.ShowTextAsync(c.GroupId, $"{c.Callsign}, {c.Airfield.Facility} Ground, reduce taxi speed.", 10, ct: ct);
            }
        }
    }

    public async Task OnEventAsync(DcsEventMessage e, CancellationToken ct = default)
    {
        if (e.Event is not ("takeoff" or "runway_takeoff" or "crash" or "dead" or "unit_lost" or "player_leave_unit")) return;
        var c = _clearances.Values.FirstOrDefault(x => x.UnitName == e.Unit);
        if (c is not null) await EndAsync(c, ct);
    }

    /// <summary>The mission ended: forget all clearances (their drawings went with the mission).</summary>
    public void Reset() => _clearances.Clear();

    private async Task EndAsync(TaxiClearance c, CancellationToken ct)
    {
        _clearances.Remove(c.GroupId);
        await dcs.ClearDrawingAsync(c.DrawingKey, ct);
    }
}
