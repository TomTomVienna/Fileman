namespace Fileman.Core.Protection;

/// <summary>Einstellungen des Schutzes vor Massenänderungen (Teil von settings.json).</summary>
public sealed class MassChangeSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Ab so vielen geänderten oder gelöschten Dateien (innerhalb des Zeitfensters) wird angehalten.</summary>
    public int MinFiles { get; set; } = 200;

    /// <summary>Bei vollständigen Abgleichen zusätzlich: mindestens dieser Anteil aller Dateien (Prozent).</summary>
    public int MinPercent { get; set; } = 10;

    /// <summary>Zeitfenster, in dem Änderungen aufsummiert werden (Automatik-Modus).</summary>
    public int WindowMinutes { get; set; } = 10;

    /// <summary>Ab so vielen Dateien, deren Inhalt nicht mehr zur Endung passt (z. B. verschlüsselt), wird angehalten.</summary>
    public int SuspiciousContentMinFiles { get; set; } = 20;
}

/// <summary>Was den Alarm ausgelöst hat – für die Anzeige.</summary>
public sealed record MassChangeReport(
    DateTime DetectedUtc,
    string Reason,
    int ChangedFiles,
    int DeletedFiles,
    int SuspiciousContentFiles,
    IReadOnlyList<string> Examples,
    IReadOnlyList<string> Folders);

/// <summary>Die Sicherung wurde angehalten, weil ungewöhnlich viele Dateien betroffen sind.</summary>
public sealed class MassChangeException(MassChangeReport report) : FilemanException(report.Reason)
{
    public MassChangeReport Report { get; } = report;
}

/// <summary>
/// Erkennt ungewöhnlich viele Änderungen (z. B. durch einen Verschlüsselungstrojaner) und hält die Sicherung an,
/// bevor der Bestand auf der Platte mit unbrauchbaren Ständen „zugemüllt“ wird. Ältere Versionen bleiben ohnehin
/// immer erhalten; der Schutz verhindert vor allem, dass der „aktuelle Stand“ im Archiv ersetzt wird.
/// Ein Exemplar lebt so lange wie der Automatik-Modus, damit auch langsame Angriffe über mehrere Läufe auffallen.
/// </summary>
public sealed class MassChangeGuard(MassChangeSettings settings, TimeProvider? time = null)
{
    private const int MaxExamples = 15;
    private readonly object _sync = new();
    private readonly List<(DateTime Utc, int Changes, int Suspicious)> _history = [];
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public MassChangeSettings Settings { get; set; } = settings;

    /// <summary>Einmalig überspringen (der Anwender hat bestätigt, dass die Änderungen gewollt sind).</summary>
    public bool BypassNextRun { get; set; }

    internal bool IsActive => Settings.Enabled && !BypassNextRun;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Vor dem Schreiben: geplante Änderungen bekannter Dateien prüfen.
    /// </summary>
    /// <param name="fullRun">Vollständiger Abgleich: dann zählt zusätzlich der Anteil am Gesamtbestand.</param>
    /// <param name="totalDocuments">Anzahl aller bekannten Dateien.</param>
    internal void CheckPlanned(int changed, int deleted, bool fullRun, int totalDocuments, IReadOnlyList<string> affectedPaths)
    {
        if (!IsActive)
            return;
        var planned = changed + deleted;
        var (recentChanges, _) = Recent();
        var threshold = fullRun
            ? Math.Max(Settings.MinFiles, (int)Math.Ceiling(totalDocuments * Settings.MinPercent / 100.0))
            : Settings.MinFiles;
        var counted = fullRun ? planned : planned + recentChanges;
        if (planned == 0 || counted < threshold)
            return;

        var reason = fullRun
            ? $"Seit der letzten Sicherung wurden {planned} Dateien geändert oder gelöscht – ungewöhnlich viele."
            : $"In den letzten {Settings.WindowMinutes} Minuten wurden {counted} Dateien geändert oder gelöscht – ungewöhnlich viele.";
        throw new MassChangeException(CreateReport(reason, changed, deleted, 0, affectedPaths));
    }

    /// <summary>Während der Sicherung: zu viele Dateien, deren Inhalt nicht mehr zur Endung passt?</summary>
    internal void CheckContent(int suspicious, int knownFormat, int changed, IReadOnlyList<string> suspiciousPaths)
    {
        if (!IsActive || suspicious == 0)
            return;
        var (_, recentSuspicious) = Recent();
        var total = suspicious + recentSuspicious;
        // Mindestanzahl und mindestens die Hälfte der geprüften Dateien dieses Laufs – einzelne „seltsame“ Dateien sind normal.
        if (total < Settings.SuspiciousContentMinFiles || suspicious * 2 < knownFormat)
            return;
        throw new MassChangeException(CreateReport(
            $"{total} Dateien sehen nach der Änderung nicht mehr wie gültige Dokumente aus (z. B. verschlüsselt).",
            changed, 0, total, suspiciousPaths));
    }

    /// <summary>Nach einem erfolgreichen Lauf: für das Zeitfenster merken.</summary>
    internal void RecordCompleted(int changes, int suspicious)
    {
        lock (_sync)
        {
            if (BypassNextRun)
            {
                // Bestätigte Änderungen nicht weiter mitzählen.
                BypassNextRun = false;
                _history.Clear();
                return;
            }
            _history.Add((Now, changes, suspicious));
        }
    }

    private (int Changes, int Suspicious) Recent()
    {
        lock (_sync)
        {
            var since = Now - TimeSpan.FromMinutes(Settings.WindowMinutes);
            _history.RemoveAll(h => h.Utc < since);
            return (_history.Sum(h => h.Changes), _history.Sum(h => h.Suspicious));
        }
    }

    private MassChangeReport CreateReport(string reason, int changed, int deleted, int suspicious, IReadOnlyList<string> paths)
    {
        var folders = paths
            .GroupBy(p => Path.GetDirectoryName(p) ?? "", PathUtil.Comparer)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => $"{g.Key}  ({g.Count()} Dateien)")
            .ToList();
        return new MassChangeReport(Now, reason, changed, deleted, suspicious, paths.Take(MaxExamples).ToList(), folders);
    }
}
