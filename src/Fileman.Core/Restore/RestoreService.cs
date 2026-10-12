using System.Globalization;
using Fileman.Core.Archive;
using Fileman.Core.Catalog;

namespace Fileman.Core.Restore;

/// <summary>Stellt gesicherte Versionen wieder her. Vorhandene Dateien werden dabei nie überschrieben.</summary>
public sealed class RestoreService(BackupArchive archive)
{
    /// <summary>
    /// Stellt eine Version wieder her.
    /// </summary>
    /// <param name="targetDirectory">Zielordner; Standard ist der Ordner, in dem die Datei zum Sicherungszeitpunkt lag.</param>
    /// <returns>Der Pfad der wiederhergestellten Datei.</returns>
    public string Restore(long versionId, string? targetDirectory = null, CancellationToken cancellationToken = default)
    {
        var version = GetVersion(versionId);
        var directory = targetDirectory is null ? Path.GetDirectoryName(version.Path)! : PathUtil.Normalize(targetDirectory);
        Directory.CreateDirectory(directory);
        var target = FreeTargetPath(directory, Path.GetFileName(version.Path), version.LastWriteUtc);
        archive.Store.ExtractTo(StoredPath(version), version.Hash, target, cancellationToken);
        File.SetLastWriteTimeUtc(target, version.LastWriteUtc);
        return target;
    }

    /// <summary>Entpackt eine Version schreibgeschützt in einen Ansichtsordner, z. B. um sie mit dem Standardprogramm zu öffnen.</summary>
    public string ExtractForViewing(long versionId, string viewDirectory, CancellationToken cancellationToken = default)
    {
        var version = GetVersion(versionId);
        var directory = Path.Combine(viewDirectory, versionId.ToString(CultureInfo.InvariantCulture));
        var target = Path.Combine(directory, Path.GetFileName(version.Path));
        if (File.Exists(target))
            return target;
        Directory.CreateDirectory(directory);
        archive.Store.ExtractTo(StoredPath(version), version.Hash, target, cancellationToken);
        File.SetLastWriteTimeUtc(target, version.LastWriteUtc);
        File.SetAttributes(target, FileAttributes.ReadOnly);
        return target;
    }

    /// <summary>Die Versionsdatei; falls sie von Hand entfernt wurde, eine andere Datei mit gleichem Inhalt.</summary>
    private string StoredPath(VersionRecord version) =>
        archive.Store.Exists(version.ArchivePath) ? version.ArchivePath : archive.Catalog.GetBlobPath(version.Hash) ?? version.ArchivePath;

    private VersionRecord GetVersion(long versionId) =>
        archive.Catalog.GetVersion(versionId) ?? throw new FilemanException($"Version {versionId} ist im Archiv nicht vorhanden.");

    /// <summary>
    /// Liefert einen freien Dateinamen: zuerst den Originalnamen, sonst "Name (Stand 2026-10-12 14-32).ext",
    /// notfalls mit fortlaufender Nummer.
    /// </summary>
    public static string FreeTargetPath(string directory, string fileName, DateTime versionUtc)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
            return candidate;

        var stamp = versionUtc.ToLocalTime().ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture);
        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var n = 1; ; n++)
        {
            var suffix = n == 1 ? $" (Stand {stamp})" : $" (Stand {stamp}) {n}";
            candidate = Path.Combine(directory, name + suffix + extension);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }
}
