using System.Globalization;
using System.Text;
using DcsAtc.Airfields.Taxi;

namespace DcsAtc.Airfields.Rn5;

/// <summary>Extra layers drawn on top of the network.</summary>
public sealed record SvgOverlay
{
    public IReadOnlyList<Runway> Runways { get; init; } = [];
    public IReadOnlyList<ParkingSpot> Parking { get; init; } = [];
    public IReadOnlyList<Vec2>? Route { get; init; }
    public string? Caption { get; init; }
    /// <summary>Taxiway names by edge index: named edges are coloured per name and labelled.</summary>
    public IReadOnlyDictionary<int, string>? EdgeNames { get; init; }
    /// <summary>The waypoint paths the names came from (drawn as small black tags at each waypoint).</summary>
    public IReadOnlyList<TaxiwayPath>? Labels { get; init; }
    /// <summary>A chart image drawn under the network (href: file path or data URI).</summary>
    public ChartImage? Chart { get; init; }
    /// <summary>Grid spacing in metres, labelled with DCS coordinates.</summary>
    public double? Grid { get; init; }
    /// <summary>Only draw this window: (min X, max X, min Z, max Z) in DCS coordinates.</summary>
    public (double MinX, double MaxX, double MinZ, double MaxZ)? Window { get; init; }
}

/// <summary>A georeferenced chart image: DCS x = A·px + B·py + C, z = D·px + E·py + F (px, py in image pixels).</summary>
public sealed record ChartImage(string Href, int Width, int Height, ChartAffine Affine, double Opacity = 0.45);

