namespace Fileman.Core.Catalog;

/// <summary>Ein Dokument über seine gesamte Lebenszeit, unabhängig von Umbenennungen und Verschiebungen.</summary>
public sealed class DocumentRecord
{
    public long Id { get; set; }
    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
    public string? FileId { get; set; }
    public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public string Hash { get; set; } = "";
    public bool IsDeleted { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime? DeletedUtc { get; set; }
    public int VersionCount { get; set; }
}

/// <summary>Ein gesicherter Inhaltsstand eines Dokuments.</summary>
public sealed class VersionRecord
{
    public long Id { get; set; }
    public long DocumentId { get; set; }
    public string Hash { get; set; } = "";
    public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }
    /// <summary>Pfad zum Zeitpunkt der Sicherung.</summary>
    public string Path { get; set; } = "";
    public DateTime CapturedUtc { get; set; }
    public BackupTrigger Trigger { get; set; }
    /// <summary>Datei im Archiv (relativ zum Ordner "Dateien"), z. B. "D\Mandanten\Bilanz (2026-10-12 14-32).xlsx".</summary>
    public string ArchivePath { get; set; } = "";
}

/// <summary>Ein eingelesener Altbestand (z. B. ein Datumsordner einer früheren Sicherung).</summary>
/// <param name="SnapshotUtc">Stand der Sicherung: Datum aus dem Ordnernamen, sonst jüngstes Dateidatum.</param>
public sealed record LegacySourceRecord(long Id, string Path, string Name, DateTime SnapshotUtc, DateTime? ImportedUtc,
    DateTime? ConsolidatedUtc, int Files, long Bytes);

/// <summary>Eine Datei eines Altbestands; RelativePath ist relativ zum Altbestand-Ordner.</summary>
public sealed record LegacyFileRecord(long SourceId, string RelativePath, long Size, DateTime LastWriteUtc, string Hash, string ArchivePath);

/// <summary>Ein von Fileman im Archivordner "Dateien" angelegter Name.</summary>
public sealed record ArchiveFileRecord(string RelativePath, string Hash, long DocumentId, bool IsCurrent);

public sealed class PathChangeRecord
{
    public long DocumentId { get; set; }
    public string OldPath { get; set; } = "";
    public string NewPath { get; set; } = "";
    public DateTime ChangedUtc { get; set; }
}

public enum ChangeKind
{
    Created,
    Modified,
    Renamed,
    Moved,
    Deleted,
    Recreated,
}

/// <summary>Ein Eintrag für die Anzeige „Letzte Änderungen“.</summary>
public sealed class ChangeEvent
{
    public long Id { get; set; }
    public DateTime Utc { get; set; }
    public long DocumentId { get; set; }
    public ChangeKind Kind { get; set; }
    public string Path { get; set; } = "";
    public string? OldPath { get; set; }
}

public enum BackupTrigger
{
    /// <summary>Vom Anwender angestoßen („Jetzt sichern“).</summary>
    Manual,
    /// <summary>Durch den Dateisystem-Wächter ausgelöst.</summary>
    Watcher,
    /// <summary>Regelmäßiger Abgleich oder Abgleich beim Start.</summary>
    Reconcile,
    /// <summary>Aus einem Altbestand (alte Sicherung) übernommen.</summary>
    Legacy,
}

public sealed class RunRecord
{
    public long Id { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public BackupTrigger Trigger { get; set; }
    public int FilesScanned { get; set; }
    public int Changes { get; set; }
    public int Errors { get; set; }
}

public sealed class CatalogStatistics
{
    public int LiveDocuments { get; set; }
    public int DeletedDocuments { get; set; }
    public int Versions { get; set; }
    public int Blobs { get; set; }
    /// <summary>Summe aller Versionen (so viel Platz bräuchte man ohne Hardlinks/Deduplizierung).</summary>
    public long OriginalBytes { get; set; }
    /// <summary>Tatsächlich belegter Platz (jeder Inhalt einmal).</summary>
    public long StoredBytes { get; set; }
}
