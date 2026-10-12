using System.Text.Json;
using Fileman.Core.Archive;
using Fileman.Core.Catalog;
using Fileman.Core.Storage;

namespace Fileman.Core.Automation;

/// <summary>Ein Dateistand, der zwischengespeichert wurde, während die Sicherungsplatte nicht angesteckt war.</summary>
public sealed record SpoolEntry(string Path, long Size, DateTime LastWriteUtc, string Hash, long StoredSize, DateTime CapturedUtc);

/// <summary>
/// Lokaler Zwischenspeicher (%LOCALAPPDATA%\Fileman\spool). Hält Zwischenstände fest, solange die Platte fehlt,
/// und überträgt sie beim nächsten Einstecken ins Archiv. Intern komprimiert (gzip); im Archiv landen sie lesbar.
/// </summary>
public sealed class Spool
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly object _sync = new();
    private readonly string _journal;
    private long _storedBytes = -1;

    public Spool(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
        Blobs = new BlobStore(Path.Combine(directory, "blobs"));
        _journal = Path.Combine(directory, "journal.jsonl");
    }

    public string Directory { get; }

    public BlobStore Blobs { get; }

    public IReadOnlyList<SpoolEntry> ReadAll()
    {
        lock (_sync)
        {
            if (!File.Exists(_journal))
                return [];
            var entries = new List<SpoolEntry>();
            foreach (var line in File.ReadAllLines(_journal))
            {
                try
                {
                    if (JsonSerializer.Deserialize<SpoolEntry>(line, JsonOptions) is { } entry && Blobs.Exists(entry.Hash))
                        entries.Add(entry);
                }
                catch (JsonException)
                {
                    // unvollständige letzte Zeile nach Absturz
                }
            }
            return entries;
        }
    }

    public int Count => ReadAll().Count;

    /// <summary>Belegter Platz der zwischengespeicherten Inhalte.</summary>
    public long StoredBytes
    {
        get
        {
            lock (_sync)
            {
                if (_storedBytes < 0)
                    _storedBytes = ReadAll().DistinctBy(e => e.Hash).Sum(e => e.StoredSize);
                return _storedBytes;
            }
        }
    }

    /// <summary>Speichert den aktuellen Stand einer Datei zwischen.</summary>
    public SpoolEntry Add(string path, DateTime capturedUtc, CancellationToken cancellationToken = default)
    {
        var before = new FileInfo(path);
        var content = Blobs.Store(path, cancellationToken);
        var entry = new SpoolEntry(path, content.Size, before.LastWriteTimeUtc, content.Hash, content.StoredSize, capturedUtc);
        lock (_sync)
        {
            using (var stream = new FileStream(_journal, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream))
            {
                writer.WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            if (_storedBytes >= 0 && content.IsNew)
                _storedBytes += content.StoredSize;
        }
        return entry;
    }

    /// <summary>
    /// Überträgt alle Zwischenstände ins Archiv und leert danach den Zwischenspeicher.
    /// Muss nach einem vollständigen Abgleich laufen, damit Verschiebungen bereits erkannt sind.
    /// </summary>
    /// <returns>Anzahl übernommener Versionen.</returns>
    public int ImportInto(BackupArchive archive, DateTime nowUtc)
    {
        var entries = ReadAll();
        if (entries.Count == 0)
        {
            Clear();
            return 0;
        }
        var catalog = archive.Catalog;
        var layout = new ArchiveLayout(archive.Store, catalog, nowUtc);
        var imported = 0;
        catalog.BeginTransaction();
        try
        {
            foreach (var entry in entries.OrderBy(e => e.CapturedUtc))
            {
                var doc = catalog.FindDocumentByAnyPath(entry.Path);
                var isNew = doc is null;
                if (doc is null)
                {
                    // Entstanden und wieder verschwunden, während die Platte fehlte.
                    doc = new DocumentRecord
                    {
                        Path = entry.Path,
                        FileName = System.IO.Path.GetFileName(entry.Path),
                        Size = entry.Size,
                        LastWriteUtc = entry.LastWriteUtc,
                        Hash = entry.Hash,
                        FirstSeenUtc = entry.CapturedUtc,
                        IsDeleted = true,
                        DeletedUtc = nowUtc,
                    };
                    catalog.InsertDocument(doc);
                }
                else if (catalog.HasVersionWithHash(doc.Id, entry.Hash))
                {
                    continue;
                }

                StagedContent? staged = null;
                if (catalog.GetBlobPath(entry.Hash) is null)
                {
                    using var content = Blobs.OpenRead(entry.Hash);
                    staged = archive.Store.Stage(content);
                }
                try
                {
                    var archivePath = layout.PlaceVersion(doc.Id, entry.Path, entry.Hash, entry.Size, entry.LastWriteUtc, staged);
                    catalog.InsertVersion(new VersionRecord
                    {
                        DocumentId = doc.Id,
                        Hash = entry.Hash,
                        Size = entry.Size,
                        LastWriteUtc = entry.LastWriteUtc,
                        Path = entry.Path,
                        CapturedUtc = entry.CapturedUtc,
                        Trigger = BackupTrigger.Watcher,
                        ArchivePath = archivePath,
                    }, runId: null);
                }
                finally
                {
                    if (staged is not null)
                        ArchiveStore.Discard(staged.TempPath);
                }
                if (isNew)
                    layout.SetCurrent(doc);
                catalog.InsertEvent(new ChangeEvent { Utc = entry.CapturedUtc, DocumentId = doc.Id, Kind = ChangeKind.Modified, Path = entry.Path });
                imported++;
            }
            catalog.Commit();
        }
        catch
        {
            catalog.Rollback();
            throw;
        }
        Clear();
        return imported;
    }

    public void Clear()
    {
        lock (_sync)
        {
            if (File.Exists(_journal))
                File.Delete(_journal);
            foreach (var file in System.IO.Directory.EnumerateFiles(Blobs.Directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            _storedBytes = 0;
        }
    }
}
