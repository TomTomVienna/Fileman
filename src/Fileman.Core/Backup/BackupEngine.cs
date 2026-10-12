using System.Diagnostics;
using Fileman.Core.Archive;
using Fileman.Core.Catalog;
using Fileman.Core.Identity;
using Fileman.Core.Scanning;
using Fileman.Core.Storage;

namespace Fileman.Core.Backup;

/// <summary>
/// Vergleicht die Quellordner mit dem Katalog und sichert alle Änderungen (Delta-Sicherung).
/// <para>
/// Wiedererkennung eines Dokuments an neuem Ort, in dieser Reihenfolge:
/// 1. gleicher Pfad, 2. gleiche Datei-ID (NTFS), 3. gleicher Inhalt (Hash), 4. gleicher Dateiname
/// – jeweils nur unter den Dokumenten, die an ihrem bisherigen Ort fehlen oder kürzlich gelöscht wurden.
/// </para>
/// </summary>
public sealed class BackupEngine
{
    private const int CommitEveryFiles = 50;
    private static readonly TimeSpan CommitInterval = TimeSpan.FromSeconds(3);
    private const int MaxReadAttempts = 3;

    private readonly BackupArchive _archive;
    private readonly IFileIdentityProvider _identity;
    private readonly ExclusionFilter _filter;
    private readonly TimeProvider _time;

    public BackupEngine(BackupArchive archive, IFileIdentityProvider identity, ExclusionFilter filter, TimeProvider? time = null)
    {
        if (archive.IsReadOnly)
            throw new ArgumentException("Das Archiv ist schreibgeschützt geöffnet.", nameof(archive));
        _archive = archive;
        _identity = identity;
        _filter = filter;
        _time = time ?? TimeProvider.System;
    }

    private CatalogDatabase Catalog => _archive.Catalog;
    private BlobStore Blobs => _archive.Blobs;

    public BackupResult Run(BackupOptions options, IProgress<BackupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var run = new RunState(this, options, progress, cancellationToken);
        return run.Execute();
    }

