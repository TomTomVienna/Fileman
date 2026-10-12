namespace Fileman.Core.Archive;

public sealed record VerifyResult(int Checked, IReadOnlyList<string> DamagedOrMissing);

/// <summary>Prüft alle gesicherten Inhalte gegen ihre Prüfsumme (parallel).</summary>
public static class ArchiveVerifier
{
    /// <returns>Beschädigte oder fehlende Dateien (relativ zum Ordner "Dateien").</returns>
    public static VerifyResult Verify(BackupArchive archive, IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default, int parallelism = 0)
    {
        var blobs = archive.Catalog.GetAllBlobs();
        var bad = new System.Collections.Concurrent.ConcurrentBag<string>();
        var done = 0;
        Parallel.ForEach(blobs, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = parallelism > 0 ? parallelism : Backup.BackupOptions.DefaultFileParallelism,
        }, blob =>
        {
            if (!archive.Store.Verify(blob.StoredPath, blob.Hash))
                bad.Add(blob.StoredPath);
            progress?.Report((Interlocked.Increment(ref done), blobs.Count));
        });
        return new VerifyResult(blobs.Count, [.. bad.Order(StringComparer.Ordinal)]);
    }
}
