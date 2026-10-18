namespace DcsAtc.Tests;

internal static class TestPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>The DCS terrains folder, from DCSATC_DCS_DIR or the default install location; null if absent.</summary>
    public static string? Terrains
    {
        get
        {
            string dcs = Environment.GetEnvironmentVariable("DCSATC_DCS_DIR") ?? @"D:\DCS\DCS World";
            string terrains = Path.Combine(dcs, "Mods", "terrains");
            return Directory.Exists(terrains) ? terrains : null;
        }
    }

    /// <summary>Al Dhafra's layout from the DCS taxi network and the airbase dump; null if either is missing.</summary>
    public static DcsAtc.Airfields.Airfield? AlDhafra()
    {
        if (Terrains is null) return null;
        string rn5 = Path.Combine(Terrains, "PersianGulf", "AirfieldsTaxiways", "Dhafra_AFB.rn5");
        string dump = Path.Combine(RepoRoot, "data", "dcs", "Al Dhafra AFB.json");
        if (!File.Exists(rn5) || !File.Exists(dump)) return null;
        var info = System.Text.Json.JsonSerializer.Deserialize<DcsAtc.Bridge.AirbaseInfo>(File.ReadAllText(dump),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return DcsAtc.Airfields.Airfield.Build(DcsAtc.Airfields.Rn5.Rn5Reader.Read(rn5), info.Runways.Select(r => r.Name!),
            info.Parking.Select(p => new DcsAtc.Airfields.ParkingSpot(p.Index, p.TermType, new DcsAtc.Airfields.Vec2(p.X, p.Z))));
    }

    /// <summary>A Lua 5.1 interpreter with LuaSocket, from DCSATC_LUA or the Lua for Windows default; null if absent.</summary>
    public static string? FindLua()
    {
        foreach (var candidate in new[] { Environment.GetEnvironmentVariable("DCSATC_LUA"), @"C:\Program Files (x86)\Lua\5.1\lua.exe" })
        {
            if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DcsAtc.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException("Repository root (DcsAtc.slnx) not found.");
    }
}
