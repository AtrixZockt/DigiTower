using Velopack;

namespace DcsAtc.Gui;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // First: the installer and updates call DigiTower.exe to set up shortcuts and finish an update, and expect it
        // to return at once.
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
