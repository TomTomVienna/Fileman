using Fileman.Core;
using Fileman.Core.Catalog;
using Fileman.Core.Metadata;
using Fileman.Core.Restore;

namespace Fileman.App;

/// <summary>Dokumente suchen (auch über frühere Namen/Pfade), Versionen ansehen, wiederherstellen und vergleichen.</summary>
internal sealed class ArchiveBrowserForm : Form
{
    private readonly FilemanWorkspace _workspace;
    private readonly TextBox _search;
    private readonly CheckBox _showDeleted;
    private readonly ComboBox _root;
    private readonly ListView _documents;
    private readonly ListView _versions;
    private readonly ListView _history;
    private readonly Button _view;
    private readonly Button _restore;
    private readonly Button _compare;
    private readonly Label _info;
    private bool _archiveAvailable;
    private int _loadGeneration;

    public ArchiveBrowserForm(FilemanWorkspace workspace)
    {
        _workspace = workspace;
        Text = "Fileman – Archiv durchsuchen";
        Size = new Size(1100, 720);
        MinimumSize = new Size(800, 500);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = StatusIcons.For(Core.Automation.ServiceStatus.Idle);

        // Suchleiste
        _search = new TextBox { Width = 380, Margin = Ui.Gap, PlaceholderText = "Dateiname oder Ordner – auch frühere Namen …" };
        _search.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await SearchAsync();
            }
        };
        _root = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220, Margin = Ui.Gap };
        _showDeleted = new CheckBox { Text = "Gelöschte anzeigen", Checked = true, AutoSize = true, Margin = new Padding(6, 10, 6, 6) };
        var searchBar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        searchBar.Controls.AddRange([_search, _root, _showDeleted, Ui.Button("Suchen", async (_, _) => await SearchAsync())]);

        // Dokumente
        _documents = Ui.DetailsList(("Name", 240), ("Ordner", 420), ("Dateistand", 120), ("Versionen", 70), ("Status", 160));
        _documents.MultiSelect = false;
        _documents.SelectedIndexChanged += async (_, _) => await LoadDetailsAsync();

        // Versionen
        _versions = Ui.DetailsList(("Dateistand", 125), ("Gesichert", 125), ("Größe", 80), ("Art", 85), ("Prüfsumme", 100));
        _versions.SelectedIndexChanged += (_, _) => UpdateButtons();
        _versions.ItemActivate += async (_, _) => await ViewAsync();
        _view = Ui.Button("Ansehen", async (_, _) => await ViewAsync());
        _restore = Ui.Button("Wiederherstellen …", async (_, _) => await RestoreAsync());
        _compare = Ui.Button("Vergleichen", async (_, _) => await CompareAsync());
        var versionButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        versionButtons.Controls.AddRange([_view, _restore, _compare]);

        // Pfadverlauf
        _history = Ui.DetailsList(("Bis", 125), ("Früherer Pfad", 400));

        var versionsPanel = Section("Versionen (neueste zuerst; zwei markieren zum Vergleichen)", _versions, versionButtons);
        var historyPanel = Section("Frühere Pfade und Namen", _history, null);
        var lower = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
        lower.Panel1.Controls.Add(versionsPanel);
        lower.Panel2.Controls.Add(historyPanel);
        var main = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        main.Panel1.Controls.Add(_documents);
        main.Panel2.Controls.Add(lower);

        _info = new Label { AutoSize = true, Margin = Ui.Gap, ForeColor = SystemColors.GrayText };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(searchBar, 0, 0);
        layout.Controls.Add(main, 0, 1);
        layout.Controls.Add(_info, 0, 2);
        Controls.Add(layout);

        Load += async (_, _) =>
        {
            main.SplitterDistance = (int)(main.Height * 0.5);
            lower.SplitterDistance = (int)(lower.Width * 0.6);
            FillRoots();
            UpdateButtons();
            await SearchAsync();
        };
    }

    private static Control Section(string title, Control content, Control? footer)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label { Text = title, AutoSize = true, Margin = Ui.Gap, Font = new Font(Control.DefaultFont, FontStyle.Bold) }, 0, 0);
        panel.Controls.Add(content, 0, 1);
        if (footer is not null)
            panel.Controls.Add(footer, 0, 2);
        return panel;
    }

    private void FillRoots()
    {
        _root.Items.Clear();
        _root.Items.Add("Alle Quellordner");
        foreach (var root in _workspace.Settings.Roots)
            _root.Items.Add(root.DisplayName);
        _root.SelectedIndex = 0;
    }

    private string? SelectedRootPath =>
        _root.SelectedIndex > 0 ? _workspace.Settings.Roots.ElementAtOrDefault(_root.SelectedIndex - 1)?.Path : null;

    // ---------- Laden ----------

    private async Task SearchAsync()
    {
        var query = new FindQuery { Text = _search.Text, IncludeDeleted = _showDeleted.Checked, UnderPath = SelectedRootPath, Limit = 1000 };
        var result = await Ui.RunAsync(this, () =>
        {
            using var view = _workspace.OpenCatalogForReading();
            return (Docs: view.Catalog.Find(query), view.IsCache);
        });
        if (result.Docs is null)
            return;
        ShowDocuments(result.Docs, result.IsCache);
        _info.Text = (result.Docs.Count >= query.Limit ? $"Mehr als {query.Limit} Treffer – bitte Suche verfeinern. " : $"{result.Docs.Count} Treffer. ")
                     + (result.IsCache ? "Die Sicherungsplatte ist nicht angesteckt: Suchen ist möglich, Ansehen und Wiederherstellen erst nach dem Anstecken." : "");
    }

    /// <summary>Zeigt ein bestimmtes Dokument an (z. B. aus „Letzte Änderungen“).</summary>
    public async Task ShowDocumentAsync(long documentId)
    {
        var result = await Ui.RunAsync(this, () =>
        {
            using var view = _workspace.OpenCatalogForReading();
            return (Doc: view.Catalog.GetDocument(documentId), view.IsCache);
        });
        if (result.Doc is null)
            return;
        _search.Text = result.Doc.FileName;
        ShowDocuments([result.Doc], result.IsCache);
        _documents.Items[0].Selected = true;
    }

    private void ShowDocuments(List<DocumentRecord> docs, bool isCache)
    {
        _archiveAvailable = !isCache;
        _documents.BeginUpdate();
        _documents.Items.Clear();
        foreach (var doc in docs)
        {
            var item = new ListViewItem(doc.FileName) { Tag = doc };
            item.SubItems.Add(Path.GetDirectoryName(doc.Path));
            item.SubItems.Add(DisplayText.Time(doc.LastWriteUtc));
            item.SubItems.Add(doc.VersionCount.ToString());
            item.SubItems.Add(doc.IsDeleted ? $"gelöscht {DisplayText.Time(doc.DeletedUtc!.Value)}" : "vorhanden");
            if (doc.IsDeleted)
                item.ForeColor = SystemColors.GrayText;
            _documents.Items.Add(item);
        }
        _documents.EndUpdate();
        _versions.Items.Clear();
        _history.Items.Clear();
        UpdateButtons();
    }

    private async Task LoadDetailsAsync()
    {
        _versions.Items.Clear();
        _history.Items.Clear();
        UpdateButtons();
        if (_documents.SelectedItems.Count != 1 || _documents.SelectedItems[0].Tag is not DocumentRecord doc)
            return;

        var generation = ++_loadGeneration;
        var details = await Ui.RunAsync(this, () =>
        {
            using var view = _workspace.OpenCatalogForReading();
            return (Versions: view.Catalog.GetVersions(doc.Id), History: view.Catalog.GetPathHistory(doc.Id));
        });
        if (details.Versions is null || generation != _loadGeneration)
            return; // inzwischen ein anderes Dokument gewählt

        foreach (var v in details.Versions)
        {
            var item = new ListViewItem(DisplayText.Time(v.LastWriteUtc)) { Tag = v };
            item.SubItems.Add(DisplayText.Time(v.CapturedUtc));
            item.SubItems.Add(DisplayText.Size(v.Size));
            item.SubItems.Add(DisplayText.Trigger(v.Trigger));
            item.SubItems.Add(v.Hash[..10]);
            item.ToolTipText = v.Path;
            _versions.Items.Add(item);
        }
        _versions.ShowItemToolTips = true;
        foreach (var change in details.History.AsEnumerable().Reverse())
        {
            var item = new ListViewItem(DisplayText.Time(change.ChangedUtc));
            item.SubItems.Add(change.OldPath);
            _history.Items.Add(item);
        }
        if (_versions.Items.Count > 0)
            _versions.Items[0].Selected = true;
        UpdateButtons();
    }

    private List<VersionRecord> SelectedVersions =>
        _versions.SelectedItems.Cast<ListViewItem>().Select(i => (VersionRecord)i.Tag!).ToList();

    private void UpdateButtons()
    {
        var count = _versions.SelectedItems.Count;
        _view.Enabled = _archiveAvailable && count >= 1;
        _restore.Enabled = _archiveAvailable && count == 1;
        _compare.Enabled = _archiveAvailable && (count == 2 || (count == 1 && _versions.Items.Count > 1));
    }

    // ---------- Aktionen ----------

    private async Task ViewAsync()
    {
        foreach (var version in SelectedVersions.Take(2))
        {
            var path = await Ui.RunAsync(this, () =>
            {
                using var archive = _workspace.OpenArchive(readOnly: true);
                return new RestoreService(archive).ExtractForViewing(version.Id, _workspace.Paths.ViewDirectory);
            });
            if (path is not null)
                Ui.OpenWithDefaultProgram(path);
        }
    }

    private async Task RestoreAsync()
    {
        if (SelectedVersions is not [var version])
            return;
        var originalFolder = Path.GetDirectoryName(version.Path)!;
        using var dialog = new FolderBrowserDialog
        {
            Description = "Wohin soll die Version wiederhergestellt werden? Vorhandene Dateien werden nie überschrieben.",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(originalFolder) ? originalFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var target = await Ui.RunAsync(this, () =>
        {
            using var archive = _workspace.OpenArchive(readOnly: true);
            return new RestoreService(archive).Restore(version.Id, dialog.SelectedPath);
        });
        if (target is null)
            return;
        if (MessageBox.Show(this, $"Wiederhergestellt als:\n\n{target}\n\nIm Explorer anzeigen?", "Fileman",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            Ui.ShowInExplorer(target);
    }

    private async Task CompareAsync()
    {
        var selected = SelectedVersions;
        if (selected.Count == 1)
        {
            // Mit der vorherigen (älteren) Version vergleichen, sonst mit der neueren.
            var all = _versions.Items.Cast<ListViewItem>().Select(i => (VersionRecord)i.Tag!).ToList();
            var index = all.FindIndex(v => v.Id == selected[0].Id);
            selected.Add(index + 1 < all.Count ? all[index + 1] : all[index - 1]);
        }
        if (selected.Count != 2)
            return;
        var ordered = selected.OrderBy(v => v.CapturedUtc).ThenBy(v => v.Id).ToList();
        var rows = await Ui.RunAsync(this, () =>
        {
            using var archive = _workspace.OpenArchive(readOnly: true);
            return VersionComparison.Compare(archive, ordered[0], ordered[1]);
        });
        if (rows is not null)
            new CompareForm(ordered[0], ordered[1], rows).Show(this);
    }
}
