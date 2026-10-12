using System.Diagnostics;
using Fileman.Core;

namespace Fileman.App;

/// <summary>Kleine Hilfen für die Oberfläche.</summary>
internal static class Ui
{
    public static readonly Padding Gap = new(6);

    public static Button Button(string text, EventHandler onClick, bool autoSize = true)
    {
        var button = new Button { Text = text, AutoSize = autoSize, Margin = Gap, Padding = new Padding(8, 2, 8, 2) };
        button.Click += onClick;
        return button;
    }

    public static ListView DetailsList(params (string Title, int Width)[] columns)
    {
        var list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            Dock = DockStyle.Fill,
            GridLines = false,
        };
        foreach (var (title, width) in columns)
            list.Columns.Add(title, width);
        return list;
    }

    /// <summary>Öffnet eine Datei mit dem zugeordneten Standardprogramm.</summary>
    public static void OpenWithDefaultProgram(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// <summary>Zeigt die Datei im Explorer markiert an.</summary>
    public static void ShowInExplorer(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    /// <summary>
    /// Führt Arbeit im Hintergrund aus (Plattenzugriffe blockieren so nie die Oberfläche) und zeigt Fehler verständlich an.
    /// </summary>
    public static async Task<T?> RunAsync<T>(Control owner, Func<T> work)
    {
        var previous = owner.Cursor;
        owner.Cursor = Cursors.AppStarting;
        try
        {
            return await Task.Run(work);
        }
        catch (FilemanException ex)
        {
            MessageBox.Show(owner, ex.Message, "Fileman", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            FilemanLog.Error("Fehler bei einer Aktion in der Oberfläche.", ex);
            MessageBox.Show(owner, ex.Message, "Fileman", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return default;
        }
        finally
        {
            if (!owner.IsDisposed)
                owner.Cursor = previous;
        }
    }
}