    /// <summary>Zustand eines einzelnen Laufs.</summary>
    private sealed class RunState(BackupEngine engine, BackupOptions options, IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        private readonly BackupResult _result = new();
        private readonly Dictionary<string, DocumentRecord> _byPath = new();
        private readonly Dictionary<string, List<DocumentRecord>> _byFileId = new();
        private readonly HashSet<long> _unclaimed = [];
        private readonly List<DocumentRecord> _missing = [];
        private List<DocumentRecord> _recentlyDeleted = [];
        private long _runId;
        private DateTime _now;

        private CatalogDatabase Catalog => engine.Catalog;

        public BackupResult Execute()
        {
            _now = engine._time.GetUtcNow().UtcDateTime;
            _result.StartedUtc = _now;
            _runId = Catalog.StartRun(options.Trigger, _now);
            _result.RunId = _runId;

            var completed = false;
            try
            {
                Report(BackupPhase.Scanning, 0, 0, null);
                var roots = options.Roots.Select(PathUtil.Normalize).Distinct(PathUtil.Comparer).ToList();
                var (diskFiles, coveredScopes) = ScanDisk(roots);
                _result.FilesScanned = diskFiles.Count;
                LoadCatalogState(diskFiles, coveredScopes);

                var toProcess = new List<DiskFile>();
                foreach (var file in diskFiles.Values)
                {
                    if (_byPath.TryGetValue(PathUtil.Key(file.Path), out var doc) && doc.Size == file.Size && doc.LastWriteUtc == file.LastWriteUtc
                        && string.Equals(doc.Path, file.Path, StringComparison.Ordinal))
                        _result.Unchanged++;
                    else
                        toProcess.Add(file);
                }
                toProcess.Sort((a, b) => PathUtil.Comparer.Compare(a.Path, b.Path));

                Catalog.BeginTransaction();
                var sinceCommit = Stopwatch.StartNew();
                var pending = 0;
                for (var i = 0; i < toProcess.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var file = toProcess[i];
                    Report(BackupPhase.Saving, i, toProcess.Count, file.Path);
                    try
                    {
                        ProcessFile(file);
                    }
                    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                    {
                        // Zwischen Durchsuchen und Sichern gelöscht – wird beim nächsten Lauf erkannt.
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _result.Errors.Add(new BackupError(file.Path, ex.Message));
                    }

                    if (++pending >= CommitEveryFiles || sinceCommit.Elapsed > CommitInterval)
                    {
                        Catalog.Commit();
                        Catalog.BeginTransaction();
                        pending = 0;
                        sinceCommit.Restart();
                    }
                }

                Report(BackupPhase.Finishing, toProcess.Count, toProcess.Count, null);
                foreach (var doc in _missing.Where(d => _unclaimed.Contains(d.Id)))
                    MarkDeleted(doc);

                completed = true;
                return _result;
            }
            finally
            {
                // Auch bei Abbruch bleibt alles bereits Gesicherte erhalten.
                Catalog.Commit();
                _result.FinishedUtc = engine._time.GetUtcNow().UtcDateTime;
                Catalog.FinishRun(_runId, _result.FinishedUtc, _result.FilesScanned, _result.TotalChanges,
                    _result.Errors.Count + (completed ? 0 : 1));
            }
        }

        private (Dictionary<string, DiskFile> Files, List<ScanScope> Covered) ScanDisk(List<string> roots)
        {
            var scanner = new FileScanner(engine._filter, [engine._archive.Directory]);
            var files = new Dictionary<string, DiskFile>();
            var covered = new List<ScanScope>();
            var scopes = options.Scopes ?? roots.Select(r => new ScanScope(r)).ToList();

            foreach (var scope in scopes)
            {
                ct.ThrowIfCancellationRequested();
                var path = PathUtil.Normalize(scope.Path);
                var root = roots.FirstOrDefault(r => PathUtil.IsUnder(path, r));
                if (root is null || engine._filter.IsExcludedPath(path, root) || PathUtil.IsUnder(path, engine._archive.Directory))
                    continue;
                if (!Directory.Exists(root))
                {
                    // Quellordner nicht erreichbar (z. B. Netzlaufwerk getrennt): nichts als gelöscht markieren!
                    if (!_result.SkippedRoots.Contains(root, PathUtil.Comparer))
                        _result.SkippedRoots.Add(root);
                    continue;
                }

                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    files[PathUtil.Key(path)] = new DiskFile(path, info.Length, info.LastWriteTimeUtc);
                }
                else if (Directory.Exists(path))
                {
                    foreach (var file in scanner.Enumerate(path, scope.Recursive))
                    {
                        files[PathUtil.Key(file.Path)] = file;
                        if (files.Count % 500 == 0)
                            Report(BackupPhase.Scanning, files.Count, 0, file.Path);
                    }
                }
                covered.Add(scope with { Path = path });
            }
            return (files, covered);
        }

        private void LoadCatalogState(Dictionary<string, DiskFile> diskFiles, List<ScanScope> coveredScopes)
        {
            foreach (var doc in Catalog.GetLiveDocuments())
            {
                _byPath[PathUtil.Key(doc.Path)] = doc;
                IndexFileId(doc);
                if (!diskFiles.ContainsKey(PathUtil.Key(doc.Path)) && IsCovered(doc.Path, coveredScopes))
                {
                    _missing.Add(doc);
                    _unclaimed.Add(doc.Id);
                }
            }
            _recentlyDeleted = Catalog.GetDeletedSince(_now - options.MoveDetectionWindow);
        }

        private static bool IsCovered(string path, List<ScanScope> scopes) =>
            scopes.Any(s => s.Recursive
                ? PathUtil.IsUnder(path, s.Path)
                : string.Equals(path, s.Path, PathUtil.Comparison)
                  || string.Equals(Path.GetDirectoryName(path), s.Path, PathUtil.Comparison));

        private void IndexFileId(DocumentRecord doc)
        {
            if (doc.FileId is null)
                return;
            if (!_byFileId.TryGetValue(doc.FileId, out var list))
                _byFileId[doc.FileId] = list = [];
            list.Add(doc);
        }

