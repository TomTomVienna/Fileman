using Fileman.Core;
using Fileman.Core.Archive;
using Fileman.Core.Catalog;
using Fileman.Core.Configuration;
using Fileman.Core.Legacy;

namespace Fileman.App;

/// <summary>
/// Quellordner, Altbestand, Sicherungsplatte und Optionen (je ein Reiter). Bearbeitet eine Kopie; gespeichert wird erst mit „OK“.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly FilemanWorkspace _workspace;
    private readonly FilemanSettings _settings;
    private readonly ListView _roots;
    private readonly Label _target;
    private readonly CheckBox _autostart;
    private readonly NumericUpDown _debounce;
    private readonly TextBox _excludes;
    private readonly ListView _legacy;
    private Dictionary<string, LegacySourceRecord> _legacyState = new(PathUtil.Comparer);

    /// <summary>So viele Quellordner sollen ohne Scrollen sichtbar sein.</summary>
    private const int VisibleRootRows = 10;

    public SettingsForm(FilemanWorkspace workspace)
    {
        _workspace = workspace;
        _settings = SettingsStore.Clone(workspace.Settings);
        Text = "Fileman – Einstellungen";
        Size = new Size(820, 520);
        MinimumSize = new Size(640, 420);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = StatusIcons.For(Core.Automation.ServiceStatus.Idle);

        // Quellordner
        _roots = Ui.DetailsList(("Anzeigename", 170), ("Ordner", 440), ("Status", 120));
        _roots.LabelEdit = true;
        _roots.AfterLabelEdit += (_, e) =>
        {
            if (e.Label is not null)
                _settings.Roots[e.Item].Name = string.IsNullOrWhiteSpace(e.Label) ? null : e.Label.Trim();
        };
        var rootButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        rootButtons.Controls.Add(Ui.Button("Ordner hinzufügen …", (_, _) => AddRoot()));
        rootButtons.Controls.Add(Ui.Button("Entfernen", (_, _) => RemoveRoot()));
        rootButtons.Controls.Add(new Label { Text = "Anzeigename ändern: Eintrag anklicken und F2 drücken.", AutoSize = true, Margin = new Padding(12, 12, 6, 6), ForeColor = SystemColors.GrayText });
        var rootsPage = Page("Gesicherte Ordner", Stack(
            Hint("Diese Ordner werden laufend überwacht und gesichert."), _roots, rootButtons));

        // Altbestand
        _legacy = Ui.DetailsList(("Ordner der alten Sicherung", 470), ("Stand", 260));
        _legacy.MultiSelect = true;
        var legacyButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        legacyButtons.Controls.Add(Ui.Button("Oberordner durchsuchen …", async (_, _) => await DetectLegacyAsync()));
        legacyButtons.Controls.Add(Ui.Button("Einzelne Ordner hinzufügen …", (_, _) => AddLegacy()));
        legacyButtons.Controls.Add(Ui.Button("Entfernen", (_, _) => RemoveLegacy()));
        var legacyPage = Page("Altbestand", Stack(
            Hint("Alte Sicherungen, z. B. Datumsordner, die früher auf eine externe Platte kopiert wurden. Jeder Ordner ist eine " +
                 "Momentaufnahme. Altbestände werden nicht überwacht, sondern einmal eingelesen und dann automatisch den " +
                 "gesicherten Ordnern zugeordnet (Tray-Menü „Altbestand übernehmen …“). Am einfachsten: „Oberordner durchsuchen …“ " +
                 "und den Ordner wählen, in dem die alten Sicherungen liegen (z. B. E:\\Sicherungen) – Fileman erkennt sie selbst."),
            _legacy, legacyButtons));

        // Sicherungsplatte
        _target = new Label { AutoSize = true, Margin = Ui.Gap, MaximumSize = new Size(700, 0) };
        var targetPage = Page("Sicherungsplatte", Stack(_target, Ui.Button("Sicherungsplatte festlegen …", async (_, _) => await ChooseTargetAsync())));

        // Optionen
        _autostart = new CheckBox { Text = "Fileman beim Anmelden an Windows automatisch starten", AutoSize = true, Margin = Ui.Gap, Checked = SafeAutostartState() };
        _debounce = new NumericUpDown { Minimum = 1, Maximum = 600, Value = Math.Clamp(_settings.DebounceSeconds, 1, 600), Width = 70, Margin = Ui.Gap };
        var debounceRow = new FlowLayoutPanel { AutoSize = true };
        debounceRow.Controls.Add(new Label { Text = "Automatisch sichern, wenn eine Datei so viele Sekunden nicht mehr geändert wurde:", AutoSize = true, Margin = new Padding(6, 10, 0, 6) });
        debounceRow.Controls.Add(_debounce);
        _excludes = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 90, Margin = Ui.Gap, Text = string.Join(Environment.NewLine, _settings.ExcludePatterns) };
        var optionsPage = Page("Optionen", Stack(_autostart, debounceRow,
            new Label { Text = "Nicht sichern (ein Muster pro Zeile, * und ? erlaubt, z. B. *.tmp):", AutoSize = true, Margin = Ui.Gap },
            _excludes));

        var okButton = Ui.Button("OK", (_, _) => Save());
        var cancelButton = Ui.Button("Abbrechen", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        AcceptButton = okButton;
        CancelButton = cancelButton;
        var dialogButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        dialogButtons.Controls.AddRange([cancelButton, okButton]);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 4) };
        tabs.TabPages.AddRange([rootsPage, legacyPage, targetPage, optionsPage]);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(tabs, 0, 0);
        layout.Controls.Add(dialogButtons, 0, 1);
        Controls.Add(layout);

        FillRoots();
        FillLegacy();
        UpdateTarget();
        Load += async (_, _) =>
        {
            EnsureVisibleRootRows();
            await LoadLegacyStateAsync();
        };
    }

    /// <summary>
    /// Vergrößert das Fenster so, dass in der Quellordner-Liste <see cref="VisibleRootRows"/> Zeilen sichtbar sind.
    /// Die Zeilenhöhe wird gemessen, damit es bei jeder Windows-Skalierung (100 %, 125 %, 150 % …) passt.
    /// </summary>
    private void EnsureVisibleRootRows()
    {
        var probe = _roots.Items.Count == 0 ? _roots.Items.Add("Probe") : null;
        var firstRow = _roots.GetItemRect(0);
        probe?.Remove();

        // Oberkante der ersten Zeile = Höhe der Spaltenüberschrift; etwas Reserve für Rahmen.
        var needed = firstRow.Top + VisibleRootRows * firstRow.Height + 4;
        var missing = needed - _roots.ClientSize.Height;
        if (missing <= 0)
            return;

        var screen = Screen.FromControl(this).WorkingArea;
        var newHeight = Math.Min(Height + missing, screen.Height);
        MinimumSize = new Size(MinimumSize.Width, Math.Min(MinimumSize.Height + missing, screen.Height));
        Height = newHeight;
        Top = Math.Max(screen.Top, screen.Top + (screen.Height - Height) / 2);
    }

    private static TabPage Page(string title, Control content)
    {
        var page = new TabPage(title) { Padding = new Padding(8), UseVisualStyleBackColor = true };
        content.Dock = DockStyle.Fill;
        page.Controls.Add(content);
        return page;
    }

    private static Label Hint(string text) =>
        new() { Text = text, AutoSize = true, MaximumSize = new Size(740, 0), Margin = Ui.Gap, ForeColor = SystemColors.GrayText };

    // ---------- Altbestand ----------

    private void FillLegacy()
    {
        _legacy.BeginUpdate();
        _legacy.Items.Clear();
        foreach (var folder in _settings.LegacyFolders)
        {
            var item = new ListViewItem(folder.Path) { Tag = folder };
            item.SubItems.Add(LegacyStateText(folder.Path));
            _legacy.Items.Add(item);
        }
        _legacy.EndUpdate();
    }

    private string LegacyStateText(string path) =>
        _legacyState.TryGetValue(path, out var s)
            ? s.ConsolidatedUtc is { } c ? $"übernommen am {DisplayText.Time(c)}"
            : s.ImportedUtc is not null ? $"eingelesen ({s.Files} Dateien), Zuordnung offen"
            : "teilweise eingelesen"
            : Directory.Exists(path) ? "noch nicht eingelesen" : "noch nicht eingelesen (nicht erreichbar)";

    private async Task LoadLegacyStateAsync()
    {
        var sources = await Task.Run(() =>
        {
            try
            {
                using var view = _workspace.OpenCatalogForReading();
                return view.Catalog.GetLegacySources();
            }
            catch (Exception ex) when (ex is FilemanException or IOException or Microsoft.Data.Sqlite.SqliteException)
            {
                return [];
            }
        });
        if (IsDisposed)
            return;
        _legacyState = sources.ToDictionary(s => s.Path, PathUtil.Comparer);
        FillLegacy();
    }

    private async Task DetectLegacyAsync()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Ordner wählen, in dem die alten Sicherungen liegen (z. B. E:\\Sicherungen oder die ganze externe Platte).",
            UseDescriptionForTitle = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        // Mit den Einstellungen dieses Fensters suchen (auch noch nicht gespeicherte Quellordner/Altbestände berücksichtigen).
        var settings = _settings;
        var candidates = await Ui.RunAsync(this, () => LegacyDetector.Detect(dialog.SelectedPath,
            settings.Roots.Select(r => r.Path).ToList(),
            settings.Target is { LastKnownPath.Length: > 0 } t ? [t.LastKnownPath] : [],
            settings.LegacyFolders.Select(l => l.Path).ToList()));
        if (candidates is null)
            return;
        using var form = new LegacyDetectForm(dialog.SelectedPath, candidates);
        if (form.ShowDialog(this) != DialogResult.OK || form.SelectedFolders.Count == 0)
            return;
        try
        {
            SettingsStore.AddLegacyFolders(_settings, form.SelectedFolders);
            FillLegacy();
        }
        catch (FilemanException ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void AddLegacy()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Ordner der alten Sicherungen wählen (mehrere mit Strg oder Umschalt markieren), z. B. alle Datumsordner.",
            UseDescriptionForTitle = true,
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            var added = SettingsStore.AddLegacyFolders(_settings, dialog.SelectedPaths);
            if (added < dialog.SelectedPaths.Length)
                MessageBox.Show(this, $"{dialog.SelectedPaths.Length - added} Ordner waren bereits eingetragen.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            FillLegacy();
        }
        catch (FilemanException ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void RemoveLegacy()
    {
        var selected = _legacy.SelectedItems.Cast<ListViewItem>().Select(i => (LegacyFolder)i.Tag!).ToList();
        if (selected.Count == 0)
            return;
        if (MessageBox.Show(this, $"{selected.Count} Altbestand-Ordner aus der Liste entfernen?\n\nBereits eingelesene Dateien und übernommene Versionen bleiben im Archiv erhalten.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        foreach (var folder in selected)
            _settings.LegacyFolders.Remove(folder);
        FillLegacy();
    }

    /// <summary>Wurden Altbestände eingetragen, die noch nicht eingelesen sind? (Tray bietet dann das Einlesen an.)</summary>
    public bool HasNewLegacyFolders =>
        _settings.LegacyFolders.Any(l => !_legacyState.TryGetValue(l.Path, out var s) || s.ImportedUtc is null);

    private static TableLayoutPanel Stack(params Control[] controls)
    {
        var hasList = controls.Any(c => c is ListView);
        var panel = new TableLayoutPanel { ColumnCount = 1, RowCount = controls.Length, AutoSize = !hasList, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < controls.Length; i++)
        {
            panel.RowStyles.Add(controls[i] is ListView ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            if (controls[i] is ListView)
                controls[i].Dock = DockStyle.Fill;
            else if (controls[i] is TextBox { Multiline: true })
                controls[i].Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            panel.Controls.Add(controls[i], 0, i);
        }
        return panel;
    }

    private static bool SafeAutostartState()
    {
        try { return Autostart.IsEnabled; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    // ---------- Quellordner ----------

    private void FillRoots()
    {
        _roots.Items.Clear();
        foreach (var root in _settings.Roots)
        {
            var item = new ListViewItem(root.Name ?? Path.GetFileName(root.Path));
            item.SubItems.Add(root.Path);
            item.SubItems.Add(Directory.Exists(root.Path) ? "erreichbar" : "nicht erreichbar");
            _roots.Items.Add(item);
        }
    }

    private void AddRoot()
    {
        using var dialog = new FolderBrowserDialog { Description = "Welcher Ordner soll gesichert werden?", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            SettingsStore.AddRoot(_settings, dialog.SelectedPath, Path.GetFileName(PathUtil.Normalize(dialog.SelectedPath)));
            FillRoots();
        }
        catch (FilemanException ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void RemoveRoot()
    {
        if (_roots.SelectedIndices.Count != 1)
            return;
        var root = _settings.Roots[_roots.SelectedIndices[0]];
        if (MessageBox.Show(this, $"\"{root.Path}\" nicht mehr sichern?\n\nBereits gesicherte Stände bleiben im Archiv erhalten und können weiterhin gesucht und wiederhergestellt werden.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _settings.Roots.Remove(root);
        FillRoots();
    }

    // ---------- Sicherungsplatte ----------

    private void UpdateTarget()
    {
        if (_settings.Target is not { } target)
        {
            _target.Text = "Noch keine Sicherungsplatte festgelegt. Bitte die USB-SSD anstecken und unten auswählen.";
            _target.ForeColor = StatusIcons.ColorFor(Core.Automation.ServiceStatus.Error);
            return;
        }
        var found = TargetLocator.Locate(target);
        _target.Text = found is not null
            ? $"Archiv: {found}  (angesteckt)"
            : $"Archiv: {target.LastKnownPath}  (zurzeit nicht angesteckt – wird auch unter anderem Laufwerksbuchstaben wiedererkannt)";
        _target.ForeColor = found is not null ? SystemColors.ControlText : StatusIcons.ColorFor(Core.Automation.ServiceStatus.TargetMissing);
    }

    private async Task ChooseTargetAsync()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Bitte die USB-SSD (oder einen Ordner darauf) auswählen. Wird das Laufwerk selbst gewählt, legt Fileman dort den Ordner \"FilemanArchiv\" an.",
            UseDescriptionForTitle = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var directory = FilemanWorkspace.ResolveTargetDirectory(dialog.SelectedPath);
        if (_settings.Roots.Any(r => PathUtil.IsUnder(directory, r.Path) || PathUtil.IsUnder(r.Path, directory)))
        {
            MessageBox.Show(this, "Die Sicherung darf nicht in einem der gesicherten Ordner liegen. Bitte die USB-SSD wählen.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var existing = TargetMarker.TryRead(directory);
        var question = existing is not null
            ? $"In \"{directory}\" liegt bereits ein Fileman-Archiv (\"{existing.Name}\", angelegt am {existing.CreatedUtc.ToLocalTime():dd.MM.yyyy}).\n\nDieses Archiv weiterverwenden?"
            : $"Das Archiv wird in\n\n{directory}\n\nangelegt. Fortfahren?";
        if (MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        var marker = await Ui.RunAsync(this, () => BackupArchive.Initialize(directory, $"Fileman-Sicherung {Environment.MachineName}"));
        if (marker is null)
            return;
        _settings.Target = new TargetSettings { Id = marker.Id, LastKnownPath = directory };
        UpdateTarget();
    }

    // ---------- Speichern ----------

    private void Save()
    {
        if (_settings.Roots.Count == 0 || _settings.Target is null)
        {
            if (MessageBox.Show(this, "Ohne Quellordner und Sicherungsplatte kann Fileman nichts sichern. Trotzdem speichern?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
        }
        _settings.DebounceSeconds = (int)_debounce.Value;
        _settings.ExcludePatterns = _excludes.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
        SettingsStore.Save(_settings, _workspace.Paths.SettingsFile);
        try
        {
            if (_autostart.Checked != SafeAutostartState())
                Autostart.Set(_autostart.Checked);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            MessageBox.Show(this, "Der automatische Start konnte nicht geändert werden: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