/// <summary>Renders a taxi network as SVG, north up. Runways dark, taxiways blue, stand lead-ins orange, nodes labelled.</summary>
public static class Rn5Svg
{
    public static string Render(Rn5Network net, double pixelsPerMetre = 0.5, bool labels = true, SvgOverlay? overlay = null)
    {
        var all = net.Edges.SelectMany(e => e.Points).ToList();
        var (minX, maxX, minZ, maxZ) = overlay?.Window ?? (all.Min(p => p.X), all.Max(p => p.X), all.Min(p => p.Z), all.Max(p => p.Z));
        const double margin = 60;
        double width = (maxZ - minZ) * pixelsPerMetre + 2 * margin;
        double height = (maxX - minX) * pixelsPerMetre + 2 * margin + 40;
        double X(Vec2 p) => (p.Z - minZ) * pixelsPerMetre + margin;
        double Y(Vec2 p) => (maxX - p.X) * pixelsPerMetre + margin + 40;
        string Sx(Vec2 p) => F(X(p));
        string Sy(Vec2 p) => F(Y(p));
        string Points(IEnumerable<Vec2> pts) => string.Join(" ", pts.Select(p => $"{Sx(p)},{Sy(p)}"));

        var deadEnds = net.Nodes.Where(n => n.Degree == 1).Select(n => n.Id).ToHashSet();
        var sb = new StringBuilder();
        sb.AppendLine($"""<svg xmlns="http://www.w3.org/2000/svg" width="{F(width)}" height="{F(height)}" viewBox="0 0 {F(width)} {F(height)}" font-family="Segoe UI, Arial, sans-serif">""");
        sb.AppendLine($"""<rect width="100%" height="100%" fill="#f4f2ec"/>""");
        if (overlay?.Chart is { } chart)
        {
            // Chart pixel -> SVG: compose the chart's affine fit with the map projection above.
            var a = chart.Affine;
            double s = pixelsPerMetre;
            sb.AppendLine($"""<image href="{chart.Href}" width="{chart.Width}" height="{chart.Height}" opacity="{F(chart.Opacity)}" """ +
                $"""transform="matrix({G(s * a.D)} {G(-s * a.A)} {G(s * a.E)} {G(-s * a.B)} {G(s * (a.F - minZ) + margin)} {G(s * (maxX - a.C) + margin + 40)})"/>""");
        }
        if (overlay?.Grid is double grid)
        {
            for (double gx = Math.Ceiling(minX / grid) * grid; gx <= maxX; gx += grid)
            {
                double y = (maxX - gx) * pixelsPerMetre + margin + 40;
                sb.AppendLine($"""<line x1="0" y1="{F(y)}" x2="{F(width)}" y2="{F(y)}" stroke="#999" stroke-width="0.6" stroke-dasharray="4 4"/><text x="4" y="{F(y - 3)}" font-size="11" fill="#666">x {gx:F0}</text>""");
            }
            for (double gz = Math.Ceiling(minZ / grid) * grid; gz <= maxZ; gz += grid)
            {
                double x = (gz - minZ) * pixelsPerMetre + margin;
                sb.AppendLine($"""<line x1="{F(x)}" y1="40" x2="{F(x)}" y2="{F(height)}" stroke="#999" stroke-width="0.6" stroke-dasharray="4 4"/><text x="{F(x + 3)}" y="52" font-size="11" fill="#666">z {gz:F0}</text>""");
            }
        }
        string caption = overlay?.Caption ?? $"{net.Name} — {net.Edges.Count} edges, {net.Nodes.Count} nodes, runways: {string.Join(", ", net.RunwayNames)} (north up)";
        sb.AppendLine($"""<text x="{F(margin)}" y="30" font-size="20" fill="#222">{Escape(caption)}</text>""");

        foreach (var e in net.Edges.OrderBy(e => e.IsRunway ? 0 : 1))
        {
            bool stand = deadEnds.Contains(e.NodeA) || deadEnds.Contains(e.NodeB);
            (string color, double w) = e.IsRunway ? ("#3a3a3a", 9.0) : stand ? ("#e08a1e", 2.0) : ("#2f6fd0", 3.5);
            sb.AppendLine($"""<polyline points="{Points(e.Points)}" fill="none" stroke="{color}" stroke-width="{F(w)}" stroke-linecap="round" stroke-linejoin="round"><title>edge {e.Index}: {e.NodeA}:{e.PortA} → {e.NodeB}:{e.PortB}, {Escape(e.TypeName)}, {e.Length:F0} m</title></polyline>""");
        }

        foreach (var n in net.Nodes.Where(n => n.Ports.Count > 0))
        {
            var c = n.Center;
            string fill = n.Degree == 1 ? "#e08a1e" : n.Spread > 150 ? "#c0392b" : "#1d3f73";
            sb.AppendLine($"""<circle cx="{Sx(c)}" cy="{Sy(c)}" r="{(n.Degree == 1 ? 2.5 : 4)}" fill="{fill}"><title>node {n.Id}: degree {n.Degree}, {n.Ports.Count} ports, spread {n.Spread:F0} m</title></circle>""");
            if (labels && n.Degree > 1)
                sb.AppendLine($"""<text x="{Sx(c)}" y="{Sy(c)}" dx="5" dy="-5" font-size="11" fill="#1d3f73">{n.Id}</text>""");
        }

        if (overlay?.EdgeNames is { } edgeNames)
        {
            foreach (var e in net.Edges)
            {
                if (!edgeNames.TryGetValue(e.Index, out var name)) continue;
                sb.AppendLine($"""<polyline points="{Points(e.Points)}" fill="none" stroke="{ColorFor(name)}" stroke-width="5" stroke-linecap="round"><title>{Escape(name)} (edge {e.Index})</title></polyline>""");
            }
            foreach (var e in net.Edges.Where(e => edgeNames.ContainsKey(e.Index) && e.Length > 120))
            {
                var mid = Polyline.PointAt(e.Points, e.Length / 2);
                sb.AppendLine($"""<text x="{Sx(mid)}" y="{Sy(mid)}" font-size="13" font-weight="bold" fill="{ColorFor(edgeNames[e.Index])}" stroke="#f4f2ec" stroke-width="3" paint-order="stroke" text-anchor="middle">{Escape(edgeNames[e.Index])}</text>""");
            }
        }
        if (overlay?.Labels is { } chartLabels)
        {
            foreach (var l in chartLabels)
            {
                foreach (var p in l.Points)
                {
                    sb.AppendLine($"""<rect x="{F(X(p) - 11)}" y="{F(Y(p) - 8)}" width="22" height="16" rx="8" fill="#111"/>""");
                    sb.AppendLine($"""<text x="{Sx(p)}" y="{F(Y(p) + 4)}" font-size="10" font-weight="bold" fill="#fff" text-anchor="middle">{Escape(l.Name)}</text>""");
                }
            }
        }

        if (overlay is not null)
        {
            foreach (var s in overlay.Parking)
            {
                sb.AppendLine($"""<rect x="{F(X(s.Position) - 3)}" y="{F(Y(s.Position) - 3)}" width="6" height="6" fill="#7a7a7a"><title>parking {s.Index} (type {s.TermType})</title></rect>""");
                sb.AppendLine($"""<text x="{Sx(s.Position)}" y="{Sy(s.Position)}" dx="4" dy="10" font-size="9" fill="#555">{s.Index}</text>""");
            }
            foreach (var r in overlay.Runways)
            {
                foreach (var h in r.HoldPoints)
                    sb.AppendLine($"""<circle cx="{Sx(h.Position)}" cy="{Sy(h.Position)}" r="5" fill="none" stroke="#d01010" stroke-width="2.5"><title>holding point, runway {r.Name}</title></circle>""");
                foreach (var end in new[] { r.EndA, r.EndB })
                    sb.AppendLine($"""<text x="{Sx(end.Threshold)}" y="{Sy(end.Threshold)}" dx="-14" dy="-12" font-size="22" font-weight="bold" fill="#111">{end.Name}</text>""");
            }
            if (overlay.Route is { Count: > 1 } route)
            {
                sb.AppendLine($"""<polyline points="{Points(route)}" fill="none" stroke="#d6249f" stroke-width="7" stroke-opacity="0.85" stroke-linecap="round" stroke-linejoin="round"/>""");
                sb.AppendLine($"""<circle cx="{Sx(route[0])}" cy="{Sy(route[0])}" r="9" fill="#d6249f"/>""");
                sb.AppendLine($"""<rect x="{F(X(route[^1]) - 9)}" y="{F(Y(route[^1]) - 9)}" width="18" height="18" fill="#d01010"/>""");
            }
        }
        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static readonly string[] Palette = ["#e6194b", "#3cb44b", "#4363d8", "#f58231", "#911eb4", "#008080", "#9a6324", "#800000", "#808000", "#000075", "#f032e6", "#469990"];

    private static string ColorFor(string name) => Palette[(int)((uint)name.Aggregate(17, (h, c) => h * 31 + c) % Palette.Length)];

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    private static string G(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
