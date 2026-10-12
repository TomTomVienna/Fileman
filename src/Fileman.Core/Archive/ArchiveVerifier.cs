namespace Fileman.Core.Archive;

public sealed record VerifyResult(int Checked, IReadOnlyList<string> DamagedOrMissing);

/// <summary>Prüft alle gesicherten Inhalte gegen ihre Prüfsumme (parallel).</summary>
public static class ArchiveVerifier
{
    public static VerifyResult Verify(BackupArchive archive, IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default, int parallelism = 0)
    {
        var hashes = archive.Catalog.GetAllBlobHashes();
        var bad = new System.Collections.Concurrent.ConcurrentBag<string>();
        var done = 0;
        Parallel.ForEach(hashes, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = parallelism > 0 ? parallelism : Backup.BackupOptions.DefaultFileParallelism,
        }, hash =>
        {
            if (!archive.Blobs.Verify(hash))
                bad.Add(hash);
            progress?.Report((Interlocked.Increment(ref done), hashes.Count));
        });
        return new VerifyResult(hashes.Count, [.. bad.Order(StringComparer.Ordinal)]);
    }
}
