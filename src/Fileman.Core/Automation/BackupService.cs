using Fileman.Core.Archive;
using Fileman.Core.Backup;
using System.Text.Json;
using Fileman.Core.Catalog;
using Fileman.Core.Protection;
using Fileman.Core.Scanning;

namespace Fileman.Core.Automation;

public enum ServiceStatus
{
    /// <summary>Quellordner oder Sicherungsplatte fehlen in den Einstellungen.</summary>
    NotConfigured,
    /// <summary>Alles gesichert.</summary>
    Idle,
    /// <summary>Änderungen erkannt, Ruhezeit läuft.</summary>
    Pending,
    Running,
    /// <summary>Platte nicht angesteckt; Änderungen werden zwischengespeichert.</summary>
    TargetMissing,
    Paused,
    /// <summary>Letzter Versuch ist fehlgeschlagen; es wird erneut versucht.</summary>
    Error,
    /// <summary>Ungewöhnlich viele Änderungen: Automatik angehalten, bis der Anwender bestätigt.</summary>
    MassChange,
}

public sealed record ServiceState(
    ServiceStatus Status,
    int PendingPaths,
    int SpooledFiles,
    DateTime? LastBackupUtc,
    string? Message,
    BackupProgress? Progress,
    MassChangeReport? Alarm = null);

public sealed class BackupServiceOptions
{
    /// <summary>Ruhezeit nach der letzten Änderung (Standard: aus den Einstellungen).</summary>
    public TimeSpan? Debounce { get; init; }
    /// <summary>Spätestens nach dieser Zeit wird gesichert, auch wenn sich laufend etwas ändert.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>Wie oft geprüft wird, ob die Platte angesteckt wurde.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Abstand vollständiger Abgleiche (Standard: aus den Einstellungen).</summary>
    public TimeSpan? ReconcileInterval { get; init; }
    /// <summary>Wartezeit nach einem Fehler.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Ab so vielen geänderten Pfaden wird statt gezielt vollständig abgeglichen.</summary>
    public int MaxScopedPaths { get; init; } = 300;
}

/// <summary>
/// Automatik-Modus: überwacht die Quellordner, sichert Änderungen nach einer Ruhezeit, gleicht regelmäßig
/// vollständig ab und speichert Änderungen lokal zwischen, solange die Sicherungsplatte fehlt.
/// Alle Arbeit läuft auf einem einzigen Hintergrund-Ablauf; Oberflächen erfahren den Zustand über <see cref="StateChanged"/>.
/// </summary>
public sealed class BackupService : IAsyncDisposable
{
    private readonly FilemanWorkspace _workspace;
    private readonly BackupServiceOptions _options;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly object _sync = new();
    private readonly HashSet<string> _pending = new(PathUtil.Comparer);
    private readonly Queue<TaskCompletionSource<BackupResult>> _manualRequests = new();
    private ChangeWatcher? _watcher;
    private CancellationTokenSource? _stop;
    private CancellationTokenSource? _currentRun;
    private Task? _loop;
    private DateTime? _firstPendingUtc;
    private DateTime? _lastEventUtc;
    private DateTime _lastFullRunUtc = DateTime.MinValue;
    private DateTime _retryAfterUtc = DateTime.MinValue;
    private bool _needFullRun = true;
    private bool _reloadRequested;
    private bool? _targetWasAvailable;
    private volatile bool _paused;
    private readonly MassChangeGuard _guard;
    private MassChangeReport? _alarm;
    private DateTime? _lastBackupUtc;
    private long _lastProgressTicks;
    private ServiceState _state = new(ServiceStatus.Idle, 0, 0, null, null, null);

    public BackupService(FilemanWorkspace workspace, BackupServiceOptions? options = null)
    {
        _workspace = workspace;
        _options = options ?? new BackupServiceOptions();
        _guard = new MassChangeGuard(workspace.Settings.MassChange, workspace.Time);
        _alarm = LoadAlarm();
    }

    /// <summary>Ungewöhnlich viele Änderungen erkannt; die Automatik ist angehalten (auf einem Hintergrund-Thread!).</summary>
    public event EventHandler<MassChangeReport>? MassChangeDetected;

    /// <summary>Der aktuelle Alarm, falls die Automatik wegen einer Massenänderung angehalten ist.</summary>
    public MassChangeReport? Alarm
    {
        get
        {
            lock (_sync)
                return _alarm;
        }
    }

