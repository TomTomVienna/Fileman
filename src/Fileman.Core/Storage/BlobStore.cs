using System.IO.Compression;
using System.Security.Cryptography;

namespace Fileman.Core.Storage;

/// <summary>Ergebnis des Einlagerns einer Datei.</summary>
public sealed record StoredContent(string Hash, long Size, long StoredSize, bool IsNew);

/// <summary>
/// Inhaltsadressierter Speicher: jeder Dateiinhalt liegt genau einmal als blobs\ab\cd\&lt;sha256&gt;.gz.
/// Vorhandene Blobs werden nie verändert oder gelöscht.
/// </summary>
public sealed class BlobStore
{
    private const int BufferSize = 1 << 16;

    public BlobStore(string directory)
    {
        Directory = directory;
        TempDirectory = Path.Combine(directory, "tmp");
        System.IO.Directory.CreateDirectory(TempDirectory);
    }

    public string Directory { get; }

    private string TempDirectory { get; }

    public string GetPath(string hash) => Path.Combine(Directory, hash[..2], hash[2..4], hash + ".gz");

    public bool Exists(string hash) => File.Exists(GetPath(hash));

    /// <summary>
    /// Liest die Quelldatei genau einmal: gleichzeitig hashen und komprimiert in eine temporäre Datei schreiben.
    /// Erst danach wird sie unter ihrem Hash abgelegt. So entsteht nie ein halber Blob unter einem gültigen Namen.
    /// </summary>
    public StoredContent Store(string sourcePath, CancellationToken cancellationToken = default)
    {
        var temp = Path.Combine(TempDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            string hash;
            long size = 0;
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan))
            using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize))
            {
                using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                using (var gzip = new GZipStream(target, CompressionLevel.Fastest, leaveOpen: true))
                {
                    var buffer = new byte[BufferSize];
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        sha.AppendData(buffer, 0, read);
                        gzip.Write(buffer, 0, read);
                        size += read;
                    }
                    hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                }
                target.Flush(flushToDisk: true);
            }

            var storedSize = new FileInfo(temp).Length;
            var finalPath = GetPath(hash);
            if (File.Exists(finalPath))
                return new StoredContent(hash, size, storedSize, IsNew: false);

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            try
            {
                File.Move(temp, finalPath);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                return new StoredContent(hash, size, storedSize, IsNew: false);
            }
            File.SetAttributes(finalPath, FileAttributes.ReadOnly);
            return new StoredContent(hash, size, storedSize, IsNew: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <summary>Öffnet den entpackten Inhalt zum Lesen.</summary>
    public Stream OpenRead(string hash)
    {
        var path = GetPath(hash);
        if (!File.Exists(path))
            throw new FilemanException($"Der gesicherte Inhalt {hash[..12]}… fehlt im Archiv.");
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        return new GZipStream(file, CompressionMode.Decompress);
    }

    /// <summary>Entpackt den Inhalt in eine Datei und prüft dabei den Hash.</summary>
    public void ExtractTo(string hash, string targetPath, CancellationToken cancellationToken = default)
    {
        var temp = targetPath + ".fileman-tmp";
        try
        {
            string actual;
            using (var source = OpenRead(hash))
            using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sha.AppendData(buffer, 0, read);
                    target.Write(buffer, 0, read);
                }
                actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            }
            if (actual != hash)
                throw new FilemanException($"Der gesicherte Inhalt {hash[..12]}… ist beschädigt (Prüfsumme stimmt nicht).");
            File.Move(temp, targetPath);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <summary>Prüft, ob ein Blob vorhanden und unbeschädigt ist.</summary>
    public bool Verify(string hash)
    {
        try
        {
            using var source = OpenRead(hash);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(source)).Equals(hash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FilemanException)
        {
            return false;
        }
    }

    /// <summary>Entfernt Reste abgebrochener Schreibvorgänge.</summary>
    public void CleanupTemp()
    {
        foreach (var file in System.IO.Directory.EnumerateFiles(TempDirectory))
        {
            try { File.Delete(file); }
            catch (IOException) { }
        }
    }
}
