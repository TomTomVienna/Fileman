using Fileman.Core;
using Fileman.Core.Automation;
using Fileman.Core.Backup;
using Fileman.Core.Catalog;
using Fileman.Core.Configuration;
using Fileman.Core.Protection;

namespace Fileman.App;

/// <summary>
/// Die Anwendung ohne Hauptfenster: Symbol im Infobereich mit Menü, Benachrichtigungen und den Fenstern
/// „Letzte Änderungen“, „Archiv durchsuchen“ und „Einstellungen“. Die Arbeit erledigt der <see cref="BackupService"/>.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly FilemanWorkspace _workspace;
    private readonly BackupService _service;
    private readonly NotifyIcon _tray;
    private readonly Control _invoker;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _alarmItem;
    private MassChangeForm? _alarmForm;
    private RecentChangesForm? _recent;
    private ArchiveBrowserForm? _browser;
    private bool _settingsOpen;
    private ServiceStatus? _lastStatus;
    private Action? _balloonClick;
    private CopiesForm? _copiesForm;
    private DateTime _lastCopyCheckUtc = DateTime.MinValue;
    private readonly HashSet<string> _notifiedCopies = [];
    private readonly EventWaitHandle _exitRequest;
    private readonly RegisteredWaitHandle _exitRegistration;
    private readonly List<(EventWaitHandle Event, RegisteredWaitHandle Registration)> _showRequests = [];

    /// <param name="initialWindow">Per Startoption gewünschtes Fenster (siehe <see cref="Program.Windows"/>).</param>
    public TrayApplicationContext(string? initialWindow = null)
    {
        var paths = AppPaths.Default();
        FilemanLog.Configure(paths.LogFile);
        FilemanLog.Info("Fileman gestartet.");
        _workspace = new FilemanWorkspace(paths);

        // Ereignisse des Dienstes kommen auf Hintergrund-Threads; über dieses Steuerelement gelangen sie in die Oberfläche.
        _invoker = new Control();
        _invoker.CreateControl();

        _statusItem = new ToolStripMenuItem("Status wird ermittelt …") { Enabled = false };
        _pauseItem = new ToolStripMenuItem("Automatische Sicherung pausieren", null, (_, _) => TogglePause()) { CheckOnClick = true };
        _alarmItem = new ToolStripMenuItem("Ungewöhnlich viele Änderungen prüfen …", null, (_, _) => ShowAlarm()) { Visible = false };
        _alarmItem.Font = new Font(_alarmItem.Font, FontStyle.Bold);
        var recentItem = new ToolStripMenuItem("Letzte Änderungen anzeigen", null, (_, _) => ShowRecent());
        recentItem.Font = new Font(recentItem.Font, FontStyle.Bold);
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            _statusItem,
            _alarmItem,
            new ToolStripSeparator(),
            recentItem,
            new ToolStripMenuItem("Jetzt sichern", null, (_, _) => BackupNow()),
            new ToolStripMenuItem("Archiv durchsuchen …", null, (_, _) => ShowBrowser(null)),
            new ToolStripMenuItem("Altbestand übernehmen …", null, (_, _) => ShowLegacyImport()),
            new ToolStripMenuItem("Kopien prüfen …", null, (_, _) => ShowCopies()),
            new ToolStripSeparator(),
            _pauseItem,
            new ToolStripMenuItem("Einstellungen …", null, (_, _) => ShowSettings()),
            new ToolStripMenuItem("Protokoll öffnen", null, (_, _) => OpenLog()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Beenden", null, (_, _) => Exit()),
        ]);

        _tray = new NotifyIcon
        {
            Icon = StatusIcons.For(ServiceStatus.NotConfigured),
            Text = "Fileman",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ToggleRecent();
        };
        _tray.BalloonTipClicked += (_, _) => (_balloonClick ?? ShowRecent)();
        _tray.BalloonTipClosed += (_, _) => _balloonClick = null;

        // Das Setup kann eine laufende Instanz über dieses Ereignis beenden ("Fileman.exe --exit").
        _exitRequest = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ExitEventName);
        _exitRegistration = ThreadPool.RegisterWaitForSingleObject(_exitRequest, (_, _) => Post(Exit), null, Timeout.Infinite, executeOnlyOnce: true);

        _service = new BackupService(_workspace);
        _service.StateChanged += (_, state) => Post(() => OnStateChanged(state));
        _service.BackupCompleted += (_, result) => Post(() => OnBackupCompleted(result));
        _service.MassChangeDetected += (_, _) => Post(() =>
        {
            Notify(ToolTipIcon.Error, "Sicherung angehalten", "Ungewöhnlich viele Dateien wurden geändert. Bitte prüfen!");
            ShowAlarm();
        });
        _service.Start();
        if (_service.Alarm is not null)
            Post(ShowAlarm); // Alarm von vor dem Neustart

        // Startoptionen aus einem zweiten Aufruf (z. B. Startmenü "Archiv durchsuchen") in dieser Instanz ausführen.
        foreach (var window in Program.Windows)
        {
            var request = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName(window));
            var registration = ThreadPool.RegisterWaitForSingleObject(request, (_, _) => Post(() => ShowWindow(window)), null, Timeout.Infinite, executeOnlyOnce: false);
            _showRequests.Add((request, registration));
        }
        if (initialWindow is not null)
            Post(() => ShowWindow(initialWindow));

        if (_workspace.Settings.Roots.Count == 0 || _workspace.Settings.Target is null)
            Post(ShowSettings);
    }

    private void Post(Action action)
    {
        if (!_invoker.IsDisposed && _invoker.IsHandleCreated)
            _invoker.BeginInvoke(action);
    }

    // ---------- Zustand ----------

    private void OnStateChanged(ServiceState state)
    {
        _tray.Icon = StatusIcons.For(state.Status);
        var text = "Fileman – " + DisplayText.Status(state.Status);
        if (state.Status == ServiceStatus.Running && state.Progress is { Phase: BackupPhase.Saving, Total: > 0 } p)
            text += $" ({p.Processed}/{p.Total})";
        else if (state.LastBackupUtc is { } last && state.Status == ServiceStatus.Idle)
            text += $" ({last.ToLocalTime():HH:mm})";
        else if (state.SpooledFiles > 0)
            text += $" – {state.SpooledFiles} Änderungen warten";
        _tray.Text = text.Length > 127 ? text[..127] : text;
        _statusItem.Text = text["Fileman – ".Length..];
        _recent?.UpdateState(state);
        _alarmItem.Visible = state.Alarm is not null;

        if (state.Status != _lastStatus)
        {
            switch (state.Status)
            {
                case ServiceStatus.Error:
                    Notify(ToolTipIcon.Warning, "Sicherung fehlgeschlagen", state.Message ?? "Details stehen im Protokoll.");
                    break;
                case ServiceStatus.TargetMissing when _lastStatus is not null:
                    Notify(ToolTipIcon.Info, "Sicherungsplatte nicht angesteckt",
                        "Änderungen werden vorerst auf diesem PC zwischengespeichert und beim nächsten Anstecken übertragen.");
                    break;
                case ServiceStatus.Running when _lastStatus == ServiceStatus.TargetMissing:
                    Notify(ToolTipIcon.Info, "Sicherungsplatte erkannt", "Die Sicherung wird jetzt nachgeholt.");
                    break;
            }
            _lastStatus = state.Status;
        }
    }

    private void OnBackupCompleted(BackupResult result)
    {
        var problems = result.Errors.Count + result.SkippedRoots.Count + result.UnreadableDirectories.Count;
        if (problems > 0)
        {
            var detail = result.SkippedRoots.Count > 0
                ? $"Quellordner nicht erreichbar: {string.Join(", ", result.SkippedRoots)}"
                : $"{result.Errors.Count + result.UnreadableDirectories.Count} Dateien oder Ordner konnten nicht gesichert werden (z. B. weil sie gerade geöffnet sind). Es wird später erneut versucht.";
            Notify(ToolTipIcon.Warning, "Sicherung unvollständig", detail);
        }
        else if (result.Trigger == BackupTrigger.Manual)
        {
            Notify(ToolTipIcon.Info, "Sicherung abgeschlossen",
                result.TotalChanges switch
                {
                    0 => "Es gab keine Änderungen – alles war bereits gesichert.",
                    1 => "1 Änderung gesichert.",
                    var n => $"{n} Änderungen gesichert.",
                });
        }
        if (_recent is { Visible: true })
            _ = _recent.RefreshAsync();
        if (result.Trigger != BackupTrigger.Watcher)
            _ = CheckForCopiesAsync();
    }

    /// <summary>Nach vollständigen Läufen höchstens einmal täglich im Hintergrund nach Kopien suchen und neue melden.</summary>
    private async Task CheckForCopiesAsync()
    {
        if (DateTime.UtcNow - _lastCopyCheckUtc < TimeSpan.FromHours(24))
            return;
        _lastCopyCheckUtc = DateTime.UtcNow;
        try
        {
            var candidates = await Task.Run(() => _workspace.DetectCopies());
            var fresh = candidates.Where(c => _notifiedCopies.Add(c.Key)).ToList();
            if (fresh.Count == 0)
                return;
            Notify(ToolTipIcon.Info, "Kopien gefunden",
                (fresh.Count == 1 ? $"„{Path.GetFileName(fresh[0].CopyFolder)}“ sieht aus wie eine Kopie" : $"{fresh.Count} Ordner sehen aus wie Kopien") +
                " anderer Ordner. Klicken, um sie zu prüfen.", ShowCopies);
        }
        catch (Exception ex) when (ex is FilemanException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            FilemanLog.Warn("Kopien-Erkennung fehlgeschlagen: " + ex.Message);
        }
    }

    private void ShowCopies()
    {
        if (_copiesForm is null || _copiesForm.IsDisposed)
            _copiesForm = new CopiesForm(_workspace);
        _copiesForm.Show();
        _copiesForm.Activate();
    }

    private void Notify(ToolTipIcon icon, string title, string text, Action? onClick = null)
    {
        _balloonClick = onClick;
        _tray.ShowBalloonTip(5000, title, text, icon);
    }

    // ---------- Aktionen ----------

    private async void BackupNow()
    {
        try
        {
            Notify(ToolTipIcon.Info, "Sicherung gestartet", "Sie werden benachrichtigt, sobald sie abgeschlossen ist.");
            await _service.BackupNowAsync();
        }
        catch (TargetNotConnectedException)
        {
            Notify(ToolTipIcon.Warning, "Sicherungsplatte nicht angesteckt", "Bitte die Sicherungsplatte anstecken und erneut versuchen.");
        }
        catch (MassChangeException)
        {
            ShowAlarm();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is FilemanException or IOException)
        {
            Notify(ToolTipIcon.Warning, "Sicherung nicht möglich", ex.Message);
        }
    }

    /// <summary>Warnfenster zur Massenänderung; bei „Das war ich“ wird weiter gesichert.</summary>
    private void ShowAlarm()
    {
        if (_service.Alarm is not { } report)
            return;
        if (_alarmForm is { IsDisposed: false })
        {
            _alarmForm.Activate();
            return;
        }
        var form = new MassChangeForm(report);
        _alarmForm = form;
        form.FormClosed += (_, _) =>
        {
            if (form.DialogResult == DialogResult.Yes)
            {
                _service.ConfirmMassChange();
                Notify(ToolTipIcon.Info, "Sicherung wird fortgesetzt", "Die Änderungen werden jetzt gesichert.");
            }
            _alarmForm = null;
        };
        form.Show();
        form.Activate();
    }

    private LegacyImportForm? _legacyForm;

    private void ShowWindow(string window)
    {
        switch (window)
        {
            case "archiv": ShowBrowser(null); break;
            case "einstellungen": ShowSettings(); break;
            case "altbestand": ShowLegacyImport(); break;
            case "kopien": ShowCopies(); break;
        }
    }

    /// <summary>Altbestand übernehmen: einlesen, Zuordnung prüfen, übernehmen.</summary>
    private void ShowLegacyImport()
    {
        if (_legacyForm is null || _legacyForm.IsDisposed)
            _legacyForm = new LegacyImportForm(_workspace, _service);
        _legacyForm.Show();
        _legacyForm.Activate();
    }

    private void TogglePause()
    {
        if (_pauseItem.Checked)
            _service.Pause();
        else
            _service.Resume();
    }

    private void ToggleRecent()
    {
        if (_recent is { Visible: true })
            _recent.Hide();
        else
            ShowRecent();
    }

    private void ShowRecent()
    {
        if (_recent is null || _recent.IsDisposed)
        {
            _recent = new RecentChangesForm(_workspace);
            _recent.OpenDocumentRequested += id => ShowBrowser(id);
            _recent.BackupNowRequested += BackupNow;
            _recent.BrowseRequested += () => ShowBrowser(null);
        }
        _recent.UpdateState(_service.State);
        _ = _recent.ShowNearTrayAsync();
    }

    private void ShowBrowser(long? documentId)
    {
        if (_browser is null || _browser.IsDisposed)
            _browser = new ArchiveBrowserForm(_workspace);
        _browser.Show();
        if (_browser.WindowState == FormWindowState.Minimized)
            _browser.WindowState = FormWindowState.Normal;
        _browser.Activate();
        if (documentId is { } id)
            _ = _browser.ShowDocumentAsync(id);
    }

    private void ShowSettings()
    {
        if (_settingsOpen)
            return;
        _settingsOpen = true;
        try
        {
            using var form = new SettingsForm(_workspace);
            if (form.ShowDialog() != DialogResult.OK)
                return;
            _workspace.ReloadSettings();
            _service.ReloadSettings();
            if (form.HasNewLegacyFolders
                && MessageBox.Show("Es wurden Altbestände eingetragen, die noch nicht eingelesen sind. Jetzt übernehmen?", "Fileman",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                ShowLegacyImport();
        }
        finally
        {
            _settingsOpen = false;
        }
    }

    private void OpenLog()
    {
        if (File.Exists(_workspace.Paths.LogFile))
            Ui.OpenWithDefaultProgram(_workspace.Paths.LogFile);
        else
            MessageBox.Show("Es wurde noch nichts protokolliert.", "Fileman");
    }

    private async void Exit()
    {
        _tray.Visible = false;
        _recent?.Close();
        _browser?.Close();
        _alarmForm?.Close();
        _legacyForm?.Close();
        _copiesForm?.Close();
        try
        {
            await _service.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            FilemanLog.Warn("Automatik-Modus hat sich nicht rechtzeitig beendet.");
        }
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exitRegistration.Unregister(null);
            foreach (var (request, registration) in _showRequests)
            {
                registration.Unregister(null);
                request.Dispose();
            }
            _exitRequest.Dispose();
            _tray.Dispose();
            _recent?.Dispose();
            _browser?.Dispose();
            _invoker.Dispose();
        }
        base.Dispose(disposing);
    }
}
