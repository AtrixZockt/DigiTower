using System.Text.Json;
using DcsAtc.Airfields;
using DcsAtc.Airfields.Rn5;
using DcsAtc.Bridge;

// Reads DCS taxi network files (.rn5).
//
//   rn5tool info <file.rn5>
//   rn5tool svg  <file.rn5> <out.svg> [pixels-per-metre]
//   rn5tool json <file.rn5> <out.json>
//   rn5tool scan <DCS terrains folder>      e.g. "D:\DCS\DCS World\Mods\terrains"

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: rn5tool info|svg|json <file.rn5> [out] | rn5tool scan <terrains folder>");
    return 1;
}

switch (args[0])
{
    case "info":
        PrintInfo(Rn5Reader.Read(args[1]));
        return 0;

    case "svg" when args.Length >= 3:
        var net = Rn5Reader.Read(args[1]);
        double scale = args.Length >= 4 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 0.5;
        File.WriteAllText(args[2], Rn5Svg.Render(net, scale));
        Console.WriteLine($"wrote {args[2]}");
        return 0;

    case "json" when args.Length >= 3:
        var n = Rn5Reader.Read(args[1]);
        var export = new
        {
            n.Name,
            n.EdgeTypes,
            n.RunwayNames,
            Nodes = n.Nodes.Select(x => new { x.Id, x.Degree, Ports = x.Ports.ToDictionary(p => p.Key, p => new { p.Value.X, p.Value.Z }), x.EdgeIndices }),
            Edges = n.Edges.Select(e => new { e.Index, e.NodeA, e.PortA, e.NodeB, e.PortB, Type = e.TypeName, Length = Math.Round(e.Length, 1), Points = e.Points.Select(p => new[] { Math.Round(p.X, 2), Math.Round(p.Z, 2) }) }),
        };
        File.WriteAllText(args[2], JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"wrote {args[2]}");
        return 0;

    case "scan":
        return Scan(args[1]);

    case "airfield":
        {
            // rn5tool airfield <file.rn5> [dcs-airbase.json] [out.svg]
            var airfield = LoadAirfield(args[1], args.Length >= 3 ? args[2] : null);
            Console.WriteLine($"{airfield.Name}: {airfield.Graph.Vertices.Count} vertices, {airfield.Graph.Links.Count} links, {airfield.Parking.Count} DCS parking spots");
            if (airfield.Labels is { } l)
            {
                Console.WriteLine($"  named edges: {airfield.TaxiwayNames.Count} ({string.Join(" ", airfield.TaxiwayNames.Values.Distinct().Order())}); names not placed: {string.Join(" ", l.Taxiways.Select(t => t.Name).Distinct().Except(airfield.TaxiwayNames.Values))}");
                foreach (var w in airfield.LabelWarnings) Console.WriteLine($"  warning: {w}");
            }
            foreach (var r in airfield.Runways)
            {
                Console.WriteLine($"  runway {r.Name}: {r.Length:F0} m, heading {r.EndA.TrueHeading:F1}°T / {r.EndB.TrueHeading:F1}°T, {r.HoldPoints.Count} holding points");
                Console.WriteLine($"    {r.EndA.Name} end x={r.EndA.Threshold.X:F1} z={r.EndA.Threshold.Z:F1}   {r.EndB.Name} end x={r.EndB.Threshold.X:F1} z={r.EndB.Threshold.Z:F1}");
            }
            if (args.Length >= 4)
            {
                File.WriteAllText(args[3], Rn5Svg.Render(airfield.Network, 0.5, labels: false, new SvgOverlay
                {
                    Runways = airfield.Runways,
                    Parking = airfield.Parking,
                    EdgeNames = airfield.TaxiwayNames,
                    Labels = airfield.Labels?.Taxiways,
                }));
                Console.WriteLine($"wrote {args[3]}");
            }
            return 0;
        }

    case "route" when args.Length >= 5:
        {
            // rn5tool route <file.rn5> <dcs-airbase.json> <parking index> <runway end> [out.svg]
            var airfield = LoadAirfield(args[1], args[2]);
            var spot = airfield.Parking.FirstOrDefault(p => p.Index == int.Parse(args[3]));
            if (spot is null)
            {
                Console.Error.WriteLine($"no parking spot {args[3]}");
                return 1;
            }
            var departure = airfield.RouteToRunway(spot.Position, args[4]);
            if (departure is null)
            {
                Console.Error.WriteLine($"no route to runway {args[4]}");
                return 1;
            }
            var route = departure.Route;
            var crossed = route.CrossingNodes.Select(n => airfield.Runways.First(r => r.Nodes.Contains(n)).Name);
            double fromThreshold = departure.End == departure.Runway.EndA ? departure.Hold.AlongRunway : departure.Runway.Length - departure.Hold.AlongRunway;
            Console.WriteLine($"parking {spot.Index} -> holding point runway {departure.End.Name}: {route.Length:F0} m, {route.Steps.Count} links");
            Console.WriteLine($"  holding point {fromThreshold:F0} m from the {departure.End.Name} threshold");
            var via = new List<string>();
            foreach (var s in route.Steps)
                if (airfield.NameOf(s.Link) is { } nm && (via.Count == 0 || via[^1] != nm)) via.Add(nm);
            Console.WriteLine($"  via (raw): {string.Join(", ", via)}");
            Console.WriteLine($"  via: {string.Join(", ", airfield.Via(route).Select(v => v.IsRunwayCrossing ? $"cross runway {v.Name}" : v.Name))}");
            Console.WriteLine($"  runway crossings: {(route.CrossingNodes.Count == 0 ? "none" : string.Join(", ", crossed))}; rolls on a runway: {route.UsesRunway}");
            if (args.Contains("--steps"))
            {
                foreach (var s in route.Steps)
                {
                    var (f, t) = (airfield.Graph.Vertices[s.From], airfield.Graph.Vertices[s.To]);
                    var pts = s.Link.PointsFrom(s.From);
                    double h0 = (pts[1] - pts[0]).Heading, h1 = (pts[^1] - pts[^2]).Heading;
                    Console.WriteLine($"    {s.Link.Kind,-8} {f.Node}:{f.Port} -> {t.Node}:{t.Port}  {s.Link.Length,6:F0} m  rn5 edge {s.Link.Rn5Edge}  from ({f.Position.X:F0},{f.Position.Z:F0}) to ({t.Position.X:F0},{t.Position.Z:F0})  hdg {h0:000}->{h1:000}");
                }
            }
            if (args.Length >= 6 && args[5] != "--steps")
            {
                var overlay = new SvgOverlay
                {
                    Runways = airfield.Runways,
                    Parking = airfield.Parking,
                    Route = route.Geometry,
                    EdgeNames = airfield.TaxiwayNames,
                    Caption = $"{airfield.Name}: parking {spot.Index} → holding point {departure.End.Name}, {route.Length:F0} m",
                };
                File.WriteAllText(args[5], Rn5Svg.Render(airfield.Network, 0.5, labels: false, overlay));
                Console.WriteLine($"wrote {args[5]}");
            }
            return 0;
        }

    case "overlay" when args.Length >= 4:
        {
            // rn5tool overlay <file.rn5> <chart.png> <out.svg> [centre x] [centre z] [size m] [px per m]
            var airfield = LoadAirfield(args[1], null);
            var geo = airfield.Labels?.Georeference ?? throw new InvalidOperationException("the labels file has no georeference");
            var bytes = File.ReadAllBytes(args[2]);
            int w = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)), h = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20));
            var chart = new ChartImage("data:image/png;base64," + Convert.ToBase64String(bytes), w, h, geo.Affine());
            (double, double, double, double)? window = null;
            double ppm = 0.5;
            if (args.Length >= 7 && !args[4].StartsWith("--"))
            {
                double cx = double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture), cz = double.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture), size = double.Parse(args[6], System.Globalization.CultureInfo.InvariantCulture);
                window = (cx - size / 2, cx + size / 2, cz - size / 2, cz + size / 2);
                ppm = args.Length >= 8 && !args[7].StartsWith("--") ? double.Parse(args[7], System.Globalization.CultureInfo.InvariantCulture) : 1.0;
            }
            File.WriteAllText(args[3], Rn5Svg.Render(airfield.Network, ppm, labels: false, new SvgOverlay
            {
                Runways = airfield.Runways,
                Chart = chart,
                Grid = window is null ? 500 : 100,
                Window = window,
                EdgeNames = args.Contains("--names") ? airfield.TaxiwayNames : null,
                Caption = $"{airfield.Name} with {Path.GetFileName(args[2])} (georeferenced)",
            }));
            Console.WriteLine($"wrote {args[3]}");
            return 0;
        }

    case "names":
        {
            // rn5tool names <file.rn5>: the taxiway name of every named edge, "edge<TAB>name" (for comparing labels files)
            var airfield = LoadAirfield(args[1], null);
            foreach (var (edge, name) in airfield.TaxiwayNames.OrderBy(e => e.Key)) Console.WriteLine($"{edge}\t{name}");
            return 0;
        }

    case "lists":
        foreach (var (offset, count, header) in Rn5Reader.FindPointLists(File.ReadAllBytes(args[1])))
            Console.WriteLine($"0x{offset:x6}  n={count,4}  header {string.Join(" ", header)}");
        return 0;

    default:
        Console.Error.WriteLine($"unknown command: {args[0]}");
        return 1;
}

