using System.Diagnostics;
using System.Threading.Channels;
using Fileman.Core.Archive;
using Fileman.Core.Catalog;
using Fileman.Core.Scanning;
using Fileman.Core.Storage;

namespace Fileman.Core.Legacy;

public sealed record LegacyProgress(string Folder, int FolderIndex, int FolderCount, int Done, int Total);

public sealed class LegacyImportResult
{
    /// <summary>Neu eingelesene Altbestände.</summary>
    public List<string> Imported { get; } = [];
    /// <summary>Bereits früher vollständig eingelesen (Altbestände ändern sich nicht).</summary>
    public List<string> AlreadyImported { get; } = [];
    /// <summary>Nicht erreichbar (z. B. externe Platte nicht angesteckt).</summary>
    public List<string> Unreachable { get; } = [];
    public List<Backup.BackupError> Errors { get; } = [];
    public int Files { get; set; }
    /// <summary>Dateien, deren Inhalt gelesen werden musste (unbekannter Stand).</summary>
    public int FilesRead { get; set; }
    /// <summary>Dateien, die als bekannter Stand nur verlinkt wurden (Kopie aus einem anderen Datumsordner).</summary>
    public int FilesLinked => Files - FilesRead;
    public long BytesStored { get; set; }
}

/// <summary>
/// Liest Altbestände (alte Sicherungen) unverändert ins Archiv ein: Dateien\_Altbestand\&lt;Laufwerk&gt;\…\&lt;Datumsordner&gt;\….
/// Altbestände ändern sich nicht mehr – jeder wird genau einmal eingelesen. Ein abgebrochenes Einlesen wird beim
/// nächsten Mal fortgesetzt. Stände (relativer Pfad, Größe, Änderungsdatum), die schon aus einem anderen
/// Datumsordner bekannt sind, werden nicht erneut gelesen, sondern verlinkt.
/// </summary>
public sealed class LegacyImporter(BackupArchive archive, TimeProvider? time = null)
{
    public const string AreaName = "_Altbestand";
    private const int CommitEvery = 200;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Ablageort einer Altbestand-Datei (relativ zum Ordner "Dateien").</summary>
    public static string ArchivePathFor(string legacyFolder, string relativePath) =>
        Path.Combine(AreaName, ArchiveStore.MirrorPath(legacyFolder), relativePath);

    public async Task<LegacyImportResult> ImportAsync(IReadOnlyList<string> folders, IProgress<LegacyProgress>? progress = null,
        CancellationToken cancellationToken = default, int parallelism = 0)
    {
        var result = new LegacyImportResult();
        var catalog = archive.Catalog;
        var known = new Dictionary<(string Key, long Size, long Time), string>();
        foreach (var file in catalog.GetLegacyFiles())
            known.TryAdd(StateKey(file.RelativePath, file.Size, file.LastWriteUtc), file.Hash);

        for (var index = 0; index < folders.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = PathUtil.Normalize(folders[index]);
            var source = catalog.GetLegacySource(folder);
            if (source?.ImportedUtc is not null)
            {
                result.AlreadyImported.Add(folder);
                continue;
            }
            if (!Directory.Exists(folder))
            {
                result.Unreachable.Add(folder);
                continue;
            }

            progress?.Report(new LegacyProgress(folder, index, folders.Count, 0, 0));
            var scan = new FileScanner(new ExclusionFilter(Configuration.FilemanSettings.DefaultExcludePatterns), parallelism: parallelism)
                .Scan(folder, cancellationToken: cancellationToken);
            foreach (var failed in scan.FailedDirectories)
                result.Errors.Add(new Backup.BackupError(failed.Directory, failed.Message));

            var snapshotUtc = LegacyAnalyzer.DateFromName(Path.GetFileName(folder))
                              ?? (scan.Files.Count > 0 ? scan.Files.Max(f => f.LastWriteUtc) : Directory.GetLastWriteTimeUtc(folder));
            var sourceId = source?.Id ?? catalog.InsertLegacySource(folder, Path.GetFileName(folder), snapshotUtc);
            var done = catalog.GetLegacyFiles(sourceId).Select(f => PathUtil.Key(f.RelativePath)).ToHashSet();

            var errorsBefore = result.Errors.Count;
            await ImportFolderAsync(folder, sourceId, scan.Files, done, known, result,
                (n, total) => progress?.Report(new LegacyProgress(folder, index, folders.Count, n, total)),
                parallelism, cancellationToken).ConfigureAwait(false);

            if (result.Errors.Count == errorsBefore)
            {
                catalog.MarkLegacySourceImported(sourceId, snapshotUtc, _time.GetUtcNow().UtcDateTime, scan.Files.Count, scan.Files.Sum(f => f.Size));
                result.Imported.Add(folder);
            }
        }
        return result;
    }

