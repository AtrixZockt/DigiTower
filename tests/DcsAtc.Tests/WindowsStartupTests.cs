using System.Runtime.Versioning;
using DcsAtc.Host;
using Microsoft.Win32;

namespace DcsAtc.Tests;

/// <summary>"Start with Windows", on a scratch registry key (never the real Run key).</summary>
[SupportedOSPlatform("windows")]
public class WindowsStartupTests : IDisposable
{
    private readonly string _key = $@"Software\DigiTowerTests\{Guid.NewGuid():N}";

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(@"Software\DigiTowerTests", throwOnMissingSubKey: false);

    [Fact]
    public void Enables_and_disables_the_sign_in_entry()
    {
        var startup = new WindowsStartup(_key);
        Assert.False(startup.IsEnabled);

        startup.Enable(@"C:\Users\pilot\AppData\Local\DigiTower\DigiTower.exe");
        Assert.True(startup.IsEnabled);
        Assert.Equal("\"C:\\Users\\pilot\\AppData\\Local\\DigiTower\\DigiTower.exe\" --minimized", startup.Command);

        startup.Enable(@"D:\DigiTower\DigiTower.exe", @"D:\test settings.json");
        Assert.Equal("\"D:\\DigiTower\\DigiTower.exe\" --minimized --settings \"D:\\test settings.json\"", startup.Command);

        startup.Disable();
        Assert.False(startup.IsEnabled);
        startup.Disable(); // already off: fine
    }

    [Fact]
    public void An_installed_copy_starts_through_its_launcher()
    {
        string root = @"C:\Users\pilot\AppData\Local\DigiTower";
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $@"{root}\Update.exe", $@"{root}\DigiTower.exe" };

        Assert.Equal($@"{root}\DigiTower.exe", WindowsStartup.LauncherFor($@"{root}\current\DigiTower.exe", installed.Contains));
        // Built from the repository, or unzipped: the program itself.
        Assert.Equal(@"E:\src\bin\DigiTower.exe", WindowsStartup.LauncherFor(@"E:\src\bin\DigiTower.exe", installed.Contains));
        Assert.Equal(@"D:\x\current\DigiTower.exe", WindowsStartup.LauncherFor(@"D:\x\current\DigiTower.exe", installed.Contains));
    }
}
