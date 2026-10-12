namespace Fileman.Core.Legacy;

/// <summary>Eine alte Sicherung: ein Datumsordner mit einer Kopie des Teilbaums.</summary>
/// <param name="Path">Vollständiger Pfad des Datumsordners.</param>
/// <param name="DateFromName">Datum aus dem Ordnernamen, falls erkennbar.</param>
/// <param name="NewestFileUtc">Jüngstes Änderungsdatum einer Datei darin (Ersatz, wenn der Name kein Datum enthält).</param>
/// <param name="TopFolders">Ordner der ersten Ebene – zeigt, ob überall derselbe Teilbaum liegt.</param>
public sealed record LegacySnapshot(
    string Path,
    string Name,
    DateTime? DateFromName,
    DateTime NewestFileUtc,
    int Files,
    long Bytes,
    IReadOnlyList<string> TopFolders)
{
    /// <summary>Zeitpunkt für die Reihenfolge der Sicherungen.</summary>
    public DateTime EffectiveDate => DateFromName ?? NewestFileUtc;
}

/// <summary>
/// Ein Stand einer Datei: gleicher relativer Pfad, gleiche Größe, gleiches Änderungsdatum.
/// Explorer-Kopien behalten Größe und Datum – gleiche Stände in mehreren Sicherungen sind daher Duplikate.
/// </summary>
public sealed record LegacyFileState(string RelativePath, long Size, DateTime LastWriteUtc, IReadOnlyList<int> SnapshotIndexes)
{
    /// <summary>Erste Sicherung (Index), in der dieser Stand vorkommt.</summary>
    public int FirstSnapshot => SnapshotIndexes[0];
}

/// <summary>Gleiche Datei (Name, Größe, Datum) an verschiedenen Orten – vermutlich verschoben oder umbenannt.</summary>
public sealed record LegacyPossibleMove(string FileName, long Size, DateTime LastWriteUtc, IReadOnlyList<string> RelativePaths);

/// <summary>Ergebnis der Analyse alter Sicherungen. Es wurde nichts verändert.</summary>
public sealed class LegacyAnalysis
{
    public required string RootPath { get; init; }
    public required DateTime CreatedUtc { get; init; }

    /// <summary>Sicherungen in zeitlicher Reihenfolge.</summary>
    public required IReadOnlyList<LegacySnapshot> Snapshots { get; init; }

    /// <summary>Alle unterschiedlichen Stände, sortiert nach Pfad und Datum.</summary>
    public required IReadOnlyList<LegacyFileState> States { get; init; }

    public required IReadOnlyList<LegacyPossibleMove> PossibleMoves { get; init; }

    /// <summary>Ordner, die nicht gelesen werden konnten.</summary>
    public required IReadOnlyList<string> UnreadableFolders { get; init; }

    public int TotalFiles => Snapshots.Sum(s => s.Files);
    public long TotalBytes => Snapshots.Sum(s => s.Bytes);
    public int DistinctStates => States.Count;
    public long DistinctBytes => States.Sum(s => s.Size);
    public int DuplicateFiles => TotalFiles - DistinctStates;
    public long DuplicateBytes => TotalBytes - DistinctBytes;

    /// <summary>Unterschiedliche Dateien (relative Pfade).</summary>
    public int DistinctPaths => States.Select(s => s.RelativePath).Distinct(PathUtil.Comparer).Count();

    /// <summary>Dateien, die im Lauf der Zeit mehr als einen Stand hatten.</summary>
    public IReadOnlyList<IGrouping<string, LegacyFileState>> FilesWithSeveralStates =>
        States.GroupBy(s => s.RelativePath, PathUtil.Comparer).Where(g => g.Count() > 1).ToList();

    /// <summary>Dateien, die in der jüngsten Sicherung nicht mehr vorkommen (später gelöscht, umbenannt oder verschoben).</summary>
    public IReadOnlyList<string> MissingInNewest
    {
        get
        {
            if (Snapshots.Count == 0)
                return [];
            var newest = Snapshots.Count - 1;
            return States.GroupBy(s => s.RelativePath, PathUtil.Comparer)
                .Where(g => g.All(s => !s.SnapshotIndexes.Contains(newest)))
                .Select(g => g.Key)
                .ToList();
        }
    }

    /// <summary>Neue oder geänderte Stände, die eine Sicherung gegenüber allen früheren hinzugebracht hat.</summary>
    public int NewStatesIn(int snapshotIndex) => States.Count(s => s.FirstSnapshot == snapshotIndex);
}
