using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace Fileman.Core.Scanning;

/// <summary>Eine auf der Platte gefundene Datei (nur Metadaten, ohne Inhalt zu lesen).</summary>
public sealed record DiskFile(string Path, long Size, DateTime LastWriteUtc);

/// <summary>Ergebnis eines Durchlaufs. Nicht lesbare Ordner werden gemeldet, damit ihr Inhalt nicht als gelöscht gilt.</summary>
public sealed record ScanResult(IReadOnlyList<DiskFile> Files, IReadOnlyList<ScanFailure> FailedDirectories);

public sealed record ScanFailure(string Directory, string Message);

/// <summary>
/// Zählt Dateien auf, ohne Inhalte zu lesen. Mehrere Threads arbeiten eine gemeinsame Ordner-Warteschlange ab;
/// das bringt besonders auf Netzlaufwerken und SSDs ein Vielfaches an Geschwindigkeit.
/// </summary>
public sealed class FileScanner
{
    // Nicht im .NET-Enum enthalten: Cloud-Platzhalter (z. B. OneDrive „nur online verfügbar“).
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    private static readonly EnumerationOptions SingleDirectory = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    private readonly ExclusionFilter _filter;
    private readonly IReadOnlyCollection<string> _skipDirectories;
    private readonly int _parallelism;

    /// <param name="skipDirectories">Ordner, die nie durchsucht werden (z. B. das Archiv selbst).</param>
    /// <param name="parallelism">Anzahl gleichzeitig durchsuchter Ordner.</param>
    public FileScanner(ExclusionFilter filter, IReadOnlyCollection<string>? skipDirectories = null, int parallelism = 0)
    {
        _filter = filter;
        _skipDirectories = skipDirectories ?? [];
        _parallelism = parallelism > 0 ? parallelism : DefaultParallelism;
    }

    public static int DefaultParallelism => Math.Clamp(Environment.ProcessorCount, 2, 16);

    public ScanResult Scan(string directory, bool recursive = true, Action<int>? onProgress = null, CancellationToken cancellationToken = default)
    {
        var files = new ConcurrentBag<DiskFile>();
        var failures = new ConcurrentBag<ScanFailure>();
        using var queue = new BlockingCollection<string>(new ConcurrentQueue<string>());
        var pending = 1;
        var found = 0;
        queue.Add(directory, cancellationToken);

        void Worker()
        {
            foreach (var current in queue.GetConsumingEnumerable(cancellationToken))
            {
                try
                {
                    var added = ScanDirectory(current, recursive, files, failures, sub =>
                    {
                        Interlocked.Increment(ref pending);
                        queue.Add(sub, cancellationToken);
                    });
                    onProgress?.Invoke(Interlocked.Add(ref found, added));
                }
                finally
                {
                    if (Interlocked.Decrement(ref pending) == 0)
                        queue.CompleteAdding();
                }
            }
        }

        var workers = Enumerable.Range(0, recursive ? _parallelism : 1)
            .Select(_ => Task.Factory.StartNew(Worker, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToArray();
        try
        {
            Task.WaitAll(workers, cancellationToken);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            throw new OperationCanceledException(cancellationToken);
        }
        return new ScanResult([.. files], [.. failures]);
    }

    private int ScanDirectory(string directory, bool recursive, ConcurrentBag<DiskFile> files, ConcurrentBag<ScanFailure> failures, Action<string> enqueue)
    {
        var added = 0;
        try
        {
            var entries = new FileSystemEnumerable<(string Path, bool IsDirectory, long Size, DateTime LastWriteUtc)>(directory,
                (ref FileSystemEntry e) => (e.ToFullPath(), e.IsDirectory, e.Length, e.LastWriteTimeUtc.UtcDateTime),
                SingleDirectory)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) =>
                    !_filter.IsExcluded(e.FileName.ToString())
                    && (e.IsDirectory
                        ? (e.Attributes & FileAttributes.ReparsePoint) == 0 // keine Junctions/Symlinks (Schleifen!)
                        : (e.Attributes & (FileAttributes.Offline | RecallOnDataAccess | RecallOnOpen)) == 0),
            };
            foreach (var entry in entries)
            {
                if (!entry.IsDirectory)
                {
                    files.Add(new DiskFile(entry.Path, entry.Size, entry.LastWriteUtc));
                    added++;
                }
                else if (recursive && !IsSkipped(entry.Path))
                    enqueue(entry.Path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            failures.Add(new ScanFailure(directory, ex.Message));
        }
        return added;
    }

    private bool IsSkipped(string path) => _skipDirectories.Any(skip => PathUtil.IsUnder(path, skip));
}
