using DcsAtc.Airfields;
using DcsAtc.Bridge;

namespace DcsAtc.Atc;

/// <summary>
/// Parking spots, landing spots and pads given to the members of a flight, each marked on the F10 map with the member's
/// number ("1-2"). A mark stays until its aircraft is down and stopped on it (or gone, or after <see cref="MaxAge"/>),
/// so wingmen keep theirs after the lead's clearance has ended.
/// </summary>
public sealed class SpotMarks(IDcsCommands dcs, Func<DateTime>? clock = null)
{
    /// <summary>Stopped this close to its spot, an aircraft is on it (metres).</summary>
    public const double Reached = 25;

    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    private sealed record Mark(string Unit, Vec2 Position, DateTime Since);

    private readonly Dictionary<string, Mark> _marks = [];
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);

    /// <summary>The drawing keys of the marks shown now.</summary>
    public IReadOnlyCollection<string> Keys => _marks.Keys;

    /// <summary>Where marked spots are, except those whose keys start with <paramref name="prefix"/> (a flight's own): taken, until their aircraft are on them.</summary>
    public IEnumerable<Vec2> TakenExcept(string prefix) =>
        _marks.Where(m => !m.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(m => m.Value.Position);

    /// <summary>A square of <paramref name="halfSize"/> metres around the spot, with <paramref name="label"/>.</summary>
    public async Task AddAsync(string key, string unit, int coalition, Vec2 spot, double halfSize, double[] color, string label, CancellationToken ct = default)
    {
        _marks[key] = new Mark(unit, spot, _now());
        Vec2[] square = [spot + new Vec2(-halfSize, -halfSize), spot + new Vec2(-halfSize, halfSize), spot + new Vec2(halfSize, halfSize),
            spot + new Vec2(halfSize, -halfSize), spot + new Vec2(-halfSize, -halfSize)];
        await dcs.DrawRouteAsync(key, coalition, square.Select(p => (p.X, p.Z)), color, label, ct);
    }

    /// <summary>Removes the marks whose keys start with <paramref name="prefix"/> (a flight's, when its clearance is cancelled).</summary>
    public async Task RemoveAsync(string prefix, CancellationToken ct = default)
    {
        foreach (var key in _marks.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            _marks.Remove(key);
            await dcs.ClearDrawingAsync(key, ct);
        }
    }

    public async Task OnUnitsAsync(IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        var now = _now();
        foreach (var (key, m) in _marks.ToList())
        {
            bool done = !units.TryGetValue(m.Unit, out var u)
                        || (!u.InAir && u.GroundSpeed < 1 && new Vec2(u.X, u.Z).DistanceTo(m.Position) <= Reached)
                        || now - m.Since > MaxAge;
            if (!done) continue;
            _marks.Remove(key);
            await dcs.ClearDrawingAsync(key, ct);
        }
    }

    /// <summary>The mission ended: its drawings went with it.</summary>
    public void Reset() => _marks.Clear();

    /// <summary>The member's number for its mark: "Hangman 1-2" → "1-2" (the whole callsign without a flight number).</summary>
    public static string Label(string callsign) => FlightRegistry.Split(callsign) is { } s && s.Prefix.Split(' ')[^1] is { } flight && flight.All(char.IsAsciiDigit)
        ? $"{flight}-{s.Number}" : callsign;

    /// <summary>A flight's members on the ground or in the air, in flight order (by their number), the lead first.</summary>
    public static List<UnitState> Members(Party party, IReadOnlyDictionary<string, UnitState> units) =>
        party.Units.Select(n => units.GetValueOrDefault(n)).OfType<UnitState>()
            .OrderBy(u => u.Name == party.LeadUnit ? 0 : 1)
            .ThenBy(u => FlightRegistry.Split(Phraseology.Callsign(u))?.Number ?? int.MaxValue)
            .ToList();
}
