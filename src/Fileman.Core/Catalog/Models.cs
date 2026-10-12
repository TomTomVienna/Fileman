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
}

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
    public long OriginalBytes { get; set; }
    public long StoredBytes { get; set; }
}
