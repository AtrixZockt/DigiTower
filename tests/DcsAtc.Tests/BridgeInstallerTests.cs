using DcsAtc.Host;

namespace DcsAtc.Tests;

/// <summary>The control panel's bridge installer, on a made-up Saved Games folder.</summary>
public class BridgeInstallerTests : IDisposable
{
    private readonly string _savedGames = Path.Combine(Path.GetTempPath(), "digitower-tests", Guid.NewGuid().ToString("N"), "Saved Games");
    private readonly BridgeInstaller _installer = new(Path.Combine(TestPaths.RepoRoot, "bridge", "Scripts"));

    public BridgeInstallerTests() => Directory.CreateDirectory(_savedGames);

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_savedGames)!, true);

    private string Instance(string name = "DCS.dcs_serverrelease", string autoexec = "options.graphics.maxfps = 60\n")
    {
        string dir = Path.Combine(_savedGames, name);
        Directory.CreateDirectory(Path.Combine(dir, "Config"));
        if (autoexec.Length > 0) File.WriteAllText(Path.Combine(dir, "Config", "autoexec.cfg"), autoexec);
        return dir;
    }

    private static string Autoexec(string dcs) => File.ReadAllText(Path.Combine(dcs, "Config", "autoexec.cfg"));

    [Fact]
    public void Finds_the_dcs_folders_server_first()
    {
        Instance("DCS");
        Instance("DCS.dcs_serverrelease");
        Instance("DCS.openbeta");
        Directory.CreateDirectory(Path.Combine(_savedGames, "DCS.unused")); // never started: no Config
        Directory.CreateDirectory(Path.Combine(_savedGames, "Elite Dangerous", "Config"));

        var found = BridgeInstaller.FindInstances(_savedGames);
        Assert.Equal(["DCS server", "DCS", "DCS open beta"], found.Select(i => i.Name));
        Assert.Equal(Path.Combine(_savedGames, "DCS.dcs_serverrelease"), found[0].Path);
    }

    [Fact]
    public void Installs_keeps_the_settings_and_uninstalls()
    {
        string dcs = Instance();
        Assert.Equal(BridgeState.NotInstalled, _installer.Status(dcs).State);

        _installer.Install(dcs);
        var status = _installer.Status(dcs);
        Assert.Equal((BridgeState.Installed, new BridgeConfig()), (status.State, status.Config));
        Assert.True(File.Exists(Path.Combine(dcs, "Scripts", BridgeInstaller.HookFile)));
        Assert.True(File.Exists(Path.Combine(dcs, "Scripts", BridgeInstaller.MissionFile)));
        string autoexec = Autoexec(dcs);
        Assert.StartsWith("options.graphics.maxfps = 60\n", autoexec);
        Assert.Contains("table.insert(net.allow_dostring_in, \"mission\")\r\n-- <<< DigiTower bridge", autoexec);
        Assert.Equal("options.graphics.maxfps = 60\n", File.ReadAllText(Path.Combine(dcs, "Config", "autoexec.cfg.bak-digitower")));

        // Settings: changed in place, comments kept, and kept by the next install.
        string config = Path.Combine(dcs, "Scripts", BridgeInstaller.ConfigFile);
        BridgeInstaller.WriteConfig(config, new BridgeConfig(ManageAi: false, MapDrawings: true, Port: 42701));
        Assert.Contains("-- false: AI is left to DCS.", File.ReadAllText(config));
        Assert.Contains("manageAi = false,", File.ReadAllText(config));
        _installer.Install(dcs);
        Assert.Equal(new BridgeConfig(false, true, 42701), _installer.Status(dcs).Config);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(Autoexec(dcs), "-- >>> DigiTower bridge"));

        // A different hook (an older DigiTower) is an update.
        File.AppendAllText(Path.Combine(dcs, "Scripts", BridgeInstaller.HookFile), "\n-- older\n");
        Assert.Equal(BridgeState.UpdateAvailable, _installer.Status(dcs).State);
        _installer.Install(dcs, new BridgeConfig(ManageAi: true, MapDrawings: false));
        Assert.Equal((BridgeState.Installed, new BridgeConfig(true, false, 42700)), (_installer.Status(dcs).State, _installer.Status(dcs).Config));

        _installer.Uninstall(dcs);
        Assert.Equal(BridgeState.NotInstalled, _installer.Status(dcs).State);
        Assert.Equal("options.graphics.maxfps = 60\n", Autoexec(dcs));
        Assert.True(File.Exists(config)); // the settings stay
    }

    [Fact]
    public void Moves_an_old_dcs_atc_install_over()
    {
        string dcs = Instance(autoexec: "-- >>> DCS-ATC bridge (added by bridge\\install.ps1)\r\nnet = net or {}\r\n-- <<< DCS-ATC bridge\r\nother = 1\r\n");
        string scripts = Path.Combine(dcs, "Scripts");
        Directory.CreateDirectory(Path.Combine(scripts, "Hooks"));
        Directory.CreateDirectory(Path.Combine(scripts, "DcsAtc"));
        File.WriteAllText(Path.Combine(scripts, "Hooks", "DcsAtcBridgeHook.lua"), "-- old hook");
        File.WriteAllText(Path.Combine(scripts, "DcsAtc", "DcsAtcMission.lua"), "-- old mission");
        File.WriteAllText(Path.Combine(scripts, "DcsAtc", "DcsAtcConfig.lua"), "return {\n  manageAi = false,\n}\n");
        File.WriteAllText(Path.Combine(scripts, "Hooks", "SomeoneElse.lua"), "-- another mod");

        var before = _installer.Status(dcs);
        Assert.Equal(BridgeState.UpdateAvailable, before.State);
        Assert.False(before.Config.ManageAi is false); // read from the new place only
        Assert.Equal(["SomeoneElse.lua"], before.OtherHooks);

        _installer.Install(dcs);
        var after = _installer.Status(dcs);
        Assert.Equal(BridgeState.Installed, after.State);
        Assert.False(after.Config.ManageAi); // the old settings came along
        Assert.False(Directory.Exists(Path.Combine(scripts, "DcsAtc")));
        Assert.False(File.Exists(Path.Combine(scripts, "Hooks", "DcsAtcBridgeHook.lua")));
        string autoexec = Autoexec(dcs);
        Assert.DoesNotContain("DCS-ATC", autoexec);
        Assert.StartsWith("other = 1\r\n-- >>> DigiTower bridge", autoexec);
    }

    [Fact]
    public void Install_without_an_autoexec_creates_it()
    {
        string dcs = Instance("DCS", autoexec: "");
        _installer.Install(dcs);
        Assert.StartsWith("-- >>> DigiTower bridge", Autoexec(dcs));
        Assert.False(File.Exists(Path.Combine(dcs, "Config", "autoexec.cfg.bak-digitower")));
        Assert.Equal(BridgeState.Installed, _installer.Status(dcs).State);
    }
}
