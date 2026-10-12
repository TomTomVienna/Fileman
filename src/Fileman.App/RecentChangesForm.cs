using Fileman.Core;
using Fileman.Core.Automation;
using Fileman.Core.Catalog;

namespace Fileman.App;

/// <summary>Kleines Fenster über dem Infobereich mit den letzten Änderungen.</summary>
internal sealed class RecentChangesForm : Form
{
    private const int MaxEntries = 100;

    private readonly FilemanWorkspace _workspace;
    private readonly ListView _list;
    private readonly Label _status;
    private readonly Label _source;

    public RecentChangesForm(FilemanWorkspace workspace)
    {
        _workspace = workspace;
        Text = "Fileman – Letzte Änderungen";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(720, 400);
        MinimumSize = new Size(420, 240);
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
                Hide();
        };

        _status = new Label { AutoSize = true, Margin = Ui.Gap, Font = new Font(Font, FontStyle.Bold) };
        _source = new Label { AutoSize = true, Margin = Ui.Gap, ForeColor = SystemColors.GrayText };
        _list = Ui.DetailsList(("Zeit", 110), ("Art", 90), ("Datei", 220), ("Ordner", 280));
        _list.ItemActivate += (_, _) =>
        {
            if (_list.SelectedItems.Count == 1 && _list.SelectedItems[0].Tag is long id)
                OpenDocumentRequested?.Invoke(id);
        };

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(Ui.Button("Archiv durchsuchen …", (_, _) => BrowseRequested?.Invoke()));
        buttons.Controls.Add(Ui.Button("Jetzt sichern", (_, _) => BackupNowRequested?.Invoke()));
        buttons.Controls.Add(Ui.Button("Aktualisieren", async (_, _) => await RefreshAsync()));

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_status, 0, 0);
        layout.Controls.Add(_list, 0, 1);
        layout.Controls.Add(_source, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);

        // Wie ein Popup: verschwindet, sobald man woanders hinklickt.
        Deactivate += (_, _) => Hide();
    }

    public event Action<long>? OpenDocumentRequested;
    public event Action? BackupNowRequested;
    public event Action? BrowseRequested;

    public void UpdateState(ServiceState state)
    {
        var text = DisplayText.Status(state.Status);
        if (state.Message is { Length: > 0 } message && message != text)
            text += " – " + message;
        if (state.LastBackupUtc is { } last)
            text += $"   (letzte Sicherung {DisplayText.Time(last)})";
        _status.Text = text;
        _status.ForeColor = StatusIcons.ColorFor(state.Status);
    }

    public async Task ShowNearTrayAsync()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Right - Width - 8, area.Bottom - Height - 8);
        Show();
        Activate();
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        var data = await Task.Run(() =>
        {
            try
            {
                using var view = _workspace.OpenCatalogForReading();
                return (Events: view.Catalog.GetRecentEvents(MaxEntries), view.IsCache, Error: (string?)null);
            }
            catch (Exception ex) when (ex is FilemanException or IOException or Microsoft.Data.Sqlite.SqliteException)
            {
                return (Events: new List<ChangeEvent>(), IsCache: false, Error: ex is TargetNotConnectedException ? "Noch keine Sicherung vorhanden." : ex.Message);
            }
        });
        if (IsDisposed)
            return;

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in data.Events)
        {
            var item = new ListViewItem(DisplayText.Time(e.Utc)) { Tag = e.DocumentId, ForeColor = KindColor(e.Kind) };
            item.SubItems.Add(DisplayText.Kind(e.Kind));
            item.SubItems.Add(Path.GetFileName(e.Path));
            item.SubItems.Add(FolderText(e));
            item.ToolTipText = e.OldPath is null ? e.Path : $"vorher: {e.OldPath}\njetzt: {e.Path}";
            _list.Items.Add(item);
        }
        _list.ShowItemToolTips = true;
        _list.EndUpdate();
        _source.Text = data.Error
            ?? (data.IsCache ? "Anzeige aus der lokalen Kopie – die Sicherungsplatte ist nicht angesteckt." : "Doppelklick öffnet die Versionen eines Eintrags.");
    }

    /// <summary>Ordner; bei Umbenennungen der frühere Name, bei Verschiebungen der frühere Ordner.</summary>
    private static string FolderText(ChangeEvent e)
    {
        var folder = Path.GetDirectoryName(e.Path) ?? "";
        if (e.OldPath is null)
            return folder;
        var oldFolder = Path.GetDirectoryName(e.OldPath) ?? "";
        return string.Equals(oldFolder, folder, StringComparison.OrdinalIgnoreCase)
            ? $"{folder}   (vorher: {Path.GetFileName(e.OldPath)})"
            : $"{folder}   (vorher in: {oldFolder})";
    }

    private static Color KindColor(ChangeKind kind) => kind switch
    {
        ChangeKind.Created or ChangeKind.Recreated => Color.FromArgb(26, 127, 55),
        ChangeKind.Deleted => Color.FromArgb(160, 30, 40),
        ChangeKind.Moved or ChangeKind.Renamed => Color.FromArgb(31, 90, 190),
        _ => SystemColors.WindowText,
    };

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Schließen = nur ausblenden; das Fenster wird wiederverwendet.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }
}
