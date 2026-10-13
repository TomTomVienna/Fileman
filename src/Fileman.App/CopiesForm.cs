using Fileman.Core;
using Fileman.Core.Copies;

namespace Fileman.App;

/// <summary>
/// Kopien in den gesicherten Ordnern: zeigen, einordnen und auf Wunsch die Geschichte mit dem Original zusammenführen.
/// An den Ordnern des Anwenders wird nichts verändert.
/// </summary>
internal sealed class CopiesForm : Form
{
    private readonly FilemanWorkspace _workspace;
    private readonly ListView _list;
    private readonly Label _info;
    private readonly Button _merge;
    private readonly Button _ignore;
    private readonly Button _show;

    public CopiesForm(FilemanWorkspace workspace)
    {
        _workspace = workspace;
        Text = "Fileman – Kopien prüfen";
        Size = new Size(1100, 560);
        MinimumSize = new Size(760, 400);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = StatusIcons.For(Core.Automation.ServiceStatus.Idle);

        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1040, 0),
            Margin = Ui.Gap,
            Text = "Diese Ordner sehen aus wie Kopien anderer Ordner (gleiche Struktur, gleiche Inhalte). „Geschichte zusammenführen“ trägt " +
                   "die Stände der Kopie als ältere Versionen beim Original ein – danach findet man alles beim Original. An Ihren Ordnern " +
                   "ändert Fileman nichts; die Kopie können Sie anschließend selbst löschen, wenn Sie sie nicht mehr brauchen. " +
                   "Ordner, in denen bewusst weitergearbeitet wird, bitte „Nicht mehr vorschlagen“.",
        };
        _list = Ui.DetailsList(("Kopie", 300), ("Original", 270), ("Einschätzung", 175), ("Dateien", 55), ("gleich", 50), ("älter", 50),
            ("neuer", 50), ("nur Kopie", 70));
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _info = new Label { AutoSize = true, Margin = Ui.Gap, ForeColor = SystemColors.GrayText };

        _merge = Ui.Button("Geschichte zusammenführen", async (_, _) => await MergeAsync());
        _ignore = Ui.Button("Nicht mehr vorschlagen", (_, _) => Ignore());
        _show = Ui.Button("Kopie im Explorer zeigen", (_, _) =>
        {
            if (Selected is { } c)
                Ui.OpenWithDefaultProgram(c.CopyFolder);
        });
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([_merge, _ignore, _show, Ui.Button("Erneut suchen", async (_, _) => await RefreshAsync()), Ui.Button("Schließen", (_, _) => Close())]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(intro, 0, 0);
        layout.Controls.Add(_list, 0, 1);
        layout.Controls.Add(_info, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        Load += async (_, _) => await RefreshAsync();
    }

    private CopyCandidate? Selected => _list.SelectedItems.Count == 1 ? (CopyCandidate)_list.SelectedItems[0].Tag! : null;

    private void UpdateButtons()
    {
        _merge.Enabled = _ignore.Enabled = _show.Enabled = Selected is not null;
    }

    public async Task RefreshAsync()
    {
        _info.Text = "Suche läuft …";
        var candidates = await Ui.RunAsync(this, () => _workspace.DetectCopies());
        if (IsDisposed || candidates is null)
            return;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var c in candidates)
        {
            var item = new ListViewItem(c.CopyFolder) { Tag = c, ToolTipText = $"jüngster Stand der Kopie: {DisplayText.Time(c.CopyNewestUtc)}\njüngster Stand des Originals: {DisplayText.Time(c.OriginalNewestUtc)}" };
            item.SubItems.Add(c.OriginalFolder);
            item.SubItems.Add(DisplayText.Assessment(c.Assessment));
            item.SubItems.Add(c.CopyFiles.ToString("N0"));
            item.SubItems.Add(c.Identical.ToString("N0"));
            item.SubItems.Add(c.OlderInCopy.ToString("N0"));
            item.SubItems.Add(c.NewerInCopy.ToString("N0"));
            item.SubItems.Add(c.OnlyInCopy.ToString("N0"));
            if (c.Assessment == CopyAssessment.EditedSince)
                item.ForeColor = StatusIcons.ColorFor(Core.Automation.ServiceStatus.TargetMissing);
            _list.Items.Add(item);
        }
        _list.ShowItemToolTips = true;
        _list.EndUpdate();
        _info.Text = candidates.Count == 0 ? "Keine Kopien gefunden." : $"{candidates.Count} mögliche Kopien gefunden.";
        UpdateButtons();
    }

    private async Task MergeAsync()
    {
        if (Selected is not { } c)
            return;
        var warning = c.Assessment == CopyAssessment.EditedSince
            ? $"\n\nAchtung: In der Kopie gibt es {c.NewerInCopy} neuere Stände – dort wurde offenbar weitergearbeitet. Diese würden beim Original als Versionen eingetragen, die jünger sind als der aktuelle Stand."
            : "";
        if (MessageBox.Show(this, $"Stände aus\n{c.CopyFolder}\nals ältere Versionen bei\n{c.OriginalFolder}\neintragen?{warning}", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        var result = await Ui.RunAsync(this, () => _workspace.MergeCopyAsync(c).GetAwaiter().GetResult());
        if (result is null)
            return;
        MessageBox.Show(this, $"{DisplayText.Count(result.VersionsAdded, "älterer Stand", "ältere Stände")} beim Original eingetragen " +
                              $"({DisplayText.Count(result.AlreadyKnown, "Stand war", "Stände waren")} schon bekannt, " +
                              $"{DisplayText.Files(result.OnlyInCopy)} nur in der Kopie).\n\nAn Ihren Ordnern wurde nichts verändert. Wenn Sie die Kopie nicht " +
                              "mehr brauchen, können Sie sie löschen – alle Stände bleiben im Archiv.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        _workspace.IgnoreCopy(c); // erledigt: nicht erneut vorschlagen
        await RefreshAsync();
    }

    private void Ignore()
    {
        if (Selected is not { } c)
            return;
        _workspace.IgnoreCopy(c);
        _list.Items.Remove(_list.SelectedItems[0]);
        UpdateButtons();
    }
}