static Airfield LoadAirfield(string rn5, string? dcsJson)
{
    var net = Rn5Reader.Read(rn5);
    // Chart labels, if this airfield has been labelled: data/airfields/<theatre>/<rn5 name>.labels.json
    string? labelsPath = Directory.Exists(Path.Combine("data", "airfields"))
        ? Directory.EnumerateFiles(Path.Combine("data", "airfields"), $"{net.Name}.labels.json", SearchOption.AllDirectories).FirstOrDefault()
        : null;
    var labels = labelsPath is null ? null : AirfieldLabels.Load(labelsPath);
    if (labels is not null) Console.WriteLine($"labels: {labelsPath}");
    if (dcsJson is null) return Airfield.Build(net, labels: labels);
    var info = JsonSerializer.Deserialize<AirbaseInfo>(File.ReadAllText(dcsJson), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    return Airfield.Build(net,
        info.Runways.Select(r => r.Name).OfType<string>(),
        info.Parking.Select(p => new ParkingSpot(p.Index, p.TermType, new Vec2(p.X, p.Z))),
        labels);
}

static void PrintInfo(Rn5Network net)
{
    Console.WriteLine($"{net.Name}: {net.Edges.Count} edges, {net.Nodes.Count} nodes, runways: {string.Join(", ", net.RunwayNames)}");
    Console.WriteLine($"  edge types: {string.Join(", ", net.Edges.GroupBy(e => e.TypeName).Select(g => $"{g.Key} ×{g.Count()}"))}");
    Console.WriteLine($"  other types: {string.Join(", ", net.NodeTypes)}");
    Console.WriteLine($"  dead ends (stands): {net.Nodes.Count(n => n.Degree == 1)}, junctions: {net.Nodes.Count(n => n.Degree > 1 && n.Spread <= 150)}, long nodes (>150 m): {net.Nodes.Count(n => n.Spread > 150)}");
    foreach (var node in net.Nodes.Where(n => n.Spread > 150))
    {
        var types = node.EdgeIndices.Select(i => net.Edges[i].TypeName).Distinct();
        Console.WriteLine($"    node {node.Id}: {node.Ports.Count} ports, spread {node.Spread:F0} m, edges: {string.Join(", ", types)}");
    }
}

static int Scan(string terrains)
{
    int ok = 0, failed = 0;
    foreach (var file in Directory.EnumerateFiles(terrains, "*.rn5", SearchOption.AllDirectories).Order())
    {
        string map = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(file)))!;
        try
        {
            var net = Rn5Reader.Read(file);
            ok++;
            Console.WriteLine($"ok    {map,-16} {net.Name,-40} edges {net.Edges.Count,5}  nodes {net.Nodes.Count,5}  runways {string.Join(" ", net.RunwayNames)}");
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException)
        {
            failed++;
            Console.WriteLine($"FAIL  {map,-16} {Path.GetFileNameWithoutExtension(file),-40} {ex.Message}");
        }
    }
    Console.WriteLine($"\n{ok} parsed, {failed} failed");
    return failed == 0 ? 0 : 2;
}
