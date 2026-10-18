using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DcsAtc.Bridge;

namespace DcsAtc.Tests;

/// <summary>
/// Runs the real Lua hook and mission script inside bridge/tests/fake_dcs.lua (Lua 5.1 + LuaSocket)
/// and talks to it with <see cref="BridgeClient"/>. Skipped when no Lua 5.1 is installed; set
/// DCSATC_LUA to the lua.exe to use.
/// </summary>
public class FakeDcsIntegrationTests
{
    [SkippableFact]
    public async Task Client_talks_to_the_real_hook_in_a_fake_dcs()
    {
        string? lua = TestPaths.FindLua();
        Skip.If(lua is null, "Lua 5.1 with LuaSocket not found (set DCSATC_LUA)");
        int port = FreePort();
        string script = Path.Combine(TestPaths.RepoRoot, "bridge", "tests", "fake_dcs.lua");
        using var fake = Process.Start(new ProcessStartInfo(lua!, $"\"{script}\" --port {port} --duration 40 --click --quiet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            WorkingDirectory = TestPaths.RepoRoot,
        })!;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var client = new BridgeClient(new BridgeOptions { Port = port, ReconnectDelay = TimeSpan.FromMilliseconds(200) });
            var run = client.RunAsync(cts.Token);

            bool missionRunning = false;
            UnitsMessage? units = null;
            MenuSelectedMessage? click = null;
            bool menuAdded = false;
            await foreach (var msg in client.Messages.ReadAllAsync(cts.Token))
            {
                switch (msg)
                {
                    case HelloMessage { MissionRunning: true }:
                    case MissionStartedMessage:
                        missionRunning = true;
                        break;
                    case UnitsMessage u when u.Units.Count == 3:
                        units = u;
                        if (!menuAdded)
                        {
                            menuAdded = true;
                            int group = u.Units.Single(x => x.Name == "Viper 1-1").GroupId!.Value;
                            await client.AddMenuAsync(group, ["ATC"], "Request taxi", "taxi", cts.Token);
                        }
                        break;
                    case MenuSelectedMessage m:
                        click = m;
                        break;
                }
                if (missionRunning && units is not null && click is not null) break;
            }

            Assert.Equal("taxi", click!.Id);
            Assert.Contains(units!.Units, u => u is { Name: "Dustoff 1-1", IsHelicopter: true, IsPlayer: true });

            var dhafra = await client.GetAirbaseAsync("Al Dhafra AFB", cts.Token);
            // The fake uses DCS's real Al Dhafra data: DCS names the parallel runways "13" and "31".
            Assert.Equal(["13", "31"], dhafra!.Runways.Select(r => r.Name));
            Assert.Equal(127.9, dhafra.Runways[0].TrueHeading, 1);
            var weather = await client.GetAirbaseWeatherAsync("Al Dhafra AFB", cts.Token);
            Assert.Equal(310, weather!.WindFrom, 3);
            Assert.True(await client.SetRadioSilentAsync("Al Dhafra AFB", true, cts.Token));
            await Assert.ThrowsAsync<BridgeCommandException>(() => client.GetAirbaseAsync("Nowhere", cts.Token));

            await cts.CancelAsync();
            await run;
        }
        finally
        {
            if (!fake.HasExited) fake.Kill();
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
