using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DcsAtc.Bridge;

namespace DcsAtc.Tests;

public class BridgeClientTests
{
    [Fact]
    public async Task Parses_messages_and_matches_replies_over_tcp()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = new BridgeClient(new BridgeOptions { Port = port, ReconnectDelay = TimeSpan.FromMilliseconds(100) });
        var run = client.RunAsync(cts.Token);

        using var server = await listener.AcceptTcpClientAsync(cts.Token);
        var stream = server.GetStream();
        using var reader = new StreamReader(stream);
        await using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" };

        await writer.WriteLineAsync("""{"type":"hello","protocol":1,"mission":"Test","missionRunning":true,"info":{"theatre":"PersianGulf","date":{"Year":2026,"Month":10,"Day":18}},"multiplayer":false,"manageAi":false}""");
        await writer.WriteLineAsync("""{"type":"poll","t":12.5,"msgs":[{"type":"event","ev":"takeoff","t":12.4,"unit":"Viper 1-1","groupId":3,"place":"Al Dhafra AFB"},{"type":"menu","groupId":3,"id":"taxi","t":12.45}],"units":[{"id":1001,"name":"Viper 1-1","type":"F-16C_50","cat":"plane","coal":2,"groupId":3,"player":"DiGii","x":-211800,"y":40,"z":-173400,"vx":3,"vy":0,"vz":4,"hdg":53.1,"agl":24,"inAir":true}]}""");

        var messages = new List<BridgeMessage>();
        while (messages.Count < 5) messages.Add(await client.Messages.ReadAsync(cts.Token));

        Assert.Equal(new ConnectionChangedMessage(true), messages[0]);
        var hello = Assert.IsType<HelloMessage>(messages[1]);
        Assert.True(hello.MissionRunning);
        Assert.Equal("PersianGulf", hello.Info?.Theatre);
        Assert.Equal(new MissionDate(2026, 10, 18), hello.Info?.Date);
        Assert.False(hello.ManageAi);
        var ev = Assert.IsType<DcsEventMessage>(messages[2]);
        Assert.Equal(("takeoff", "Viper 1-1", "Al Dhafra AFB", 3), (ev.Event, ev.Unit, ev.Place, ev.GroupId));
        var menu = Assert.IsType<MenuSelectedMessage>(messages[3]);
        Assert.Equal((3, "taxi"), (menu.GroupId, menu.Id));
        var units = Assert.IsType<UnitsMessage>(messages[4]);
        var viper = Assert.Single(units.Units);
        Assert.True(viper.IsPlayer);
        Assert.False(viper.IsHelicopter);
        Assert.Equal(5.0, viper.GroundSpeed, 6);

        // A request is answered by a reply with the same id.
        var ping = client.PingAsync(cts.Token);
        var sent = JsonDocument.Parse((await reader.ReadLineAsync(cts.Token))!).RootElement;
        Assert.Equal("ping", sent.GetProperty("cmd").GetString());
        int req = sent.GetProperty("req").GetInt32();
        await writer.WriteLineAsync($$$"""{"type":"poll","t":13,"msgs":[{"type":"reply","req":{{{req}}},"cmd":"ping","ok":true,"data":{"pong":true,"t":13}}]}""");
        Assert.True((await ping)!.Value.GetProperty("pong").GetBoolean());

        // A failed command surfaces as an exception.
        var silence = client.SetRadioSilentAsync("Nowhere", true, cts.Token);
        sent = JsonDocument.Parse((await reader.ReadLineAsync(cts.Token))!).RootElement;
        Assert.Equal(("radio_silent", "Nowhere", true), (sent.GetProperty("cmd").GetString(), sent.GetProperty("name").GetString(), sent.GetProperty("silent").GetBoolean()));
        await writer.WriteLineAsync($$"""{"type":"reply","req":{{sent.GetProperty("req").GetInt32()}},"cmd":"radio_silent","ok":false,"error":"airbase not found: Nowhere"}""");
        var error = await Assert.ThrowsAsync<BridgeCommandException>(() => silence);
        Assert.Contains("not found", error.Message);

        // Menu commands carry the path as an array.
        await client.AddMenuAsync(3, ["ATC", "Ground"], "Request taxi", "taxi", cts.Token);
        sent = JsonDocument.Parse((await reader.ReadLineAsync(cts.Token))!).RootElement;
        Assert.Equal(["ATC", "Ground"], sent.GetProperty("path").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(3, sent.GetProperty("groupId").GetInt32());

        // Losing the connection fails pending requests and is reported.
        var pending = client.PingAsync(cts.Token);
        await reader.ReadLineAsync(cts.Token);
        server.Close();
        await Assert.ThrowsAsync<BridgeCommandException>(() => pending);
        BridgeMessage next;
        do next = await client.Messages.ReadAsync(cts.Token);
        while (next is not ConnectionChangedMessage);
        Assert.False(((ConnectionChangedMessage)next).Connected);

        await cts.CancelAsync();
        await run;
        listener.Stop();
    }
}
