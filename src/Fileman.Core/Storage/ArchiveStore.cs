using System.Globalization;
using System.Security.Cryptography;

namespace Fileman.Core.Storage;

/// <summary>Eine Datei, die bereits ins Archiv kopiert (und gehasht), aber noch nicht an ihren Platz gelegt wurde.</summary>
public sealed record StagedContent(string TempPath, string Hash, long Size);

/// <summary>
/// Die lesbare Ablage auf der Sicherungsplatte: unter "Dateien" liegt die Ordnerstruktur der Quellen
/// (Laufwerksbuchstabe als erster Ordner), mit aktuellem Stand unter exaktem Namen und Versionen als
/// "Name (JJJJ-MM-TT HH-MM).ext". Gleiche Inhalte werden per Hardlink nur einmal gespeichert.
/// Alle Dateien sind schreibgeschützt, damit niemand versehentlich einen gesicherten Stand verändert.
/// </summary>
public sealed class ArchiveStore
{
    public const string FilesFolderName = "Dateien";
    private const int BufferSize = 1 << 16;

    public ArchiveStore(string archiveDirectory)
    {
        FilesDirectory = Path.Combine(archiveDirectory, FilesFolderName);
        TempDirectory = Path.Combine(archiveDirectory, "tmp");
        Directory.CreateDirectory(FilesDirectory);
        Directory.CreateDirectory(TempDirectory);
    }

    public string FilesDirectory { get; }

    private string TempDirectory { get; }

    /// <summary>Wurde beim letzten Ablegen kopiert statt verlinkt (Dateisystem ohne Hardlinks)?</summary>
    public bool HardLinksUnavailable { get; private set; }

    /// <summary>
    /// Ablagepfad (relativ zu "Dateien") für einen Quellpfad:
    /// "D:\Mandanten\a.xlsx" → "D\Mandanten\a.xlsx", "\\server\freigabe\a.xlsx" → "UNC\server\freigabe\a.xlsx".
    /// </summary>
    public static string MirrorPath(string sourcePath)
    {
        var full = Path.GetFullPath(sourcePath);
        var root = Path.GetPathRoot(full) ?? "";
        var rest = full[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string prefix;
        if (root.StartsWith(@"\\", StringComparison.Ordinal))
            prefix = Path.Combine("UNC", root.Trim('\\').Replace('\\', Path.DirectorySeparatorChar));
        else
            prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':');
        return prefix.Length == 0 ? rest : Path.Combine(prefix, rest);
    }

