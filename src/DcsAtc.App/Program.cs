using DcsAtc.App;
using DcsAtc.Bridge;

// DCS-ATC app. For now: spike S1, the bridge test.
//
//   dotnet run --project src/DcsAtc.App -- [--host 127.0.0.1] [--port 42700]
//                                           [--dump "Al Dhafra AFB"]... [--silence "Al Dhafra AFB"]...

string host = "127.0.0.1";
int port = 42700;
var dump = new List<string>();
var silence = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--host": host = Next(); break;
        case "--port": port = int.Parse(Next()); break;
        case "--dump": dump.Add(Next()); break;
        case "--silence": silence.Add(Next()); break;
        default:
            Console.Error.WriteLine($"unknown argument: {args[i]}");
            return 1;
    }
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await using var client = new BridgeClient(new BridgeOptions { Host = host, Port = port });
var demo = new BridgeDemo(client, new DemoOptions { DumpAirbases = dump, SilenceAirbases = silence });

Console.WriteLine($"DCS-ATC: waiting for the DCS bridge on {host}:{port} (Ctrl+C to quit)");
var connection = client.RunAsync(cts.Token);
try
{
    await demo.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
}
await connection;
return 0;
