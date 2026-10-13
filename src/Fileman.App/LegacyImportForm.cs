using Fileman.Core;
using Fileman.Core.Automation;
using Fileman.Core.Legacy;

namespace Fileman.App;

/// <summary>
/// Altbestand übernehmen in drei Schritten: 1. einlesen („as is“), 2. Zuordnung prüfen (automatischer Vorschlag,
/// per Häkchen bestätigen), 3. übernehmen (Versionsgeschichte aufbauen). Während der Arbeit pausiert die Automatik.
/// </summary>
internal sealed class LegacyImportForm : Form
{
    private readonly FilemanWorkspace _workspace;
    private readonly BackupService _service;
    private readonly Label _importState;
    private readonly Button _import;
    private readonly Button _report;
    private readonly ListView _mappings;
    private readonly Label _mappingInfo;
    private readonly Button _consolidate;
    private readonly ProgressBar _progress;
    private readonly Label _progressText;
    private readonly Button _cancel;
    private LegacyProposal? _proposal;
    private CancellationTokenSource? _running;

    public LegacyImportForm(FilemanWorkspace workspace, BackupService service)
    {
        _workspace = workspace;
        _service = service;
        Text = "Fileman – Altbestand übernehmen";
        Size = new Size(1000, 680);
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = StatusIcons.For(ServiceStatus.Idle);

        // 1. Einlesen
        _importState = new Label { AutoSize = true, MaximumSize = new Size(900, 0), Margin = Ui.Gap };
        _import = Ui.Button("Einlesen starten", async (_, _) => await ImportAsync());
        _report = Ui.Button("Übersicht als Bericht …", async (_, _) => await ReportAsync());
        var importButtons = new FlowLayoutPanel { AutoSize = true };
        importButtons.Controls.AddRange([_import, _report]);

        // 2. Zuordnung
        _mappings = Ui.DetailsList(("Ordner im Altbestand", 220), ("Gesicherter Ordner (Ziel)", 360), ("Altbestände", 85), ("Dateien", 70),
            ("im Master bekannt", 115), ("Hinweis", 110));
        _mappings.CheckBoxes = true;
        _mappings.ItemChecked += (_, _) => UpdateButtons();
        _mappingInfo = new Label { AutoSize = true, MaximumSize = new Size(900, 0), Margin = Ui.Gap, ForeColor = SystemColors.GrayText };
        _mappingInfo.Click += (_, _) => ShowUnmappedExamples();

        // 3. Übernehmen
        _consolidate = Ui.Button("Markierte Zuordnungen übernehmen", async (_, _) => await ConsolidateAsync());

        _progress = new ProgressBar { Dock = DockStyle.Fill, Height = 18, Margin = Ui.Gap };
        _progressText = new Label { AutoSize = true, Margin = Ui.Gap };
        _cancel = Ui.Button("Abbrechen", (_, _) => _running?.Cancel());
        _cancel.Enabled = false;
        var bottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        bottom.Controls.AddRange([Ui.Button("Schließen", (_, _) => Close()), _cancel]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
        void Row(Control control, SizeType size = SizeType.AutoSize, float height = 0)
        {
            layout.RowStyles.Add(new RowStyle(size, height));
            layout.Controls.Add(control, 0, layout.RowStyles.Count - 1);
        }
        Row(Heading("1. Alte Sicherungen einlesen"));
        Row(Explanation("Alle eingetragenen Altbestand-Ordner werden unverändert ins Archiv kopiert (Ordner „Dateien\\_Altbestand“). " +
                        "Gleiche Dateien aus verschiedenen Datumsordnern werden nur einmal gelesen und gespeichert. Die alten Ordner selbst bleiben unverändert."));
        Row(_importState);
        Row(importButtons);
        Row(Heading("2. Zuordnung prüfen"));
        Row(Explanation("Fileman hat ermittelt, welcher Ordner im Altbestand welchem heute gesicherten Ordner entspricht. " +
                        "Bitte prüfen; nur markierte Zuordnungen werden übernommen."));
        Row(_mappings, SizeType.Percent, 100);
        Row(_mappingInfo);
        Row(Heading("3. Übernehmen"));
        Row(Explanation("Jeder unterschiedliche Stand wird als Version mit Datum im Namen beim heutigen Ordner abgelegt; Duplikate fallen weg. " +
                        "Dateien, die es heute nicht mehr gibt, bleiben als „gelöscht“ auffindbar. Es wird nichts überschrieben und nichts doppelt gespeichert."));
        Row(_consolidate);
        Row(_progress);
        Row(_progressText);
        Row(bottom);
        Controls.Add(layout);

        Load += async (_, _) => await RefreshAsync();
        FormClosing += (_, e) =>
        {
            if (_running is not null)
            {
                e.Cancel = true;
                MessageBox.Show(this, "Bitte warten, bis der laufende Schritt beendet ist, oder „Abbrechen“ klicken.", Text);
            }
        };
    }

    private static Label Heading(string text) =>
        new() { Text = text, AutoSize = true, Font = new Font(Control.DefaultFont.FontFamily, 11f, FontStyle.Bold), Margin = new Padding(6, 12, 6, 2) };

    private static Label Explanation(string text) =>
        new() { Text = text, AutoSize = true, MaximumSize = new Size(940, 0), Margin = Ui.Gap, ForeColor = SystemColors.GrayText };

    // ---------- Zustand ----------

    private async Task RefreshAsync()
    {
        var folders = _workspace.Settings.LegacyFolders.Select(l => l.Path).ToList();
        if (folders.Count == 0)
        {
            _importState.Text = "Es sind noch keine Altbestände eingetragen. Bitte zuerst unter Einstellungen → Altbestand die Ordner der alten Sicherungen hinzufügen.";
            UpdateButtons();
            return;
        }

        var (sources, proposal, error) = await Task.Run(() =>
        {
            try
            {
                using var view = _workspace.OpenCatalogForReading();
                var known = view.Catalog.GetLegacySources();
                var proposal = view.IsCache ? null : _workspace.ProposeLegacyMapping();
                return (known, proposal, (string?)null);
            }
            catch (Exception ex) when (ex is FilemanException or IOException or Microsoft.Data.Sqlite.SqliteException)
            {
                return ([], null, ex.Message);
            }
        });
        if (IsDisposed)
            return;

        var imported = folders.Count(f => sources.Any(s => PathUtil.Comparer.Equals(s.Path, f) && s.ImportedUtc is not null));
        var unreachable = folders.Count(f => !Directory.Exists(f) && !sources.Any(s => PathUtil.Comparer.Equals(s.Path, f) && s.ImportedUtc is not null));
        _importState.Text = $"{folders.Count} Altbestände eingetragen: {imported} eingelesen, {folders.Count - imported} noch nicht eingelesen"
                            + (unreachable > 0 ? $", davon {unreachable} nicht erreichbar (externe Platte angesteckt?)" : "") + "."
                            + (error is null ? "" : $"\n{error}");
        ShowProposal(proposal);
        UpdateButtons();
    }

    private void ShowProposal(LegacyProposal? proposal)
    {
        _proposal = proposal;
        _mappings.BeginUpdate();
        _mappings.Items.Clear();
        if (proposal is not null)
        {
            foreach (var group in proposal.Groups)
            {
                var hint = group.NewerThanMaster > 0 ? $"{group.NewerThanMaster} jünger als heute!" : "";
                var item = new ListViewItem(group.LegacyPrefix.Length == 0 ? "(ganzer Datumsordner)" : group.LegacyPrefix)
                {
                    Tag = group,
                    // Vorauswahl nur bei klarer Übereinstimmung und ohne Unplausibilitäten.
                    Checked = group.MatchRate >= 0.5 && group.NewerThanMaster == 0,
                };
                item.SubItems.Add(group.TargetPrefix);
                item.SubItems.Add(group.Mappings.Count.ToString());
                item.SubItems.Add(group.Files.ToString("N0"));
                item.SubItems.Add(group.MatchRate.ToString("P0"));
                item.SubItems.Add(hint);
                if (hint.Length > 0)
                    item.ForeColor = StatusIcons.ColorFor(ServiceStatus.TargetMissing);
                _mappings.Items.Add(item);
            }
            var unmapped = proposal.UnmappedFiles.Values.Sum();
            _mappingInfo.Text = proposal.Groups.Count == 0
                ? "Noch keine Zuordnung möglich – bitte zuerst einlesen. Die passenden Ordner müssen als gesicherte Ordner eingerichtet und mindestens einmal gesichert sein."
                : unmapped == 0 ? "Alle Dateien der eingelesenen Altbestände sind zugeordnet."
                : unmapped == 1
                    ? "1 Datei ließ sich keinem gesicherten Ordner zuordnen und bleibt unter „_Altbestand“ erhalten. (Klicken für Beispiele)"
                    : $"{unmapped:N0} Dateien ließen sich keinem gesicherten Ordner zuordnen und bleiben unter „_Altbestand“ erhalten. (Klicken für Beispiele)";
            if (proposal.Sources.Count > 0 && proposal.Sources.All(s => s.ConsolidatedUtc is not null))
                _mappingInfo.Text += "  Alle eingelesenen Altbestände wurden bereits übernommen; erneutes Übernehmen ergänzt nur Fehlendes.";
        }
        else
        {
            _mappingInfo.Text = "Für die Zuordnung muss die Sicherungsplatte angesteckt sein.";
        }
        _mappings.EndUpdate();
    }

    private void UpdateButtons()
    {
        var idle = _running is null;
        _import.Enabled = idle && _workspace.Settings.LegacyFolders.Count > 0;
        _report.Enabled = idle && _workspace.Settings.LegacyFolders.Any(l => Directory.Exists(l.Path));
        _consolidate.Enabled = idle && _mappings.CheckedItems.Count > 0;
        _cancel.Enabled = !idle;
        _mappings.Enabled = idle;
    }

    // ---------- Schritte ----------

    private async Task ImportAsync()
    {
        var result = await RunAsync("Einlesen", async token =>
        {
            var progress = new Progress<LegacyProgress>(p =>
            {
                _progress.Maximum = Math.Max(1, p.Total);
                _progress.Value = Math.Min(p.Done, _progress.Maximum);
                _progressText.Text = $"Altbestand {p.FolderIndex + 1} von {p.FolderCount}: {Path.GetFileName(p.Folder)} – {p.Done:N0} von {p.Total:N0} Dateien";
            });
            return await _workspace.ImportLegacyAsync(progress, token);
        });
        if (result is null)
            return;
        _progressText.Text = $"Eingelesen: {result.Imported.Count} Altbestände, {result.Files:N0} Dateien " +
                             $"({result.FilesRead:N0} neue Stände gelesen, {result.FilesLinked:N0} Kopien nur verlinkt, {DisplayText.Size(result.BytesStored)} neu auf der Platte)."
                             + (result.Unreachable.Count > 0 ? $" {result.Unreachable.Count} nicht erreichbar." : "")
                             + (result.Errors.Count > 0 ? $" {result.Errors.Count} Fehler (siehe Protokoll)." : "");
        foreach (var error in result.Errors)
            FilemanLog.Warn($"Altbestand: {error.Path}: {error.Message}");
        await RefreshAsync();
    }

    private async Task ConsolidateAsync()
    {
        var accepted = _mappings.CheckedItems.Cast<ListViewItem>().SelectMany(i => ((LegacyMappingGroup)i.Tag!).Mappings).ToList();
        if (MessageBox.Show(this, $"{_mappings.CheckedItems.Count} Zuordnungen übernehmen?\n\nDie Versionen erscheinen danach im Archiv-Browser bei den heutigen Dateien.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        var result = await RunAsync("Übernehmen", async token =>
        {
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                _progress.Maximum = Math.Max(1, p.Total);
                _progress.Value = Math.Min(p.Done, _progress.Maximum);
                _progressText.Text = $"Übernehme {p.Done:N0} von {p.Total:N0} Dateien …";
            });
            return await _workspace.ConsolidateLegacyAsync(accepted, progress, token);
        });
        if (result is null)
            return;
        _progressText.Text = $"Übernommen: {result.VersionsAdded:N0} neue Versionen, {result.DuplicatesSkipped:N0} Duplikate zusammengeführt, " +
                             $"{DisplayText.Files(result.DocumentsCreated)} nur im Altbestand (als gelöscht auffindbar), {result.Unmapped:N0} nicht zugeordnet.";
        await RefreshAsync();
    }