    /// <summary>"Bilanz.xlsx" + Zeitpunkt → "Bilanz (2026-10-12 14-32).xlsx" (bei Bedarf mit Sekunden oder Zähler).</summary>
    public static IEnumerable<string> VersionFileNames(string fileName, DateTime lastWriteUtc)
    {
        var local = lastWriteUtc.ToLocalTime();
        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var minute = local.ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture);
        var second = local.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
        yield return $"{name} ({minute}){extension}";
        yield return $"{name} ({second}){extension}";
        for (var n = 2; ; n++)
            yield return $"{name} ({second}) {n}{extension}";
    }

    public string FullPath(string relativePath) => Path.Combine(FilesDirectory, relativePath);

    /// <summary>Kopiert die Quelldatei in einen temporären Bereich des Archivs und hasht dabei (ein Lesevorgang). Threadsicher.</summary>
    public StagedContent Stage(string sourcePath, CancellationToken cancellationToken = default)
    {
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan);
        return Stage(source, cancellationToken);
    }

    /// <summary>Wie <see cref="Stage(string, CancellationToken)"/>, aber aus einem Datenstrom (z. B. Zwischenspeicher).</summary>
    public StagedContent Stage(Stream source, CancellationToken cancellationToken = default)
    {
        var temp = Path.Combine(TempDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            long size = 0;
            string hash;
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
                    size += read;
                }
                hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                target.Flush(flushToDisk: true);
            }
            return new StagedContent(temp, hash, size);
        }
        catch
        {
            Discard(temp);
            throw;
        }
    }

    /// <summary>Verschiebt einen vorbereiteten Inhalt an seinen endgültigen Platz.</summary>
    public void PlaceStaged(StagedContent staged, string relativeTarget, DateTime lastWriteUtc)
    {
        var target = PrepareTarget(relativeTarget);
        File.Move(staged.TempPath, target);
        Finish(target, lastWriteUtc);
    }

    /// <summary>Legt einen bereits gespeicherten Inhalt unter einem weiteren Namen ab (Hardlink, sonst Kopie).</summary>
    public void PlaceLink(string existingRelative, string relativeTarget)
    {
        var target = PrepareTarget(relativeTarget);
        if (!HardLink.CreateOrCopy(FullPath(existingRelative), target))
        {
            HardLinksUnavailable = true;
            File.SetAttributes(target, FileAttributes.ReadOnly);
        }
    }

    /// <summary>Entfernt einen Namen (z. B. den bisherigen „aktuellen Stand“). Andere Namen desselben Inhalts bleiben.</summary>
    public void RemoveName(string relativePath, string? stillExistingRelative)
    {
        var path = FullPath(relativePath);
        if (!File.Exists(path))
            return;
        // Der Schreibschutz gilt für alle Namen desselben Inhalts; nach dem Löschen wieder setzen.
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
        if (stillExistingRelative is not null && File.Exists(FullPath(stillExistingRelative)))
            File.SetAttributes(FullPath(stillExistingRelative), FileAttributes.ReadOnly);
    }

    public bool Exists(string relativePath) => File.Exists(FullPath(relativePath)) || Directory.Exists(FullPath(relativePath));

    public Stream OpenRead(string relativePath)
    {
        var path = FullPath(relativePath);
        if (!File.Exists(path))
            throw new FilemanException($"Die gesicherte Datei \"{relativePath}\" fehlt im Archiv.");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
    }

    /// <summary>Kopiert einen gesicherten Stand heraus und prüft dabei die Prüfsumme.</summary>
    public void ExtractTo(string relativePath, string expectedHash, string targetPath, CancellationToken cancellationToken = default)
    {
        var temp = targetPath + ".fileman-tmp";
        try
        {
            string actual;
            using (var source = OpenRead(relativePath))
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
            if (actual != expectedHash)
                throw new FilemanException($"Die gesicherte Datei \"{relativePath}\" ist beschädigt (Prüfsumme stimmt nicht).");
            File.Move(temp, targetPath);
            File.SetAttributes(targetPath, FileAttributes.Normal);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.SetAttributes(temp, FileAttributes.Normal);
                File.Delete(temp);
            }
        }
    }

    public bool Verify(string relativePath, string hash)
    {
        try
        {
            using var source = OpenRead(relativePath);
            return Convert.ToHexString(SHA256.HashData(source)).Equals(hash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FilemanException)
        {
            return false;
        }
    }

    /// <summary>Hinterlegt im bisherigen Ordner eine Textdatei mit dem neuen Ort (wird bei Bedarf ergänzt).</summary>
    public void WriteMoveMarker(string oldSourceFolder, string newSourceFolder, DateTime utc)
    {
        var folder = FullPath(MirrorPath(oldSourceFolder));
        Directory.CreateDirectory(folder);
        var marker = Path.Combine(folder, "_Fileman – Ordner verschoben.txt");
        var text = $"""
            Am {utc.ToLocalTime():dd.MM.yyyy} um {utc.ToLocalTime():HH:mm} Uhr wurde dieser Ordner verschoben.

              bisher:  {oldSourceFolder}
              neu:     {newSourceFolder}

            Die Versionen bis zur Verschiebung liegen weiterhin hier.
            Alle Versionen (auch die späteren) liegen im Archiv unter:
              {Path.Combine(FilesFolderName, MirrorPath(newSourceFolder))}


            """;
        if (File.Exists(marker))
            File.SetAttributes(marker, FileAttributes.Normal);
        File.AppendAllText(marker, text);
        File.SetAttributes(marker, FileAttributes.ReadOnly);
    }

    public static void Discard(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Entfernt Reste abgebrochener Schreibvorgänge.</summary>
    public void CleanupTemp()
    {
        foreach (var file in Directory.EnumerateFiles(TempDirectory))
            Discard(file);
    }

    private string PrepareTarget(string relativeTarget)
    {
        var target = FullPath(relativeTarget);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        return target;
    }

    private static void Finish(string target, DateTime lastWriteUtc)
    {
        File.SetLastWriteTimeUtc(target, lastWriteUtc);
        File.SetAttributes(target, FileAttributes.ReadOnly);
    }
}
