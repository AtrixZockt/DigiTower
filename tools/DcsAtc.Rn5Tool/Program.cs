using System.Text.Json;
using DcsAtc.Airfields.Rn5;

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

    case "lists":
        foreach (var (offset, count, header) in Rn5Reader.FindPointLists(File.ReadAllBytes(args[1])))
            Console.WriteLine($"0x{offset:x6}  n={count,4}  header {string.Join(" ", header)}");
        return 0;

    default:
        Console.Error.WriteLine($"unknown command: {args[0]}");
        return 1;
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
