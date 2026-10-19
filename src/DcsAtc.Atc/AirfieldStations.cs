using DcsAtc.Airfields;

namespace DcsAtc.Atc;

/// <summary>How a controller sends a pilot to another position: on that position's own frequency (said), or on the one they are on (not said).</summary>
public readonly record struct Contact(double? Mhz);

/// <summary>
/// The radio frequencies of an airfield's controllers. DCS gives every airfield a few ATC frequencies, shown in the
/// briefing, and all its positions work on them (<see cref="Shared"/>). Chart data can give a position a frequency of
/// its own (Al Dhafra: Ground 250.0, Tower 270.0, Approach 257.0); it then says its calls there and on the shared ones.
/// The ATIS has its own frequencies (<see cref="ForMap"/>).
/// </summary>
public sealed record AirfieldStations
{
    public static readonly AirfieldStations None = new();
    public static readonly IReadOnlyList<string> Roles = ["Ground", "Tower", "Approach"];

    // Bands of the airfields' frequencies in aircraft radios (AM); HF and the 30-88 MHz band DCS also lists are left out.
    private const double VhfLow = 118, VhfHigh = 136.975, UhfLow = 225, UhfHigh = 399.975, Step = 0.025;
    private static readonly double[] Guard = [121.5, 243];

    /// <summary>DCS's ATC frequencies of the airfield (MHz): every position works on them.</summary>
    public IReadOnlyList<double> Shared { get; init; } = [];
    public double? Ground { get; init; }
    public double? Tower { get; init; }
    public double? Approach { get; init; }
    /// <summary>The ATIS broadcasts on these.</summary>
    public IReadOnlyList<double> Atis { get; init; } = [];

    /// <summary>A position's own frequency (from chart data), if it has one.</summary>
    public double? Own(string role) => role switch { "Ground" => Ground, "Tower" => Tower, "Approach" => Approach, _ => null };

    /// <summary>What a position's calls go out on: its own frequency first, then the shared ones.</summary>
    public IReadOnlyList<double> For(string role) => Own(role) is double own ? [own, .. Shared.Where(f => !Same(f, own))] : Shared;

    /// <summary>The position is on the air here.</summary>
    public bool Serves(string role) => For(role).Count > 0;

    /// <summary>Every frequency the controllers work (and listen) on.</summary>
    public IReadOnlyList<double> All => Distinct(Roles.SelectMany(For));

    /// <summary>The position whose own frequency this is; null for a shared frequency (or one that is not this airfield's).</summary>
    public string? RoleOn(double mhz) => Roles.FirstOrDefault(r => Own(r) is double f && Same(f, mhz));

    public bool Has(double mhz) => All.Any(f => Same(f, mhz));

    /// <summary>How to send a pilot to a position: its own frequency, or none to say; null when it is not on the air here.</summary>
    public Contact? ContactFor(string role) => Serves(role) ? new Contact(Own(role)) : null;

    /// <summary>
    /// Who answers a pilot's request on <paramref name="mhz"/>: the position whose job it is (<paramref name="owner"/>)
    /// when the pilot is on one of its frequencies (always so on a shared one); otherwise the position called sends the
    /// pilot there (<c>SendTo</c>: the owner's frequency).
    /// </summary>
    /// <param name="called">The position the pilot called: the one whose own frequency it is, else the one named or the likely one.</param>
    public (string Answering, double? SendTo) Route(double? mhz, string called, string owner)
    {
        if (owner == called || mhz is not double on || !Serves(owner) || For(owner).Any(f => Same(f, on))) return (owner, null);
        return (called, For(owner)[0]);
    }

    /// <summary>"Ground/Tower/Approach 118.55, 250.1; ATIS 119.55, 260.1" (or with each position's own frequency first).</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Atis.Count > 0) parts.Add($"ATIS {List(Atis)}");
        foreach (var role in Roles.Where(r => Own(r) is not null)) parts.Add($"{role} {Phraseology.Frequency(Own(role))}");
        if (Shared.Count > 0) parts.Add($"{(Roles.All(r => Own(r) is not null) ? "all positions" : "Ground/Tower/Approach")} {List(Shared)}");
        return string.Join(", ", parts);
    }

    public static bool Same(double a, double b) => Math.Abs(a - b) < 0.0005;

    /// <summary>DCS's frequencies (MHz) an airfield's controllers use: VHF 118-137 and UHF 225-400 AM, sorted, once each.</summary>
    public static IReadOnlyList<double> Usable(IEnumerable<(double Mhz, bool Am)> dcs) =>
        Distinct(dcs.Where(f => f.Am && (InVhf(f.Mhz) || InUhf(f.Mhz))).Select(f => Round(f.Mhz)).Order());

    /// <summary>
    /// The stations of every airfield of a map, from DCS's usable ATC frequencies and the chart data. ATIS: the
    /// chart's, else one for each shared frequency, UHF + 10 MHz and VHF + 1 MHz (VHF − 1 MHz at the top of the band),
    /// moved on by 25 kHz while another airfield's ATC or ATIS frequency, or a guard frequency, has it. Airfields are
    /// taken in id order, so the frequencies are the same every mission.
    /// </summary>
    public static IReadOnlyDictionary<int, AirfieldStations> ForMap(IEnumerable<(int Id, IReadOnlyList<double> Shared, AirfieldFrequencies? Chart)> airfields)
    {
        var list = airfields.OrderBy(a => a.Id).ToList();
        var taken = new List<double>(Guard);
        foreach (var (_, shared, chart) in list)
        {
            taken.AddRange(shared);
            taken.AddRange(new[] { chart?.Ground, chart?.Tower, chart?.Approach, chart?.Atis }.OfType<double>());
        }
        var result = new Dictionary<int, AirfieldStations>();
        foreach (var (id, shared, chart) in list)
        {
            var atis = new List<double>();
            if (chart?.Atis is double own) atis.Add(own);
            else
            {
                foreach (var f in shared)
                {
                    if (AtisFor(f, taken) is not double a) continue;
                    atis.Add(a);
                    taken.Add(a);
                }
            }
            result[id] = new AirfieldStations { Shared = shared, Ground = chart?.Ground, Tower = chart?.Tower, Approach = chart?.Approach, Atis = atis };
        }
        return result;
    }

    private static double? AtisFor(double atc, List<double> taken)
    {
        bool uhf = InUhf(atc);
        double step = Step, candidate = uhf ? atc + 10 : atc + 1;
        if (!uhf && candidate > VhfHigh)
        {
            candidate = atc - 1;
            step = -Step;
        }
        for (int i = 0; i < 400; i++, candidate += step)
        {
            double c = Round(candidate);
            if (!(uhf ? InUhf(c) : InVhf(c))) return null;
            if (!taken.Any(t => Same(t, c))) return c;
        }
        return null;
    }

    private static bool InVhf(double mhz) => mhz >= VhfLow - 0.0005 && mhz <= VhfHigh + 0.0005;
    private static bool InUhf(double mhz) => mhz >= UhfLow - 0.0005 && mhz <= UhfHigh + 0.0005;
    private static double Round(double mhz) => Math.Round(mhz * 1000) / 1000;

    private static IReadOnlyList<double> Distinct(IEnumerable<double> frequencies)
    {
        var result = new List<double>();
        foreach (var f in frequencies)
            if (!result.Any(r => Same(r, f))) result.Add(f);
        return result;
    }

    private static string List(IEnumerable<double> frequencies) => string.Join(" / ", frequencies.Select(f => Phraseology.Frequency(f)));
}
