using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Fileman.Core.Archive;
using Fileman.Core.Catalog;
using Fileman.Core.Identity;
using Fileman.Core.Protection;
using Fileman.Core.Scanning;
using Fileman.Core.Storage;

namespace Fileman.Core.Backup;

/// <summary>
/// Vergleicht die Quellordner mit dem Katalog und sichert alle Änderungen (Delta-Sicherung).
/// <para>
/// Ablauf: Ordner parallel durchsuchen → geänderte Dateien bestimmen → Dateien parallel lesen, hashen, komprimieren
/// und ablegen → Ergebnisse in fester Reihenfolge von genau einem Thread in den Katalog schreiben (SQLite erlaubt nur
/// einen Schreiber; die feste Reihenfolge macht die Wiedererkennung reproduzierbar).
/// </para>
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
    private ArchiveStore Store => _archive.Store;

    public Task<BackupResult> RunAsync(BackupOptions options, IProgress<BackupProgress>? progress = null, CancellationToken cancellationToken = default) =>
        new RunState(this, options, progress, cancellationToken).ExecuteAsync();

    /// <summary>
    /// Kopiert den Inhalt in den temporären Bereich des Archivs und hasht ihn. Ändert sich die Datei währenddessen,
    /// wird es erneut versucht. Threadsicher.
    /// </summary>
    private IngestResult Ingest(string path, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var before = new FileInfo(path);
            if (!before.Exists)
                throw new FileNotFoundException(null, path);
            var staged = Store.Stage(path, ct);
            var after = new FileInfo(path);
            if (after.Exists && after.Length == before.Length && after.LastWriteTimeUtc == before.LastWriteTimeUtc && staged.Size == after.Length)
                return new IngestResult(staged, after.Length, after.LastWriteTimeUtc);
            ArchiveStore.Discard(staged.TempPath);
            if (attempt >= MaxReadAttempts)
                throw new IOException("Die Datei wurde während der Sicherung verändert und wird beim nächsten Lauf gesichert.");
            ct.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(300 * attempt));
            ct.ThrowIfCancellationRequested();
        }
    }

    private sealed record IngestResult(StagedContent Staged, long Size, DateTime LastWriteUtc)
    {
        public string Hash => Staged.Hash;
    }

    /// <summary>Was ein Arbeits-Thread für eine Datei vorbereitet hat.</summary>
    /// <param name="KnownFormat">Endung mit bekanntem Dateiformat (Office, PDF, Bilder …).</param>
    /// <param name="Suspicious">Inhalt passt nicht zur Endung (z. B. verschlüsselt).</param>
    private sealed record PreparedFile(DiskFile File, string? FileId, IngestResult? Content, Exception? Error,
        bool KnownFormat = false, bool Suspicious = false);

    /// <summary>Zustand eines einzelnen Laufs.</summary>
    private sealed class RunState(BackupEngine engine, BackupOptions options, IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        private readonly BackupResult _result = new();
        private readonly Dictionary<string, DocumentRecord> _byPath = new();
        private readonly Dictionary<string, List<DocumentRecord>> _byFileId = new();
        private readonly HashSet<long> _unclaimed = [];
        private readonly List<DocumentRecord> _missing = [];
        private List<DocumentRecord> _recentlyDeleted = [];
        private readonly List<(string OldPath, string NewPath)> _moves = [];
        private ArchiveLayout? _layout;
        private long _runId;
        private DateTime _now;

        // Unveränderliche Momentaufnahmen für die Arbeits-Threads (der Katalog-Thread ändert die Originale).
        private FrozenSet<string> _knownPathKeys = FrozenSet<string>.Empty;
        private FrozenDictionary<string, (long Size, DateTime LastWriteUtc)[]> _fileIdSnapshot =
            FrozenDictionary<string, (long, DateTime)[]>.Empty;

        private CatalogDatabase Catalog => engine.Catalog;
        private ArchiveLayout Layout => _layout!;

        public async Task<BackupResult> ExecuteAsync()
        {
            _now = engine._time.GetUtcNow().UtcDateTime;
            _result.StartedUtc = _now;
            _runId = Catalog.StartRun(options.Trigger, _now);
            _result.RunId = _runId;
            _result.Trigger = options.Trigger;
            _layout = new ArchiveLayout(engine.Store, Catalog, _now);

            var completed = false;
            try
            {
                Report(BackupPhase.Scanning, 0, 0, null);
                var roots = options.Roots.Select(PathUtil.Normalize).Distinct(PathUtil.Comparer).ToList();
                var (diskFiles, coveredScopes, unreadable) = ScanDisk(roots);
                _result.FilesScanned = diskFiles.Count;
                LoadCatalogState(diskFiles, coveredScopes, unreadable);

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

                CheckPlannedChanges(toProcess);
                await ProcessAllAsync(toProcess).ConfigureAwait(false);

                Report(BackupPhase.Finishing, toProcess.Count, toProcess.Count, null);
                foreach (var doc in _missing.Where(d => _unclaimed.Contains(d.Id)))
                    MarkDeleted(doc);
                WriteFolderMoveMarkers();

                options.Guard?.RecordCompleted(_result.Modified + _result.Deleted, _result.SuspiciousContent);
                completed = true;
                return _result;
            }
            finally
            {
                // Auch bei Abbruch bleibt alles bereits Gesicherte erhalten.
                Catalog.Commit();
                _result.BytesStored = _layout?.BytesStored ?? 0;
                _result.FinishedUtc = engine._time.GetUtcNow().UtcDateTime;
                Catalog.FinishRun(_runId, _result.FinishedUtc, _result.FilesScanned, _result.TotalChanges,
                    _result.Errors.Count + (completed ? 0 : 1));
            }
        }

        // ---------- Durchsuchen ----------

        private (Dictionary<string, DiskFile> Files, List<ScanScope> Covered, List<string> Unreadable) ScanDisk(List<string> roots)
        {
            var scanner = new FileScanner(engine._filter, [engine._archive.Directory], options.ScanParallelism);
            var files = new Dictionary<string, DiskFile>();
            var covered = new List<ScanScope>();
            var unreadable = new List<string>();
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
                    if (!engine._filter.IsExcluded(info.Name))
                        files[PathUtil.Key(path)] = new DiskFile(path, info.Length, info.LastWriteTimeUtc);
                }
                else if (Directory.Exists(path))
                {
                    var scan = scanner.Scan(path, scope.Recursive, count => Report(BackupPhase.Scanning, files.Count + count, 0, null), ct);
                    foreach (var file in scan.Files)
                        files[PathUtil.Key(file.Path)] = file;
                    foreach (var failure in scan.FailedDirectories)
                    {
                        unreadable.Add(failure.Directory);
                        _result.UnreadableDirectories.Add(new BackupError(failure.Directory, failure.Message));
                    }
                }
                covered.Add(scope with { Path = path });
            }
            return (files, covered, unreadable);
        }

        private void LoadCatalogState(Dictionary<string, DiskFile> diskFiles, List<ScanScope> coveredScopes, List<string> unreadable)
        {
            foreach (var doc in Catalog.GetLiveDocuments())
            {
                _byPath[PathUtil.Key(doc.Path)] = doc;
                IndexFileId(doc);
                if (!diskFiles.ContainsKey(PathUtil.Key(doc.Path))
                    && IsCovered(doc.Path, coveredScopes)
                    && !unreadable.Any(dir => PathUtil.IsUnder(doc.Path, dir)))
                {
                    _missing.Add(doc);
                    _unclaimed.Add(doc.Id);
                }
            }
            _recentlyDeleted = Catalog.GetDeletedSince(_now - options.MoveDetectionWindow);

            _knownPathKeys = _byPath.Keys.ToFrozenSet();
            _fileIdSnapshot = _byPath.Values.Concat(_recentlyDeleted)
                .Where(d => d.FileId is not null)
                .GroupBy(d => d.FileId!)
                .ToFrozenDictionary(g => g.Key, g => g.Select(d => (d.Size, d.LastWriteUtc)).ToArray());
        }

        private static bool IsCovered(string path, List<ScanScope> scopes) =>
            scopes.Any(s => s.Recursive
                ? PathUtil.IsUnder(path, s.Path)
                : string.Equals(path, s.Path, PathUtil.Comparison)
                  || string.Equals(Path.GetDirectoryName(path), s.Path, PathUtil.Comparison));

        // ---------- Schutz vor Massenänderungen ----------

        /// <summary>
        /// Vor dem ersten Schreibzugriff: Wie viele bekannte Dateien sind geändert oder verschwunden?
        /// Umbenennungen/Verschiebungen (gleiche Datei-ID, gleiche Größe und gleiches Datum) zählen nicht.
        /// </summary>
        private void CheckPlannedChanges(List<DiskFile> toProcess)
        {
            if (options.Guard is not { IsActive: true } guard)
                return;
            var changed = toProcess.Where(f => _byPath.ContainsKey(PathUtil.Key(f.Path))).Select(f => f.Path).ToList();
            var gone = _missing;
            if (changed.Count + gone.Count >= guard.Settings.MinFiles && gone.Count > 0)
            {
                var newFiles = new ConcurrentDictionary<string, (long Size, DateTime LastWriteUtc)>();
                Parallel.ForEach(toProcess.Where(f => !_byPath.ContainsKey(PathUtil.Key(f.Path))),
                    new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = FileScanner.DefaultParallelism },
                    f =>
                    {
                        if (engine._identity.TryGetFileId(f.Path) is { } id)
                            newFiles[id] = (f.Size, f.LastWriteUtc);
                    });
                gone = gone.Where(d => !(d.FileId is not null && newFiles.TryGetValue(d.FileId, out var m)
                                         && m.Size == d.Size && m.LastWriteUtc == d.LastWriteUtc)).ToList();
            }
            guard.CheckPlanned(changed.Count, gone.Count, fullRun: options.Scopes is null, totalDocuments: _byPath.Count,
                [.. changed, .. gone.Select(d => d.Path)]);
        }

        // ---------- Parallele Verarbeitung ----------

        /// <summary>
        /// Arbeits-Threads bereiten Dateien vor (Datei-ID, lesen, hashen, ablegen); der aufrufende Ablauf übernimmt
        /// die Ergebnisse streng in der Reihenfolge von <paramref name="files"/> in den Katalog.
        /// </summary>
        private async Task ProcessAllAsync(List<DiskFile> files)
        {
            if (files.Count == 0)
                return;

            var parallelism = options.FileParallelism > 0 ? options.FileParallelism : BackupOptions.DefaultFileParallelism;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var slots = new SemaphoreSlim(parallelism);
            var window = Channel.CreateBounded<Task<PreparedFile>>(new BoundedChannelOptions(parallelism * 4)
            {
                SingleReader = true,
                SingleWriter = true,
            });
            var started = new List<Task<PreparedFile>>(files.Count);

            var producer = Task.Run(async () =>
            {
                try
                {
                    foreach (var file in files)
                    {
                        await slots.WaitAsync(stop.Token).ConfigureAwait(false);
                        var work = Task.Run(() =>
                        {
                            try { return Prepare(file, stop.Token); }
                            finally { slots.Release(); }
                        });
                        started.Add(work);
                        await window.Writer.WriteAsync(work, stop.Token).ConfigureAwait(false);
                    }
                    window.Writer.Complete();
                }
                catch (Exception ex)
                {
                    window.Writer.Complete(ex);
                }
            });

            try
            {
                Catalog.BeginTransaction();
                var sinceCommit = Stopwatch.StartNew();
                var processed = 0;
                var guard = options.Guard is { IsActive: true } active ? active : null;
                // Verdächtige Stände (Inhalt passt nicht zur Endung) erst am Ende ablegen – bei Alarm gar nicht.
                var deferred = new List<PreparedFile>();
                var knownFormat = 0;
                await foreach (var work in window.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
                {
                    var prepared = await work.ConfigureAwait(false);
                    Report(BackupPhase.Saving, processed, files.Count, prepared.File.Path);
                    processed++;
                    if (prepared.KnownFormat)
                        knownFormat++;
                    if (prepared.Suspicious && guard is not null)
                    {
                        deferred.Add(prepared);
                        guard.CheckContent(deferred.Count, knownFormat, deferred.Count, deferred.Select(d => d.File.Path).ToList());
                        continue;
                    }
                    Apply(prepared);

                    if (processed % CommitEveryFiles == 0 || sinceCommit.Elapsed > CommitInterval)
                    {
                        Catalog.Commit();
                        Catalog.BeginTransaction();
                        sinceCommit.Restart();
                    }
                }
                await producer.ConfigureAwait(false);

                if (deferred.Count > 0)
                {
                    guard!.CheckContent(deferred.Count, knownFormat, deferred.Count, deferred.Select(d => d.File.Path).ToList());
                    foreach (var prepared in deferred)
                        Apply(prepared);
                }
            }
            finally
            {
                // Bei Abbruch oder Fehler: alle Arbeits-Threads beenden lassen, bevor Semaphore und Token freigegeben werden.
                stop.Cancel();
                try { await producer.ConfigureAwait(false); } catch (Exception) { }
                // Der Erzeuger ist beendet, "started" ändert sich nicht mehr.
                try { await Task.WhenAll(started).ConfigureAwait(false); } catch (Exception) { }
                // Nicht verbrauchte Vorbereitungen entfernen (abgelegte sind bereits verschoben).
                foreach (var work in started.Where(t => t.IsCompletedSuccessfully && t.Result.Content is not null))
                    ArchiveStore.Discard(work.Result.Content!.Staged.TempPath);
            }
        }

        /// <summary>Läuft auf einem Arbeits-Thread: darf den Katalog nicht anfassen.</summary>
        private PreparedFile Prepare(DiskFile file, CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var fileId = engine._identity.TryGetFileId(file.Path);
                // Neuer Pfad, aber bekannte Datei-ID mit gleicher Größe und gleichem Datum: nur verschoben, Inhalt nicht lesen.
                var unchangedMove = !_knownPathKeys.Contains(PathUtil.Key(file.Path))
                    && fileId is not null
                    && _fileIdSnapshot.TryGetValue(fileId, out var known)
                    && known.Any(k => k.Size == file.Size && k.LastWriteUtc == file.LastWriteUtc);
                var content = unchangedMove ? null : engine.Ingest(file.Path, token);
                var knownFormat = content is not null && options.Guard is not null && ContentSignature.IsKnownFormat(file.Path);
                var suspicious = knownFormat && ContentSignature.LooksWrong(file.Path, content!.Staged.TempPath);
                return new PreparedFile(file, fileId, content, null, knownFormat, suspicious);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new PreparedFile(file, null, null, ex);
            }
        }

        private void Apply(PreparedFile prepared)
        {
            IngestResult? fallback = null;
            try
            {
                if (prepared.Error is not null)
                    ExceptionDispatchInfo.Throw(prepared.Error);
                ProcessFile(prepared.File, prepared.FileId, prepared.Content, read => fallback = read);
                if (prepared.Suspicious)
                    _result.SuspiciousContent++;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Zwischen Durchsuchen und Sichern gelöscht – wird beim nächsten Lauf erkannt.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _result.Errors.Add(new BackupError(prepared.File.Path, ex.Message));
            }
            finally
            {
                // Nicht benötigte Vorbereitungen (z. B. Inhalt unverändert) entfernen; abgelegte sind bereits verschoben.
                if (prepared.Content is not null)
                    ArchiveStore.Discard(prepared.Content.Staged.TempPath);
                if (fallback is not null)
                    ArchiveStore.Discard(fallback.Staged.TempPath);
            }
        }

        // ---------- Zuordnung (nur Katalog-Thread) ----------

        private void ProcessFile(DiskFile file, string? fileId, IngestResult? prepared, Action<IngestResult> onFallbackRead)
        {
            var key = PathUtil.Key(file.Path);
            // Falls ein Arbeits-Thread den Inhalt nicht gelesen hat, er aber doch gebraucht wird (selten): jetzt lesen.
            IngestResult Content()
            {
                if (prepared is null)
                {
                    prepared = engine.Ingest(file.Path, ct);
                    onFallbackRead(prepared);
                }
                return prepared;
            }

            // 1. Gleicher Pfad: Inhalt geändert (oder nur Datum / Groß-/Kleinschreibung).
            if (_byPath.TryGetValue(key, out var existing))
            {
                var sameMeta = existing.Size == file.Size && existing.LastWriteUtc == file.LastWriteUtc;
                if (!string.Equals(existing.Path, file.Path, StringComparison.Ordinal))
                {
                    ApplyMove(existing, file.Path, fileId, sameMeta ? null : Content());
                    return;
                }
                var stored = Content();
                var changed = stored.Hash != existing.Hash;
                if (changed)
                    AddVersion(existing, file.Path, stored, ChangeKind.Modified);
                existing.Size = stored.Size;
                existing.LastWriteUtc = stored.LastWriteUtc;
                existing.Hash = stored.Hash;
                SetFileId(existing, fileId);
                Catalog.UpdateDocument(existing);
                if (changed)
                    Layout.SetCurrent(existing);
                return;
            }

            // 2. Gleiche Datei-ID: umbenannt oder verschoben (auf demselben Laufwerk), evtl. auch geändert.
            var candidate = fileId is null ? null : FindByFileId(fileId);
            if (candidate is not null && candidate.Size == file.Size && candidate.LastWriteUtc == file.LastWriteUtc)
            {
                ApplyMove(candidate, file.Path, fileId, stored: null);
                return;
            }

            var content = Content();
            // 3. Gleicher Inhalt: z. B. über Laufwerksgrenzen verschoben oder kopiert und Original gelöscht.
            candidate ??= FindByHash(content.Hash, Path.GetFileName(file.Path));
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
                Hash = content.Hash,
                FirstSeenUtc = _now,
            };
            Catalog.InsertDocument(doc);
            _byPath[key] = doc;
            IndexFileId(doc);
            AddVersion(doc, file.Path, content, ChangeKind.Created);
            Layout.SetCurrent(doc);
        }

        private void IndexFileId(DocumentRecord doc)
        {
            if (doc.FileId is null)
                return;
            if (!_byFileId.TryGetValue(doc.FileId, out var list))
                _byFileId[doc.FileId] = list = [];
            list.Add(doc);
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
                // Im alten Archivordner bleibt alles liegen; im neuen erscheinen alle bisherigen Versionen (Hardlinks).
                Layout.ReplicateVersions(doc);
                Layout.SetCurrent(doc);
                _moves.Add((oldPath, newPath));
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

            var changed = false;
            if (stored is not null)
            {
                changed = stored.Hash != doc.Hash;
                if (changed)
                    AddVersion(doc, newPath, stored, ChangeKind.Modified);
                doc.Size = stored.Size;
                doc.LastWriteUtc = stored.LastWriteUtc;
                doc.Hash = stored.Hash;
            }
            Catalog.UpdateDocument(doc);
            if (changed || wasDeleted)
                Layout.SetCurrent(doc);
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
            var archivePath = Layout.PlaceVersion(doc.Id, path, stored.Hash, stored.Size, stored.LastWriteUtc, stored.Staged);
            Catalog.InsertVersion(new VersionRecord
            {
                DocumentId = doc.Id,
                Hash = stored.Hash,
                ArchivePath = archivePath,
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

        /// <summary>
        /// Wurde ein ganzer Ordner verschoben oder umbenannt (alter Ordner existiert nicht mehr, neuer schon),
        /// bekommt der alte Archivordner eine Hinweisdatei mit dem neuen Ort – jeweils nur der oberste verschobene Ordner.
        /// </summary>
        private void WriteFolderMoveMarkers()
        {
            var folderMoves = new List<(string Old, string New)>();
            foreach (var (oldPath, newPath) in _moves)
            {
                var oldDir = Path.GetDirectoryName(oldPath);
                var newDir = Path.GetDirectoryName(newPath);
                (string, string)? topmost = null;
                while (oldDir is not null && newDir is not null)
                {
                    if (!Directory.Exists(oldDir) && Directory.Exists(newDir))
                        topmost = (oldDir, newDir);
                    // Weiter nach oben nur, solange die Ordnernamen übereinstimmen (gleiche Struktur unterhalb).
                    if (!string.Equals(Path.GetFileName(oldDir), Path.GetFileName(newDir), PathUtil.Comparison))
                        break;
                    oldDir = Path.GetDirectoryName(oldDir);
                    newDir = Path.GetDirectoryName(newDir);
                }
                if (topmost is { } move && !folderMoves.Contains(move))
                    folderMoves.Add(move);
            }
            foreach (var (oldDir, newDir) in folderMoves)
            {
                if (folderMoves.Any(other => !string.Equals(other.Old, oldDir, PathUtil.Comparison) && PathUtil.IsUnder(oldDir, other.Old)))
                    continue; // liegt in einem ebenfalls verschobenen Ordner
                try
                {
                    engine.Store.WriteMoveMarker(oldDir, newDir, _now);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    FilemanLog.Warn($"Hinweisdatei für verschobenen Ordner nicht geschrieben: {oldDir}: {ex.Message}");
                }
            }
        }

        private void Report(BackupPhase phase, int processed, int total, string? current) =>
            progress?.Report(new BackupProgress(phase, processed, total, current));
    }
}
