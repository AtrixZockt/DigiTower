using System.Globalization;
using System.Text;

namespace DcsAtc.Airfields.Rn5;

/// <summary>Renders a taxi network as SVG, north up. Runways dark, taxiways blue, stand lead-ins orange, nodes labelled.</summary>
public static class Rn5Svg
{
    public static string Render(Rn5Network net, double pixelsPerMetre = 0.5, bool labels = true)
    {
        var all = net.Edges.SelectMany(e => e.Points).ToList();
        double minX = all.Min(p => p.X), maxX = all.Max(p => p.X), minZ = all.Min(p => p.Z), maxZ = all.Max(p => p.Z);
        const double margin = 60;
        double width = (maxZ - minZ) * pixelsPerMetre + 2 * margin;
        double height = (maxX - minX) * pixelsPerMetre + 2 * margin + 40;
        string Sx(Vec2 p) => F((p.Z - minZ) * pixelsPerMetre + margin);
        string Sy(Vec2 p) => F((maxX - p.X) * pixelsPerMetre + margin + 40);

        var deadEnds = net.Nodes.Where(n => n.Degree == 1).Select(n => n.Id).ToHashSet();
        var sb = new StringBuilder();
        sb.AppendLine($"""<svg xmlns="http://www.w3.org/2000/svg" width="{F(width)}" height="{F(height)}" viewBox="0 0 {F(width)} {F(height)}" font-family="Segoe UI, Arial, sans-serif">""");
        sb.AppendLine($"""<rect width="100%" height="100%" fill="#f4f2ec"/>""");
        sb.AppendLine($"""<text x="{F(margin)}" y="30" font-size="20" fill="#222">{Escape(net.Name)} — {net.Edges.Count} edges, {net.Nodes.Count} nodes, runways: {Escape(string.Join(", ", net.RunwayNames))} (north up)</text>""");

        foreach (var e in net.Edges.OrderBy(e => e.IsRunway ? 0 : 1))
        {
            bool stand = deadEnds.Contains(e.NodeA) || deadEnds.Contains(e.NodeB);
            (string color, double w) = e.IsRunway ? ("#3a3a3a", 9.0) : stand ? ("#e08a1e", 2.0) : ("#2f6fd0", 3.5);
            string points = string.Join(" ", e.Points.Select(p => $"{Sx(p)},{Sy(p)}"));
            sb.AppendLine($"""<polyline points="{points}" fill="none" stroke="{color}" stroke-width="{F(w)}" stroke-linecap="round" stroke-linejoin="round"><title>edge {e.Index}: {e.NodeA}:{e.PortA} → {e.NodeB}:{e.PortB}, {Escape(e.TypeName)}, {e.Length:F0} m</title></polyline>""");
        }

        foreach (var n in net.Nodes.Where(n => n.Ports.Count > 0))
        {
            var c = n.Center;
            string fill = n.Degree == 1 ? "#e08a1e" : n.Spread > 150 ? "#c0392b" : "#1d3f73";
            sb.AppendLine($"""<circle cx="{Sx(c)}" cy="{Sy(c)}" r="{(n.Degree == 1 ? 2.5 : 4)}" fill="{fill}"><title>node {n.Id}: degree {n.Degree}, {n.Ports.Count} ports, spread {n.Spread:F0} m</title></circle>""");
            if (labels && n.Degree > 1)
                sb.AppendLine($"""<text x="{Sx(c)}" y="{Sy(c)}" dx="5" dy="-5" font-size="11" fill="#1d3f73">{n.Id}</text>""");
        }
        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
