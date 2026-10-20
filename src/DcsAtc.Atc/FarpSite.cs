using System.Text.RegularExpressions;
using DcsAtc.Airfields;
using DcsAtc.Bridge;

namespace DcsAtc.Atc;

/// <summary>A landing pad of a FARP site: its number at the site ("pad 4"), the DCS FARP it belongs to, where it is.</summary>
public sealed record FarpPad(int Number, string Farp, Vec2 Position);

/// <summary>
/// A FARP site with one controller: the FARPs of a FOB (Retribution builds one from single-pad invisible FARPs named
/// "FOB Laos Invisible FARP 0", "… 1", …), or a FARP placed in the mission editor. It calls itself by the site's name
/// ("FOB Laos") and works on the frequency the mission gives its FARPs.
/// </summary>
public sealed partial class FarpSite
{
    /// <summary>Within this of a pad an aircraft is at the site (metres).</summary>
    public const double Margin = 300;

    /// <summary>FARPs closer than this belong to one site (metres).</summary>
    public const double SameSite = 1000;

    /// <summary>The lowest DCS airbase id of its FARPs (menu ids).</summary>
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required int Coalition { get; init; }
    /// <summary>Its FARPs' DCS airbase names.</summary>
    public required IReadOnlyList<string> Farps { get; init; }
    public required IReadOnlyList<FarpPad> Pads { get; init; }
    /// <summary>The mission's frequency, as one position (none: text messages only).</summary>
    public required AirfieldStations Frequencies { get; init; }

    public Vec2 Center => Vec2.Average(Pads.Select(p => p.Position).ToList());

    /// <summary>Whether a position is at the site (within <see cref="Margin"/> of a pad).</summary>
    public bool Covers(Vec2 p) => Pads.Any(pad => pad.Position.DistanceTo(p) <= Margin);

    /// <summary>A transmission of the site's controller, or null without a frequency.</summary>
    public RadioCall? Call(int coalition, string text) => Frequencies.All.Count > 0 ? new RadioCall(Name, "", coalition, text, Frequencies) : null;

    /// <summary>
    /// The sites of a mission's FARPs: FARPs with the same name apart from a "FARP 3" ending, or within
    /// <see cref="SameSite"/> of each other, are one site; its pads are numbered in the order of the FARPs' names.
    /// FARPs not in the mission now (no airbase id) are left out.
    /// </summary>
    public static IReadOnlyList<FarpSite> Build(IEnumerable<FarpInfo> farps)
    {
        var list = farps.Where(f => f.Id is not null).OrderBy(f => NaturalKey(f.Name), StringComparer.Ordinal).ToList();
        int n = list.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                if (BaseName(list[i].Name) == BaseName(list[j].Name) || Position(list[i]).DistanceTo(Position(list[j])) <= SameSite)
                    parent[Find(i)] = Find(j);
            }
        }

        var sites = new List<FarpSite>();
        foreach (var group in Enumerable.Range(0, n).GroupBy(Find))
        {
            var members = group.Select(i => list[i]).ToList();
            var pads = new List<FarpPad>();
            foreach (var f in members)
            {
                IEnumerable<Vec2> spots = f.Pads.Count > 0 ? f.Pads.OrderBy(p => p.Index).Select(p => new Vec2(p.X, p.Z)) : [new Vec2(f.X, f.Z)];
                foreach (var spot in spots) pads.Add(new FarpPad(pads.Count + 1, f.Name, spot));
            }
            var frequency = members.Where(f => f.Frequency is not null).GroupBy(f => (Mhz: f.Frequency!.Value, f.Modulation))
                .OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
            sites.Add(new FarpSite
            {
                Id = members.Min(f => f.Id!.Value),
                Name = BaseName(members[0].Name),
                Coalition = members.GroupBy(f => f.Coalition).OrderByDescending(g => g.Count()).First().Key,
                Farps = members.Select(f => f.Name).ToList(),
                Pads = pads,
                Frequencies = new AirfieldStations
                {
                    Shared = frequency is { } fr ? AirfieldStations.Usable([(fr.Mhz, fr.Modulation == 0)]) : [],
                },
            });
        }
        return sites.OrderBy(s => s.Id).ToList();
    }

    /// <summary>"FOB Laos Invisible FARP 3" → "FOB Laos"; "London FARP" → "London"; "Pad-2" → "Pad".</summary>
    public static string BaseName(string name)
    {
        string b = FarpSuffix().Replace(name, "").Trim();
        return b.Length > 0 ? b : name;
    }

    private static Vec2 Position(FarpInfo f) =>
        f.Pads.Count > 0 ? Vec2.Average(f.Pads.Select(p => new Vec2(p.X, p.Z)).ToList()) : new Vec2(f.X, f.Z);

    /// <summary>Numbers padded so that "FARP 10" sorts after "FARP 9".</summary>
    private static string NaturalKey(string name) => Digits().Replace(name, m => m.Value.PadLeft(8, '0'));

    [GeneratedRegex(@"(\s*(invisible\s+)?farp)?[\s_#-]*\d*\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex FarpSuffix();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
