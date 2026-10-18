using System.Text.RegularExpressions;
using DcsAtc.Bridge;

namespace DcsAtc.Atc;

/// <summary>
/// Who ATC is talking to: one aircraft, or a declared flight (addressed as "Hangman 1 flight", messages
/// go to every member, the lead's position drives clearances, members never count as traffic to each other).
/// </summary>
/// <param name="Key">Identifies the party in the controllers: the lead's group id.</param>
public sealed record Party(string Callsign, int Key, string LeadUnit, IReadOnlyList<int> Groups, IReadOnlySet<string> Units)
{
    public bool IsFlight => Units.Count > 1;

    public static Party Single(UnitState unit) =>
        new(Phraseology.Callsign(unit), unit.GroupId ?? -1, unit.Name, unit.GroupId is int g ? [g] : [], new HashSet<string> { unit.Name });
}

/// <summary>A declared flight: players sharing a callsign prefix ("Hangman 1-1", "Hangman 1-2" → "Hangman 1").</summary>
public sealed class Flight
{
    public required string Name { get; init; }
    public required int DeclaredSize { get; init; }
    /// <summary>Member unit names with their number in the flight, in order.</summary>
    public SortedDictionary<int, string> Members { get; } = [];
    public string Lead => Members.First().Value;
}

/// <summary>
/// Flights are declared by a pilot ("ATC → Flight → Flight of 2"), not formed automatically: the flight
/// is every player whose callsign has the same prefix as the declaring pilot's, up to the declared size.
/// Members who spawn later join automatically until the flight is complete.
/// </summary>
public sealed partial class FlightRegistry(IDcsCommands dcs, Action<string>? log = null)
{
    private readonly Dictionary<string, Flight> _flights = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<Flight> Flights => _flights.Values;

    /// <summary>"Hangman 1-2" → ("Hangman 1", 2); null for callsigns without a flight number.</summary>
    public static (string Prefix, int Number)? Split(string callsign)
    {
        var m = FlightCallsign().Match(callsign.Trim());
        return m.Success ? (m.Groups[1].Value.Trim(), int.Parse(m.Groups[2].Value)) : null;
    }

    [GeneratedRegex(@"^(.*?\d+)\s*-\s*(\d+)$")]
    private static partial Regex FlightCallsign();

    public async Task DeclareAsync(UnitState unit, int size, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        if (unit.GroupId is not int groupId) return;
        string callsign = Phraseology.Callsign(unit);
        if (Split(callsign) is not { } own)
        {
            await dcs.ShowTextAsync(groupId, $"{callsign}: to fly as a flight, use a callsign with a flight number in your player name, e.g. \"Hangman 1-1 | {unit.Player}\".", 20, ct: ct);
            return;
        }
        // Members: the declaring pilot plus other players with the same prefix, lowest numbers first.
        var members = units.Values
            .Where(u => u.IsPlayer && u.Name != unit.Name)
            .Select(u => (Unit: u, Split: Split(Phraseology.Callsign(u))))
            .Where(x => x.Split is { } s && string.Equals(s.Prefix, own.Prefix, StringComparison.OrdinalIgnoreCase))
            .Select(x => (x.Unit.Name, x.Split!.Value.Number))
            .Prepend((unit.Name, own.Number))
            .DistinctBy(m => m.Number)
            .Take(size)
            .ToList();
        // Take them out of earlier flights first: removing empties a flight, which deletes it.
        foreach (var (name, _) in members) Remove(name);
        _flights.Remove(own.Prefix);

        var flight = new Flight { Name = own.Prefix, DeclaredSize = size };
        foreach (var (name, number) in members) flight.Members[number] = name;
        _flights[own.Prefix] = flight;
        await TellAsync(flight, units, Describe(flight, units), ct);
        log?.Invoke($"flight declared: {Describe(flight, units)}");
    }

    public async Task LeaveAsync(UnitState unit, IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        if (FlightOf(unit.Name) is not { } flight || unit.GroupId is not int groupId) return;
        Remove(unit.Name);
        await dcs.ShowTextAsync(groupId, $"{Phraseology.Callsign(unit)}, single ship.", 10, ct: ct);
        if (_flights.ContainsKey(flight.Name)) await TellAsync(flight, units, $"{Phraseology.Callsign(unit)} left. {Describe(flight, units)}", ct);
    }

    /// <summary>Drops members that are gone and adds players of an incomplete flight that have appeared.</summary>
    public async Task OnUnitsAsync(IReadOnlyDictionary<string, UnitState> units, CancellationToken ct = default)
    {
        foreach (var flight in _flights.Values.ToList())
        {
            foreach (var (number, name) in flight.Members.ToList())
                if (!units.ContainsKey(name)) flight.Members.Remove(number);
            if (flight.Members.Count == 0)
            {
                _flights.Remove(flight.Name);
                continue;
            }
            if (flight.Members.Count >= flight.DeclaredSize) continue;
            foreach (var u in units.Values.Where(u => u.IsPlayer && FlightOf(u.Name) is null))
            {
                if (Split(Phraseology.Callsign(u)) is { } s && string.Equals(s.Prefix, flight.Name, StringComparison.OrdinalIgnoreCase)
                    && flight.Members.Count < flight.DeclaredSize && flight.Members.TryAdd(s.Number, u.Name))
                {
                    await TellAsync(flight, units, $"{Phraseology.Callsign(u)} joined. {Describe(flight, units)}", ct);
                }
            }
        }
    }

    public Flight? FlightOf(string unitName) => _flights.Values.FirstOrDefault(f => f.Members.ContainsValue(unitName));

    /// <summary>The unit's flight as a party, or the unit alone.</summary>
    public Party PartyFor(UnitState unit, IReadOnlyDictionary<string, UnitState> units)
    {
        if (FlightOf(unit.Name) is not { } flight) return Party.Single(unit);
        var members = flight.Members.Values.Where(units.ContainsKey).Select(n => units[n]).ToList();
        var lead = members.FirstOrDefault() ?? unit;
        return new Party($"{flight.Name} flight", lead.GroupId ?? unit.GroupId ?? -1, lead.Name,
            members.Select(m => m.GroupId).OfType<int>().Distinct().ToList(), members.Select(m => m.Name).ToHashSet());
    }

    public void Reset() => _flights.Clear();

    private void Remove(string unitName)
    {
        foreach (var flight in _flights.Values.ToList())
        {
            foreach (var (number, name) in flight.Members.ToList())
                if (name == unitName) flight.Members.Remove(number);
            if (flight.Members.Count == 0) _flights.Remove(flight.Name);
        }
    }

    private string Describe(Flight flight, IReadOnlyDictionary<string, UnitState> units)
    {
        var names = flight.Members.Values.Select(n => units.TryGetValue(n, out var u) ? Phraseology.Callsign(u) : n).ToList();
        string text = $"{flight.Name} flight of {flight.DeclaredSize}: {string.Join(", ", names)} (lead {names[0]}).";
        if (names.Count < flight.DeclaredSize) text += $" Waiting for {flight.DeclaredSize - names.Count} more.";
        return text;
    }

    private async Task TellAsync(Flight flight, IReadOnlyDictionary<string, UnitState> units, string text, CancellationToken ct)
    {
        foreach (var name in flight.Members.Values)
            if (units.TryGetValue(name, out var u) && u.GroupId is int g)
                await dcs.ShowTextAsync(g, text, 15, ct: ct);
    }
}