        private void ProcessFile(DiskFile file)
        {
            var key = PathUtil.Key(file.Path);
            var fileId = engine._identity.TryGetFileId(file.Path);

            // 1. Gleicher Pfad: Inhalt geändert (oder nur Datum).
            if (_byPath.TryGetValue(key, out var existing))
            {
                if (!string.Equals(existing.Path, file.Path, StringComparison.Ordinal))
                {
                    // Nur Groß-/Kleinschreibung geändert (Windows).
                    ApplyMove(existing, file.Path, fileId, existing.Size == file.Size && existing.LastWriteUtc == file.LastWriteUtc ? null : Ingest(file.Path));
                    return;
                }
                var stored = Ingest(file.Path);
                if (stored.Content.Hash != existing.Hash)
                    AddVersion(existing, file.Path, stored, ChangeKind.Modified);
                existing.Size = stored.Size;
                existing.LastWriteUtc = stored.LastWriteUtc;
                existing.Hash = stored.Content.Hash;
                SetFileId(existing, fileId);
                Catalog.UpdateDocument(existing);
                return;
            }

            // 2. Gleiche Datei-ID: umbenannt oder verschoben (auf demselben Laufwerk), evtl. auch geändert.
            var candidate = fileId is null ? null : FindByFileId(fileId);
            if (candidate is not null && candidate.Size == file.Size && candidate.LastWriteUtc == file.LastWriteUtc)
            {
                ApplyMove(candidate, file.Path, fileId, stored: null);
                return;
            }

            var content = Ingest(file.Path);
            // 3. Gleicher Inhalt: z. B. über Laufwerksgrenzen verschoben oder kopiert und Original gelöscht.
            candidate ??= FindByHash(content.Content.Hash, Path.GetFileName(file.Path));
            // 4. Gleicher Name unter den fehlenden Dateien (genau ein Treffer): verschoben und dabei geändert.
            candidate ??= FindBySingleName(Path.GetFileName(file.Path));

            if (candidate is not null)
            {
                ApplyMove(candidate, file.Path, fileId, content);
                return;
            }

            var doc = new DocumentRecord
            {
                Path = file.Path,
                FileName = Path.GetFileName(file.Path),
                FileId = fileId,
                Size = content.Size,
                LastWriteUtc = content.LastWriteUtc,
                Hash = content.Content.Hash,
                FirstSeenUtc = _now,
            };
            Catalog.InsertDocument(doc);
            _byPath[key] = doc;
            IndexFileId(doc);
            AddVersion(doc, file.Path, content, ChangeKind.Created);
        }

        private DocumentRecord? FindByFileId(string fileId)
        {
            if (_byFileId.TryGetValue(fileId, out var list) && list.FirstOrDefault(d => _unclaimed.Contains(d.Id)) is { } live)
                return live;
            return _recentlyDeleted.FirstOrDefault(d => d.FileId == fileId);
        }

        private DocumentRecord? FindByHash(string hash, string fileName)
        {
            var candidates = _missing.Where(d => _unclaimed.Contains(d.Id) && d.Hash == hash)
                .Concat(_recentlyDeleted.Where(d => d.Hash == hash))
                .ToList();
            return candidates.FirstOrDefault(d => string.Equals(d.FileName, fileName, PathUtil.Comparison))
                ?? candidates.FirstOrDefault();
        }

