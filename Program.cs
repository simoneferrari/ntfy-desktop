using Velopack;

namespace NtfyDesktop;

// Custom entry point so the Velopack bootstrap runs before any WPF startup.
// App.xaml remains the ApplicationDefinition (and generates its own App.Main);
// <StartupObject> in the csproj points here, so this Main is the one used.
public static class Program
{
    [STAThread]
    public static void Main()
    {
        // Must be the very first thing the process does: when Windows launches us to
        // run an install / update / uninstall hook, VelopackApp handles it and exits
        // here — before the single-instance mutex, --data-path parsing, protocol
        // registration, or the host are ever touched. In a normal launch it returns
        // immediately and we start WPF as usual.
        VelopackApp.Build().Run();

        // Register the SQLCipher native provider for Microsoft.Data.Sqlite.Core before any
        // SqliteConnection is opened (HistoryRepository opens its first during host startup).
        // Idempotent; explicit so we never depend on bundle auto-init being present.
        SQLitePCL.Batteries_V2.Init();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
