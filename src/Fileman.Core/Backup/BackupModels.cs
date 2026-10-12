using Fileman.Core.Catalog;

namespace Fileman.Core.Backup;

/// <summary>Ein zu durchsuchender Bereich. Ein Gesamtlauf umfasst alle Quellordner rekursiv.</summary>
public sealed record ScanScope(string Path, bool Recursive = true);

public sealed class BackupOptions
{
    /// <summary>Alle überwachten Quellordner.</summary>
    public required IReadOnlyList<string> Roots { get; init; }

    /// <summary>Nur diese Bereiche durchsuchen (Automatik-Modus). Null = alle Quellordner vollständig.</summary>
    public IReadOnlyList<ScanScope>? Scopes { get; init; }

    public BackupTrigger Trigger { get; init; } = BackupTrigger.Manual;

    /// <summary>
    /// Wie lange eine gelöschte Datei als „vielleicht verschoben“ gilt. Taucht in dieser Zeit derselbe Inhalt
    /// oder dieselbe Datei-ID woanders auf, wird das als Verschiebung erkannt statt als neue Datei.
    /// </summary>
    public TimeSpan MoveDetectionWindow { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Anzahl gleichzeitig durchsuchter Ordner (0 = automatisch).</summary>
    public int ScanParallelism { get; init; }

    /// <summary>Anzahl gleichzeitig gelesener und gesicherter Dateien (0 = automatisch).</summary>
    public int FileParallelism { get; init; }

    /// <summary>Schutz vor Massenänderungen; null = keine Prüfung.</summary>
    public Protection.MassChangeGuard? Guard { get; init; }

    public static int DefaultFileParallelism => Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
}

public enum BackupPhase
{
    Scanning,
    Saving,
    Finishing,
}

public sealed record BackupProgress(BackupPhase Phase, int Processed, int Total, string? CurrentFile);

public sealed record BackupError(string Path, string Message);

public sealed class BackupResult
{
    public long RunId { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    public int FilesScanned { get; set; }
    public int Unchanged { get; set; }
    public int Created { get; set; }
    public int Modified { get; set; }
    public int Moved { get; set; }
    public int Deleted { get; set; }
    /// <summary>Bytes, die tatsächlich neu auf die Platte geschrieben wurden (komprimiert, ohne Doppelte).</summary>
    public long BytesStored { get; set; }
    public List<BackupError> Errors { get; } = [];
    /// <summary>Quellordner, die nicht erreichbar waren. Ihre Dateien werden NICHT als gelöscht markiert.</summary>
    public List<string> SkippedRoots { get; } = [];
    /// <summary>Ordner, die nicht gelesen werden konnten. Ihre Dateien werden ebenfalls NICHT als gelöscht markiert.</summary>
    public List<BackupError> UnreadableDirectories { get; } = [];
    public List<ChangeEvent> Changes { get; } = [];
    /// <summary>Gesicherte Dateien, deren Inhalt nicht zur Endung passt (nur bei bestätigten Massenänderungen &gt; 0).</summary>
    public int SuspiciousContent { get; set; }
    /// <summary>Zwischenstände, die aus dem lokalen Zwischenspeicher übernommen wurden.</summary>
    public int ImportedFromSpool { get; set; }

    public int TotalChanges => Created + Modified + Moved + Deleted;

    public BackupTrigger Trigger { get; set; }
}
