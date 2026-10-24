using System.Net.Sockets;

namespace DcsAtc.Host;

/// <summary>
/// Whether an SRS server answers at an address: a TCP connection opened and closed again every few seconds, unless
/// something else already tells (a connected controller). DigiTower joins the server; it does not start it, and a
/// pilot who only runs the SRS client otherwise sees no reason why nothing is heard.
/// </summary>
public sealed class SrsProbe : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<bool>? _known;

    /// <param name="server">"host:port" (the port defaults to SRS's 5002).</param>
    /// <param name="known">True when the server is known to answer (a controller is connected): no try then.</param>
    public SrsProbe(string server, Func<bool>? known = null)
    {
        (Host, Port) = Parse(server);
        _known = known;
        _ = RunAsync(_stop.Token);
    }

    public string Host { get; }
    public int Port { get; }
    public string Server => $"{Host}:{Port}";

    /// <summary>Null until the first try has an answer.</summary>
    public bool? Reachable { get; private set; }

    /// <summary>"127.0.0.1:5002" → (127.0.0.1, 5002); no port: 5002.</summary>
    public static (string Host, int Port) Parse(string server)
    {
        var parts = server.Split(':');
        return (parts[0].Trim(), parts.Length > 1 && int.TryParse(parts[1], out int port) ? port : 5002);
    }

    /// <summary>Opens a TCP connection to the address and closes it again; false if none within the timeout.</summary>
    public static async Task<bool> TryConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        using var tcp = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool answers = _known?.Invoke() == true || await TryConnectAsync(Host, Port, Timeout, ct);
            if (ct.IsCancellationRequested) break;
            Reachable = answers;
            try
            {
                await Task.Delay(Interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose() => _stop.Cancel();
}