    /// <summary>
    /// Der Anwender bestätigt, dass die Änderungen gewollt sind: einmal ohne Prüfung vollständig sichern, dann normal weiter.
    /// </summary>
    public void ConfirmMassChange()
    {
        lock (_sync)
        {
            if (_alarm is null)
                return;
            _alarm = null;
            _guard.BypassNextRun = true;
            _needFullRun = true;
            _retryAfterUtc = DateTime.MinValue;
        }
        try { File.Delete(_workspace.Paths.MassChangeAlarmFile); } catch (IOException) { }
        FilemanLog.Warn("Massenänderung vom Anwender bestätigt – Sicherung wird fortgesetzt.");
        _wake.Release();
    }

    /// <summary>Zustandsänderungen (auf einem Hintergrund-Thread!).</summary>
    public event EventHandler<ServiceState>? StateChanged;

    /// <summary>Nach jeder abgeschlossenen Sicherung (auf einem Hintergrund-Thread!).</summary>
    public event EventHandler<BackupResult>? BackupCompleted;

    public ServiceState State
    {
        get
        {
            lock (_sync)
                return _state;
        }
    }

    public bool IsPaused => _paused;

    private DateTime Now => _workspace.Time.GetUtcNow().UtcDateTime;
    private TimeSpan Debounce => _options.Debounce ?? TimeSpan.FromSeconds(Math.Max(1, _workspace.Settings.DebounceSeconds));
    private TimeSpan ReconcileInterval => _options.ReconcileInterval ?? TimeSpan.FromMinutes(Math.Max(5, _workspace.Settings.ReconcileMinutes));

