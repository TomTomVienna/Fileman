using Fileman.Core;
using Fileman.Core.Archive;
using Fileman.Core.Configuration;

namespace Fileman.App;

/// <summary>Quellordner, Sicherungsplatte und Optionen. Bearbeitet eine Kopie; gespeichert wird erst mit „OK“.</summary>
internal sealed class SettingsForm : Form
{
    private readonly FilemanWorkspace _workspace;
    private readonly FilemanSettings _settings;
    private readonly ListView _roots;
    private readonly Label _target;
    private readonly CheckBox _autostart;
    private readonly NumericUpDown _debounce;
    private readonly TextBox _excludes;

    public SettingsForm(FilemanWorkspace workspace)
    {
        _workspace = workspace;
        _settings = SettingsStore.Clone(workspace.Settings);
        Text = "Fileman – Einstellungen";
        Size = new Size(820, 640);
        MinimumSize = new Size(640, 520);
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
        var rootsGroup = Group("Diese Ordner werden gesichert", Stack(_roots, rootButtons));

        // Sicherungsplatte
        _target = new Label { AutoSize = true, Margin = Ui.Gap, MaximumSize = new Size(700, 0) };
        var targetGroup = Group("Sicherungsplatte", Stack(_target, Ui.Button("Sicherungsplatte festlegen …", async (_, _) => await ChooseTargetAsync())));

        // Optionen
        _autostart = new CheckBox { Text = "Fileman beim Anmelden an Windows automatisch starten", AutoSize = true, Margin = Ui.Gap, Checked = SafeAutostartState() };
        _debounce = new NumericUpDown { Minimum = 1, Maximum = 600, Value = Math.Clamp(_settings.DebounceSeconds, 1, 600), Width = 70, Margin = Ui.Gap };
        var debounceRow = new FlowLayoutPanel { AutoSize = true };
        debounceRow.Controls.Add(new Label { Text = "Automatisch sichern, wenn eine Datei so viele Sekunden nicht mehr geändert wurde:", AutoSize = true, Margin = new Padding(6, 10, 0, 6) });
        debounceRow.Controls.Add(_debounce);
        _excludes = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 90, Margin = Ui.Gap, Text = string.Join(Environment.NewLine, _settings.ExcludePatterns) };
        var optionsGroup = Group("Optionen", Stack(_autostart, debounceRow,
            new Label { Text = "Nicht sichern (ein Muster pro Zeile, * und ? erlaubt, z. B. *.tmp):", AutoSize = true, Margin = Ui.Gap },
            _excludes));

        var okButton = Ui.Button("OK", (_, _) => Save());
        var cancelButton = Ui.Button("Abbrechen", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        AcceptButton = okButton;
        CancelButton = cancelButton;
        var dialogButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        dialogButtons.Controls.AddRange([cancelButton, okButton]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(rootsGroup, 0, 0);
        layout.Controls.Add(targetGroup, 0, 1);
        layout.Controls.Add(optionsGroup, 0, 2);
        layout.Controls.Add(dialogButtons, 0, 3);
        Controls.Add(layout);

        FillRoots();
        UpdateTarget();
    }

    private static GroupBox Group(string title, Control content)
    {
        // Gruppen mit einer Liste füllen den verfügbaren Platz, alle anderen sind so hoch wie ihr Inhalt.
        var fills = content is TableLayoutPanel { AutoSize: false };
        var group = new GroupBox { Text = title, Dock = DockStyle.Fill, AutoSize = !fills, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8), Margin = new Padding(0, 0, 0, 8) };
        content.Dock = DockStyle.Fill;
        group.Controls.Add(content);
        return group;
    }

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
