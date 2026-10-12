using System.IO.Enumeration;

namespace Fileman.Core.Scanning;

/// <summary>Eine auf der Platte gefundene Datei (nur Metadaten, ohne Inhalt zu lesen).</summary>
public sealed record DiskFile(string Path, long Size, DateTime LastWriteUtc);

/// <summary>Zählt Dateien unterhalb eines Ordners auf, schnell und ohne Inhalte zu lesen.</summary>
public sealed class FileScanner
{
    // Nicht im .NET-Enum enthalten: Cloud-Platzhalter (z. B. OneDrive „nur online verfügbar“).
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    private readonly ExclusionFilter _filter;
    private readonly IReadOnlyCollection<string> _skipDirectories;

    /// <param name="skipDirectories">Ordner, die nie durchsucht werden (z. B. das Archiv selbst).</param>
    public FileScanner(ExclusionFilter filter, IReadOnlyCollection<string>? skipDirectories = null)
    {
        _filter = filter;
        _skipDirectories = skipDirectories ?? [];
    }

    public IEnumerable<DiskFile> Enumerate(string directory, bool recursive = true)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };
        var enumerable = new FileSystemEnumerable<DiskFile>(directory,
            (ref FileSystemEntry entry) => new DiskFile(entry.ToFullPath(), entry.Length, entry.LastWriteTimeUtc.UtcDateTime),
            options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                !entry.IsDirectory
                && !_filter.IsExcluded(entry.FileName.ToString())
                && (entry.Attributes & (FileAttributes.Offline | RecallOnDataAccess | RecallOnOpen)) == 0,
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                (entry.Attributes & FileAttributes.ReparsePoint) == 0
                && !_filter.IsExcluded(entry.FileName.ToString())
                && !IsSkipped(entry.ToFullPath()),
        };
        return enumerable;
    }

    private bool IsSkipped(string path) => _skipDirectories.Any(skip => PathUtil.IsUnder(path, skip));
}