    private async Task ImportFolderAsync(string folder, long sourceId, IReadOnlyList<DiskFile> files, HashSet<string> done,
        Dictionary<(string, long, long), string> known, LegacyImportResult result, Action<int, int> report, int parallelism,
        CancellationToken ct)
    {
        var catalog = archive.Catalog;
        var layout = new ArchiveLayout(archive.Store, catalog, _time.GetUtcNow().UtcDateTime);
        var pending = files
            .Select(f => (File: f, Relative: Path.GetRelativePath(folder, f.Path)))
            .Where(f => !done.Contains(PathUtil.Key(f.Relative)))
            .ToList();
        var total = pending.Count;

        // Bekannte Stände sofort verlinken, unbekannte parallel lesen.
        var toRead = new List<(DiskFile File, string Relative)>();
        catalog.BeginTransaction();
        var processed = 0;
        var sinceCommit = Stopwatch.StartNew();
        try
        {
            foreach (var item in pending)
            {
                ct.ThrowIfCancellationRequested();
                if (known.TryGetValue(StateKey(item.Relative, item.File.Size, item.File.LastWriteUtc), out var hash))
                {
                    Store(item.File, item.Relative, hash, staged: null);
                    Tick();
                }
                else
                {
                    toRead.Add(item);
                }
            }

            var channel = Channel.CreateBounded<(DiskFile File, string Relative, StagedContent? Staged, Exception? Error)>(
                new BoundedChannelOptions(Math.Max(4, parallelism * 4)) { SingleReader = true });
            var producer = Task.Run(async () =>
            {
                try
                {
                    await Parallel.ForEachAsync(toRead, new ParallelOptions
                    {
                        CancellationToken = ct,
                        MaxDegreeOfParallelism = parallelism > 0 ? parallelism : Backup.BackupOptions.DefaultFileParallelism,
                    }, async (item, token) =>
                    {
                        StagedContent? staged = null;
                        Exception? error = null;
                        try { staged = archive.Store.Stage(item.File.Path, token); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = ex; }
                        await channel.Writer.WriteAsync((item.File, item.Relative, staged, error), token).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                    channel.Writer.Complete();
                }
                catch (Exception ex)
                {
                    channel.Writer.Complete(ex);
                }
            }, ct);

            await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (item.Error is not null || item.Staged is null)
                {
                    result.Errors.Add(new Backup.BackupError(item.File.Path, item.Error?.Message ?? "unbekannter Fehler"));
                    continue;
                }
                try
                {
                    Store(item.File, item.Relative, item.Staged.Hash, item.Staged);
                    known.TryAdd(StateKey(item.Relative, item.File.Size, item.File.LastWriteUtc), item.Staged.Hash);
                    result.FilesRead++;
                }
                finally
                {
                    ArchiveStore.Discard(item.Staged.TempPath);
                }
                Tick();
            }
            await producer.ConfigureAwait(false);
        }
        finally
        {
            catalog.Commit();
            result.BytesStored += layout.BytesStored;
        }

        void Store(DiskFile file, string relative, string hash, StagedContent? staged)
        {
            var archivePath = ArchivePathFor(folder, relative);
            layout.PlaceAsIs(archivePath, hash, file.Size, file.LastWriteUtc, staged);
            catalog.InsertLegacyFile(new LegacyFileRecord(sourceId, relative, file.Size, file.LastWriteUtc, hash, archivePath));
            result.Files++;
        }

        void Tick()
        {
            processed++;
            if (processed % 50 == 0 || processed == total)
                report(processed, total);
            if (processed % CommitEvery == 0 || sinceCommit.Elapsed > TimeSpan.FromSeconds(3))
            {
                catalog.Commit();
                catalog.BeginTransaction();
                sinceCommit.Restart();
            }
        }
    }

    /// <summary>Stand = relativer Pfad + Größe + Änderungsdatum (auf 2 s gerundet wegen FAT/exFAT).</summary>
    private static (string, long, long) StateKey(string relative, long size, DateTime utc) =>
        (PathUtil.Key(relative), size, utc.Ticks / TimeSpan.TicksPerSecond / 2);
}