    public void Start()
    {
        if (_loop is not null)
            return;
        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_stop.Token));
        FilemanLog.Info("Automatik-Modus gestartet.");
    }

    public async Task StopAsync()
    {
        if (_loop is null)
            return;
        _stop!.Cancel();
        _currentRun?.Cancel();
        try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _loop = null;
        _watcher?.Dispose();
        _watcher = null;
        lock (_sync)
        {
            while (_manualRequests.TryDequeue(out var request))
                request.TrySetCanceled();
        }
        FilemanLog.Info("Automatik-Modus beendet.");
    }

    /// <summary>Sofort vollständig sichern („Jetzt sichern“). Läuft auch, wenn die Automatik pausiert ist.</summary>
    public Task<BackupResult> BackupNowAsync()
    {
        var request = new TaskCompletionSource<BackupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
            _manualRequests.Enqueue(request);
        _wake.Release();
        return request.Task;
    }

    public void Pause()
    {
        _paused = true;
        _currentRun?.Cancel();
        _wake.Release();
    }

    public void Resume()
    {
        _paused = false;
        lock (_sync)
            _needFullRun = true; // während der Pause Verpasstes nachholen
        _wake.Release();
    }

    /// <summary>Nach geänderten Einstellungen: Wächter neu aufsetzen und vollständig abgleichen.</summary>
    public void ReloadSettings()
    {
        lock (_sync)
        {
            _reloadRequested = true;
            _needFullRun = true;
        }
        _wake.Release();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _wake.Dispose();
    }

    // ---------- Hauptschleife ----------

    private async Task LoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                FilemanLog.Error("Unerwarteter Fehler im Automatik-Modus.", ex);
                SetState(ServiceStatus.Error, ex.Message);
                _retryAfterUtc = Now + _options.RetryDelay;
            }

            try
            {
                await _wake.WaitAsync(NextWait(), stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private TimeSpan NextWait()
    {
        var wait = _options.PollInterval;
        lock (_sync)
        {
            if (_lastEventUtc is { } last)
            {
                var due = last + Debounce - Now;
                wait = due < wait ? due : wait;
            }
        }
        return wait < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : wait;
    }

    private async Task TickAsync(CancellationToken stop)
    {
        var settings = _workspace.Settings;
        bool reload;
        lock (_sync)
        {
            reload = _reloadRequested;
            _reloadRequested = false;
        }
        if (reload)
        {
            _workspace.ReloadSettings();
            settings = _workspace.Settings;
            _guard.Settings = settings.MassChange;
            _watcher?.Dispose();
            _watcher = null;
        }

        if (settings.Roots.Count == 0 || settings.Target is null)
        {
            FailManualRequests(new FilemanException("Bitte zuerst Quellordner und Sicherungsplatte in den Einstellungen festlegen."));
            SetState(ServiceStatus.NotConfigured, "Bitte Quellordner und Sicherungsplatte einrichten.");
            return;
        }

        EnsureWatcher(settings.Roots.Select(r => r.Path));
        CollectWatcherEvents();

        var target = _workspace.LocateArchive();
        if (target is not null && _targetWasAvailable == false)
        {
            FilemanLog.Info($"Sicherungsplatte erkannt: {target}");
            lock (_sync)
                _needFullRun = true; // Zwischenspeicher übertragen und alles abgleichen
        }
        _targetWasAvailable = target is not null;

        TaskCompletionSource<BackupResult>[] manual;
        MassChangeReport? alarm;
        lock (_sync)
        {
            manual = [.. _manualRequests];
            _manualRequests.Clear();
            alarm = _alarm;
        }

        if (alarm is not null)
        {
            // Nichts sichern und nichts zwischenspeichern, bis der Anwender entschieden hat.
            foreach (var request in manual)
                request.TrySetException(new MassChangeException(alarm));
            SetState(ServiceStatus.MassChange, alarm.Reason);
            return;
        }

        if (manual.Length > 0)
        {
            if (target is null)
            {
                foreach (var request in manual)
                    request.TrySetException(new TargetNotConnectedException());
            }
            else
            {
                await RunAsync(BackupTrigger.Manual, scopes: null, manual, stop).ConfigureAwait(false);
                return;
            }
        }

        if (_paused)
        {
            SetState(ServiceStatus.Paused, "Automatische Sicherung pausiert.");
            return;
        }

        bool due, needFull;
        lock (_sync)
        {
            var now = Now;
            due = _pending.Count > 0 && _lastEventUtc is { } last && _firstPendingUtc is { } first
                  && (now - last >= Debounce || now - first >= _options.MaxDelay);
            needFull = _needFullRun || now - _lastFullRunUtc >= ReconcileInterval;
        }

        if (target is null)
        {
            if (due)
                SpoolPending(settings, stop);
            var spooled = _workspace.Spool.Count;
            SetState(ServiceStatus.TargetMissing,
                spooled > 0 ? $"{spooled} Änderungen warten im Zwischenspeicher." : null);
            return;
        }

        if (Now < _retryAfterUtc)
            return;

        if (needFull)
        {
            await RunAsync(BackupTrigger.Reconcile, scopes: null, [], stop).ConfigureAwait(false);
        }
        else if (due)
        {
            List<string> paths;
            lock (_sync)
                paths = [.. _pending];
            var scopes = paths.Count > _options.MaxScopedPaths ? null : BuildScopes(paths);
            await RunAsync(BackupTrigger.Watcher, scopes, [], stop).ConfigureAwait(false);
        }
        else
        {
            int pending;
            lock (_sync)
                pending = _pending.Count;
            SetState(pending > 0 ? ServiceStatus.Pending : ServiceStatus.Idle, null);
        }
    }

    private void EnsureWatcher(IEnumerable<string> roots)
    {
        if (_watcher is null)
        {
            _watcher = new ChangeWatcher(_workspace.CreateExclusionFilter(), _workspace.Settings.Target?.LastKnownPath);
            _watcher.Changed += () => _wake.Release();
        }
        // Ein Quellordner kann zwischenzeitlich verschwinden (Netzlaufwerk) und wiederkommen.
        if (_watcher.Watch(roots))
        {
            lock (_sync)
                _needFullRun = true;
        }
    }

    private void CollectWatcherEvents()
    {
        var (paths, overflow) = _watcher!.Drain();
        lock (_sync)
        {
            if (overflow)
            {
                FilemanLog.Warn("Ereignisse des Dateisystem-Wächters gingen verloren – vollständiger Abgleich folgt.");
                _needFullRun = true;
            }
            if (paths.Count == 0)
                return;
            var now = Now;
            _firstPendingUtc ??= now;
            _lastEventUtc = now;
            _pending.UnionWith(paths);
        }
    }

    /// <summary>
    /// Jeder geänderte Pfad wird rekursiv geprüft: eine Datei als Einzeldatei, ein (umbenannter) Ordner mit Inhalt,
    /// ein gelöschter Pfad als „fehlt“ – so werden auch verschobene Ordner vollständig erfasst.
    /// </summary>
    internal static List<ScanScope> BuildScopes(IEnumerable<string> paths)
    {
        var sorted = paths.Select(PathUtil.Normalize).Distinct(PathUtil.Comparer).OrderBy(p => p.Length).ToList();
        var result = new List<ScanScope>();
        foreach (var path in sorted)
        {
            if (!result.Any(s => PathUtil.IsUnder(path, s.Path)))
                result.Add(new ScanScope(path, Recursive: true));
        }
        return result;
    }

    private async Task RunAsync(BackupTrigger trigger, IReadOnlyList<ScanScope>? scopes, TaskCompletionSource<BackupResult>[] requests, CancellationToken stop)
    {
        // Pfade, die ab jetzt gemeldet werden, gehören zum nächsten Lauf.
        HashSet<string> taken;
        lock (_sync)
        {
            taken = [.. _pending];
            _pending.Clear();
            _firstPendingUtc = null;
            _lastEventUtc = null;
        }

        using var run = CancellationTokenSource.CreateLinkedTokenSource(stop);
        _currentRun = run;
        var progress = new InlineProgress(p =>
        {
            // Höchstens vier Fortschrittsmeldungen pro Sekunde an die Oberfläche.
            var ticks = Environment.TickCount64;
            if (ticks - Interlocked.Read(ref _lastProgressTicks) < 250)
                return;
            Interlocked.Exchange(ref _lastProgressTicks, ticks);
            SetState(ServiceStatus.Running, null, p);
        });
        SetState(ServiceStatus.Running, null);
        try
        {
            var result = await _workspace.RunBackupAsync(trigger, scopes, progress, run.Token, _guard).ConfigureAwait(false);
            lock (_sync)
            {
                _lastBackupUtc = result.FinishedUtc;
                if (scopes is null)
                {
                    _needFullRun = false;
                    _lastFullRunUtc = Now;
                }
            }
            Log(result);
            foreach (var request in requests)
                request.TrySetResult(result);
            BackupCompleted?.Invoke(this, result);
            SetState(ServiceStatus.Idle, Summary(result));
        }
        catch (MassChangeException ex)
        {
            lock (_sync)
            {
                _alarm = ex.Report;
                _pending.UnionWith(taken);
                _needFullRun = true;
            }
            SaveAlarm(ex.Report);
            FilemanLog.Warn($"Massenänderung erkannt, Automatik angehalten: {ex.Report.Reason}{Environment.NewLine}  "
                            + string.Join(Environment.NewLine + "  ", ex.Report.Examples));
            foreach (var request in requests)
                request.TrySetException(ex);
            SetState(ServiceStatus.MassChange, ex.Report.Reason);
            MassChangeDetected?.Invoke(this, ex.Report);
        }
        catch (Exception ex)
        {
            // Nichts geht verloren: Pfade zurücklegen und später erneut versuchen.
            lock (_sync)
            {
                _pending.UnionWith(taken);
                if (_pending.Count > 0)
                {
                    _firstPendingUtc ??= Now;
                    _lastEventUtc ??= Now;
                }
                if (scopes is null)
                    _needFullRun = true;
            }
            foreach (var request in requests)
            {
                if (ex is OperationCanceledException)
                    request.TrySetCanceled();
                else
                    request.TrySetException(ex);
            }
            if (ex is OperationCanceledException)
            {
                if (stop.IsCancellationRequested)
                    throw;
                SetState(_paused ? ServiceStatus.Paused : ServiceStatus.Pending, "Sicherung abgebrochen.");
                return;
            }
            _retryAfterUtc = Now + _options.RetryDelay;
            if (ex is ArchiveBusyException)
            {
                SetState(ServiceStatus.Pending, ex.Message);
                return;
            }
            if (ex is not (FilemanException or IOException or UnauthorizedAccessException))
                throw;
            FilemanLog.Error("Sicherung fehlgeschlagen.", ex);
            SetState(ex is TargetNotConnectedException ? ServiceStatus.TargetMissing : ServiceStatus.Error, ex.Message);
        }
        finally
        {
            _currentRun = null;
        }
    }

    /// <summary>Ohne Platte: aktuelle Stände der geänderten Dateien lokal festhalten.</summary>
    private void SpoolPending(Configuration.FilemanSettings settings, CancellationToken stop)
    {
        List<string> paths;
        lock (_sync)
        {
            paths = [.. _pending];
            _pending.Clear();
            _firstPendingUtc = null;
            _lastEventUtc = null;
            _needFullRun = true; // beim Wiederanstecken vollständig abgleichen
        }

        var limit = (long)settings.SpoolMaxMegabytes * 1024 * 1024;
        var scanner = new FileScanner(_workspace.CreateExclusionFilter(), parallelism: settings.ScanThreads);
        var now = Now;
        var spooled = 0;
        foreach (var path in paths)
        {
            IEnumerable<string> files = File.Exists(path) ? [path]
                : Directory.Exists(path) ? scanner.Scan(path, cancellationToken: stop).Files.Select(f => f.Path)
                : [];
            foreach (var file in files)
            {
                stop.ThrowIfCancellationRequested();
                if (_workspace.Spool.StoredBytes >= limit)
                {
                    FilemanLog.Warn("Zwischenspeicher voll – weitere Änderungen werden erst beim Anstecken der Platte gesichert.");
                    return;
                }
                try
                {
                    _workspace.Spool.Add(file, now, stop);
                    spooled++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    FilemanLog.Warn($"Zwischenspeichern nicht möglich: {file}: {ex.Message}");
                }
            }
        }
        if (spooled > 0)
            FilemanLog.Info($"{spooled} Dateien zwischengespeichert (Sicherungsplatte nicht angesteckt).");
    }

    private void FailManualRequests(Exception ex)
    {
        lock (_sync)
        {
            while (_manualRequests.TryDequeue(out var request))
                request.TrySetException(ex);
        }
    }

    private void SetState(ServiceStatus status, string? message, BackupProgress? progress = null)
    {
        ServiceState state;
        lock (_sync)
        {
            message ??= status == _state.Status ? _state.Message : null;
            state = new ServiceState(status, _pending.Count, status == ServiceStatus.TargetMissing ? _workspace.Spool.Count : 0, _lastBackupUtc, message, progress, _alarm);
            if (state == _state)
                return;
            _state = state;
        }
        StateChanged?.Invoke(this, state);
    }

    private static readonly JsonSerializerOptions AlarmJson = new() { WriteIndented = true };

    private MassChangeReport? LoadAlarm()
    {
        try
        {
            var file = _workspace.Paths.MassChangeAlarmFile;
            return File.Exists(file) ? JsonSerializer.Deserialize<MassChangeReport>(File.ReadAllText(file), AlarmJson) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SaveAlarm(MassChangeReport report)
    {
        try
        {
            Directory.CreateDirectory(_workspace.Paths.Home);
            File.WriteAllText(_workspace.Paths.MassChangeAlarmFile, JsonSerializer.Serialize(report, AlarmJson));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FilemanLog.Error("Alarmzustand konnte nicht gespeichert werden.", ex);
        }
    }

    private static string Summary(BackupResult result) =>
        result.TotalChanges == 0 && result.ImportedFromSpool == 0
            ? "Alles gesichert."
            : (result.TotalChanges + result.ImportedFromSpool) switch
            {
                1 => "1 Änderung gesichert.",
                var n => $"{n} Änderungen gesichert.",
            };

    private static void Log(BackupResult result)
    {
        if (result.TotalChanges > 0 || result.Errors.Count > 0 || result.ImportedFromSpool > 0 || result.Trigger != BackupTrigger.Watcher)
            FilemanLog.Info($"Sicherung ({result.Trigger}): {result.FilesScanned} geprüft, {result.Created} neu, {result.Modified} geändert, " +
                            $"{result.Moved} verschoben, {result.Deleted} gelöscht, {result.ImportedFromSpool} aus Zwischenspeicher, {result.Errors.Count} Fehler.");
        foreach (var error in result.Errors)
            FilemanLog.Warn($"{error.Path}: {error.Message}");
        foreach (var root in result.SkippedRoots)
            FilemanLog.Warn($"Quellordner nicht erreichbar: {root}");
        foreach (var dir in result.UnreadableDirectories)
            FilemanLog.Warn($"Ordner nicht lesbar: {dir.Path}: {dir.Message}");
    }

    private sealed class InlineProgress(Action<BackupProgress> report) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => report(value);
    }
}