    /// <summary>Führt einen Schritt aus: Automatik pausieren, Fortschritt zeigen, Fehler verständlich melden.</summary>
    private async Task<T?> RunAsync<T>(string what, Func<CancellationToken, Task<T>> work) where T : class
    {
        var pausedByUs = !_service.IsPaused;
        if (pausedByUs)
            _service.Pause();
        _running = new CancellationTokenSource();
        UpdateButtons();
        _progress.Value = 0;
        _progressText.Text = $"{what} läuft …";
        try
        {
            return await Task.Run(() => work(_running.Token));
        }
        catch (OperationCanceledException)
        {
            _progressText.Text = $"{what} abgebrochen. Bereits Erledigtes bleibt erhalten und wird beim nächsten Mal fortgesetzt.";
            return null;
        }
        catch (Exception ex) when (ex is FilemanException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            FilemanLog.Error($"Altbestand: {what} fehlgeschlagen.", ex);
            _progressText.Text = $"{what} fehlgeschlagen: {ex.Message}";
            return null;
        }
        finally
        {
            _running.Dispose();
            _running = null;
            if (pausedByUs)
                _service.Resume();
            UpdateButtons();
        }
    }

    private async Task ReportAsync()
    {
        var folders = _workspace.Settings.LegacyFolders.Select(l => l.Path).Where(Directory.Exists).ToList();
        var report = LegacyReport.DefaultReportPath();
        var ok = await Ui.RunAsync(this, () =>
        {
            LegacyReport.Write(LegacyAnalyzer.Analyze(folders, "Eingetragene Altbestände", parallelism: _workspace.Settings.ScanThreads), report);
            return true;
        });
        if (ok)
            Ui.OpenWithDefaultProgram(report);
    }

    private void ShowUnmappedExamples()
    {
        if (_proposal is not { UnmappedExamples.Count: > 0 })
            return;
        MessageBox.Show(this, "Beispiele nicht zugeordneter Dateien (bleiben unter „_Altbestand“):\n\n" +
                              string.Join("\n", _proposal.UnmappedExamples.Take(20)), Text);
    }
}
