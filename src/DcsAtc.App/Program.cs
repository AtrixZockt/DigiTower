using DcsAtc.App;
using DcsAtc.Bridge;

// DCS-ATC app.
//
//   dotnet run --project src/DcsAtc.App -- [--host 127.0.0.1] [--port 42700]
//       [--dcs "D:\DCS\DCS World"]   DCS install (for the taxi networks); default: DCSATC_DCS_DIR or common locations
//       [--data <dir>]               folder containing airfields/<theatre>/*.labels.json; default: the repository's data folder
//       [--dump "Al Dhafra AFB"]...  write DCS airbase data to data/dcs/<name>.json
//       [--silence "<airbase>"]...   silence the stock ATC at more airbases
//       [--keep-stock-atc]           do not silence the stock ATC where we provide ground control

string host = "127.0.0.1";
int port = 42700;
string? dcsDir = Environment.GetEnvironmentVariable("DCSATC_DCS_DIR");
string? dataDir = null;
bool keepStockAtc = false;
var dump = new List<string>();
var silence = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--host": host = Next(); break;
        case "--port": port = int.Parse(Next()); break;
        case "--dcs": dcsDir = Next(); break;
        case "--data": dataDir = Next(); break;
        case "--dump": dump.Add(Next()); break;
        case "--silence": silence.Add(Next()); break;
        case "--keep-stock-atc": keepStockAtc = true; break;
        default:
            Console.Error.WriteLine($"unknown argument: {args[i]}");
            return 1;
    }
}

dcsDir ??= new[] { @"D:\DCS\DCS World", @"C:\Program Files\Eagle Dynamics\DCS World", @"C:\Program Files\Eagle Dynamics\DCS World OpenBeta" }
    .FirstOrDefault(d => Directory.Exists(Path.Combine(d, "Mods", "terrains")));
dataDir ??= FindDataDirectory();
Console.WriteLine($"DCS install: {dcsDir ?? "not found (use --dcs)"}");
Console.WriteLine($"airfield data: {dataDir ?? "not found (use --data)"}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await using var client = new BridgeClient(new BridgeOptions { Host = host, Port = port });
var app = new AtcApp(client, new AppOptions
{
    DcsDirectory = dcsDir,
    DataDirectory = dataDir,
    DumpAirbases = dump,
    SilenceAirbases = silence,
    KeepStockAtc = keepStockAtc,
});

Console.WriteLine($"DCS-ATC: waiting for the DCS bridge on {host}:{port} (Ctrl+C to quit)");
var connection = client.RunAsync(cts.Token);
try
{
    await app.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
}
await connection;
return 0;

// The repository's data folder: look upwards from the working directory and from the app itself.
static string? FindDataDirectory()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "data");
            if (Directory.Exists(Path.Combine(candidate, "airfields"))) return candidate;
        }
    }
    return null;
}
