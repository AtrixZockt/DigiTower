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
