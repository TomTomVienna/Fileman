using Fileman.Core;

namespace Fileman.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Nur eine Instanz pro Benutzer.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\Fileman.Tray", out var isFirstInstance);
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

    internal static void ShowUnexpected(Exception ex)
    {
        FilemanLog.Error("Unerwarteter Fehler in der Oberfläche.", ex);
        var text = ex is FilemanException ? ex.Message : "Es ist ein unerwarteter Fehler aufgetreten:\n\n" + ex.Message + "\n\nDetails stehen im Protokoll.";
        MessageBox.Show(text, "Fileman", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
