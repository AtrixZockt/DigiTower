namespace DcsAtc.Atc;

/// <summary>
/// The holding levels over each airfield's fix (its TACAN, or the field), shared by Approach (players) and the AI
/// arrivals: one stack, so nobody is sent to a level someone else holds at. Levels are in feet.
/// </summary>
public sealed class HoldingStacks
{
    // Airbase id -> level (ft) -> who holds there.
    private readonly Dictionary<int, SortedDictionary<double, string>> _stacks = [];

    /// <summary>The lowest free level from <paramref name="lowest"/> up in <paramref name="step"/>s, now taken by <paramref name="occupant"/>.</summary>
    public double Take(int airbase, string occupant, double lowest, double step)
    {
        Leave(occupant);
        if (!_stacks.TryGetValue(airbase, out var stack)) _stacks[airbase] = stack = [];
        double level = lowest;
        while (stack.ContainsKey(level)) level += step;
        stack[level] = occupant;
        return level;
    }

    /// <summary><paramref name="occupant"/> left the hold (or never was in it).</summary>
    public void Leave(string occupant)
    {
        foreach (var stack in _stacks.Values)
        {
            foreach (var (level, who) in stack.ToList())
                if (who == occupant) stack.Remove(level);
        }
    }

    /// <summary>Who holds at an airfield, lowest level first.</summary>
    public IReadOnlyList<(double Level, string Occupant)> At(int airbase) =>
        _stacks.TryGetValue(airbase, out var stack) ? stack.Select(kv => (kv.Key, kv.Value)).ToList() : [];

    /// <summary>Everyone who holds anywhere.</summary>
    public IEnumerable<string> Occupants => _stacks.Values.SelectMany(s => s.Values);

    public void Clear() => _stacks.Clear();
}
