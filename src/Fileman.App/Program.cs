using Fileman.Core;

namespace Fileman.App;

internal static class Program
{
    internal const string InstanceMutexName = @"Local\Fileman.Tray";
    internal const string ExitEventName = @"Local\Fileman.Tray.Exit";

    [STAThread]
    private static void Main(string[] args)
    {
        // "Fileman.exe --exit" beendet eine laufende Instanz (wird vom Setup vor Update/Deinstallation aufgerufen).
        if (args.Any(a => a.Equals("--exit", StringComparison.OrdinalIgnoreCase)))
        {
            RequestExitOfRunningInstance();
            return;
        }

        // Nur eine Instanz pro Benutzer.
        using var mutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("Fileman läuft bereits.\n\nSie finden es als Symbol unten rechts im Infobereich der Taskleiste.",
                "Fileman", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowUnexpected(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            FilemanLog.Error("Unbehandelter Fehler (Anwendung wird beendet).", e.ExceptionObject as Exception);

        Application.Run(new TrayApplicationContext());
    }

    private static void RequestExitOfRunningInstance()
    {
        if (!EventWaitHandle.TryOpenExisting(ExitEventName, out var exitEvent))
            return; // läuft nicht
        using (exitEvent)
            exitEvent.Set();
        // Warten, bis der Prozess wirklich beendet ist und seine Dateien freigegeben hat (höchstens 30 Sekunden).
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        foreach (var other in System.Diagnostics.Process.GetProcessesByName(self.ProcessName))
        {
            using (other)
            {
                if (other.Id == self.Id || other.SessionId != self.SessionId)
                    continue;
                var remaining = deadline - DateTime.UtcNow;
                if (remaining > TimeSpan.Zero)
                    other.WaitForExit(remaining);
            }
        }
    }

    internal static void ShowUnexpected(Exception ex)
    {
        FilemanLog.Error("Unerwarteter Fehler in der Oberfläche.", ex);
        var text = ex is FilemanException ? ex.Message : "Es ist ein unerwarteter Fehler aufgetreten:\n\n" + ex.Message + "\n\nDetails stehen im Protokoll.";
        MessageBox.Show(text, "Fileman", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
