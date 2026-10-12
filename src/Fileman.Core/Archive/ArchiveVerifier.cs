namespace Fileman.Core.Archive;

public sealed record VerifyResult(int Checked, IReadOnlyList<string> DamagedOrMissing);

/// <summary>Prüft alle gesicherten Inhalte gegen ihre Prüfsumme.</summary>
public static class ArchiveVerifier
{
    public static VerifyResult Verify(BackupArchive archive, IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var hashes = archive.Catalog.GetAllBlobHashes();
        var bad = new List<string>();
        for (var i = 0; i < hashes.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!archive.Blobs.Verify(hashes[i]))
                bad.Add(hashes[i]);
            progress?.Report((i + 1, hashes.Count));
        }
        return new VerifyResult(hashes.Count, bad);
    }
}