        private DocumentRecord? FindBySingleName(string fileName)
        {
            var matches = _missing.Where(d => _unclaimed.Contains(d.Id) && string.Equals(d.FileName, fileName, PathUtil.Comparison)).Take(2).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        private void ApplyMove(DocumentRecord doc, string newPath, string? fileId, IngestResult? stored)
        {
            var oldPath = doc.Path;
            var wasDeleted = doc.IsDeleted;
            _unclaimed.Remove(doc.Id);
            _recentlyDeleted.Remove(doc);
            _byPath.Remove(PathUtil.Key(oldPath));
            _byPath[PathUtil.Key(newPath)] = doc;

            doc.Path = newPath;
            doc.FileName = Path.GetFileName(newPath);
            doc.IsDeleted = false;
            doc.DeletedUtc = null;
            SetFileId(doc, fileId);

            if (!string.Equals(oldPath, newPath, StringComparison.Ordinal))
            {
                Catalog.InsertPathChange(new PathChangeRecord { DocumentId = doc.Id, OldPath = oldPath, NewPath = newPath, ChangedUtc = _now });
                var sameFolder = string.Equals(Path.GetDirectoryName(oldPath), Path.GetDirectoryName(newPath), PathUtil.Comparison);
                AddEvent(doc, sameFolder ? ChangeKind.Renamed : ChangeKind.Moved, oldPath);
                _result.Moved++;
            }
            else if (wasDeleted)
            {
                AddEvent(doc, ChangeKind.Recreated, null);
                _result.Created++;
            }

            if (stored is not null)
            {
                if (stored.Content.Hash != doc.Hash)
                    AddVersion(doc, newPath, stored, ChangeKind.Modified);
                doc.Size = stored.Size;
                doc.LastWriteUtc = stored.LastWriteUtc;
                doc.Hash = stored.Content.Hash;
            }
            Catalog.UpdateDocument(doc);
        }

        private void SetFileId(DocumentRecord doc, string? fileId)
        {
            if (fileId is null || doc.FileId == fileId)
                return;
            doc.FileId = fileId;
            IndexFileId(doc);
        }

        private void MarkDeleted(DocumentRecord doc)
        {
            doc.IsDeleted = true;
            doc.DeletedUtc = _now;
            Catalog.UpdateDocument(doc);
            AddEvent(doc, ChangeKind.Deleted, null);
            _result.Deleted++;
        }

        private void AddVersion(DocumentRecord doc, string path, IngestResult stored, ChangeKind kind)
        {
            Catalog.InsertVersion(new VersionRecord
            {
                DocumentId = doc.Id,
                Hash = stored.Content.Hash,
                Size = stored.Size,
                LastWriteUtc = stored.LastWriteUtc,
                Path = path,
                CapturedUtc = _now,
                Trigger = options.Trigger,
            }, _runId);
            AddEvent(doc, kind, null);
            if (kind == ChangeKind.Created)
                _result.Created++;
            else
                _result.Modified++;
        }

        private void AddEvent(DocumentRecord doc, ChangeKind kind, string? oldPath)
        {
            var change = new ChangeEvent { Utc = _now, DocumentId = doc.Id, Kind = kind, Path = doc.Path, OldPath = oldPath };
            Catalog.InsertEvent(change);
            _result.Changes.Add(change);
        }

        /// <summary>Lagert den Inhalt ein. Ändert sich die Datei währenddessen, wird es erneut versucht.</summary>
        private IngestResult Ingest(string path)
        {
            for (var attempt = 1; ; attempt++)
            {
                var before = new FileInfo(path);
                if (!before.Exists)
                    throw new FileNotFoundException(null, path);
                var content = engine.Blobs.Store(path, ct);
                var after = new FileInfo(path);
                if (after.Exists && after.Length == before.Length && after.LastWriteTimeUtc == before.LastWriteTimeUtc && content.Size == after.Length)
                {
                    Catalog.EnsureBlob(content.Hash, content.Size, content.StoredSize, _now);
                    if (content.IsNew)
                        _result.BytesStored += content.StoredSize;
                    return new IngestResult(content, after.Length, after.LastWriteTimeUtc);
                }
                if (attempt >= MaxReadAttempts)
                    throw new IOException("Die Datei wurde während der Sicherung verändert und wird beim nächsten Lauf gesichert.");
                Thread.Sleep(TimeSpan.FromMilliseconds(300 * attempt));
            }
        }

        private void Report(BackupPhase phase, int processed, int total, string? current) =>
            progress?.Report(new BackupProgress(phase, processed, total, current));
    }

    private sealed record IngestResult(StoredContent Content, long Size, DateTime LastWriteUtc);
}
