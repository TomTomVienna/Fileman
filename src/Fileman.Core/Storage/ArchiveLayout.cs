using Fileman.Core.Catalog;

namespace Fileman.Core.Storage;

/// <summary>
/// Legt Versionen und aktuelle Stände im lesbaren Archivordner ab und führt darüber Buch (Tabelle ArchiveFiles).
/// Nur vom Katalog-Thread zu verwenden.
/// </summary>
internal sealed class ArchiveLayout(ArchiveStore store, CatalogDatabase catalog, DateTime nowUtc)
{
    private const string CurrentFallbackSuffix = " (aktuell)";

    /// <summary>Bytes, die in diesem Lauf tatsächlich neu auf die Platte geschrieben wurden.</summary>
    public long BytesStored { get; private set; }

    /// <summary>
    /// Legt eine Version als "Name (Zeitpunkt).ext" im Archivordner der Quelldatei ab. Ist der Inhalt schon
    /// gespeichert, entsteht nur ein Hardlink; <paramref name="staged"/> wird dann verworfen.
    /// </summary>
    /// <returns>Relativer Pfad der Versionsdatei.</returns>
    public string PlaceVersion(long documentId, string sourcePath, string hash, long size, DateTime lastWriteUtc, StagedContent? staged)
    {
        var folder = Path.GetDirectoryName(ArchiveStore.MirrorPath(sourcePath)) ?? "";
        foreach (var name in ArchiveStore.VersionFileNames(Path.GetFileName(sourcePath), lastWriteUtc))
        {
            var relative = Path.Combine(folder, name);
            var existing = catalog.GetArchiveFile(relative);
            if (existing is not null)
            {
                if (existing.Hash == hash && !existing.IsCurrent)
                    return relative; // genau diese Version liegt hier schon (z. B. nach Hin- und Zurückschieben)
                continue;
            }
            if (store.Exists(relative))
                continue; // fremde Datei – nie überschreiben

            Place(hash, size, lastWriteUtc, staged, relative);
            catalog.InsertArchiveFile(new ArchiveFileRecord(relative, hash, documentId, IsCurrent: false));
            return relative;
        }
        throw new InvalidOperationException("Unerreichbar");
    }

    /// <summary>Legt den aktuellen Stand eines Dokuments unter seinem exakten Namen ab (ersetzt den bisherigen).</summary>
    public void SetCurrent(DocumentRecord doc)
    {
        var mirror = ArchiveStore.MirrorPath(doc.Path);
        var folder = Path.GetDirectoryName(mirror) ?? "";
        var fallback = Path.Combine(folder, Path.GetFileNameWithoutExtension(mirror) + CurrentFallbackSuffix + Path.GetExtension(mirror));
        foreach (var relative in new[] { mirror, fallback })
        {
            var existing = catalog.GetArchiveFile(relative);
            if (existing is { IsCurrent: true })
            {
                if (existing.Hash == doc.Hash && existing.DocumentId == doc.Id)
                    return;
                store.RemoveName(existing.RelativePath, catalog.GetBlobPath(existing.Hash));
                catalog.DeleteArchiveFile(existing.RelativePath);
            }
            else if (existing is not null || store.Exists(relative))
            {
                continue; // Name ist durch eine Versionsdatei oder fremde Datei belegt
            }

            var stored = catalog.GetBlobPath(doc.Hash) ?? throw new InvalidOperationException($"Inhalt {doc.Hash} ist nicht gespeichert.");
            store.PlaceLink(stored, relative);
            catalog.InsertArchiveFile(new ArchiveFileRecord(relative, doc.Hash, doc.Id, IsCurrent: true));
            return;
        }
        FilemanLog.Warn($"Aktueller Stand von {doc.Path} konnte nicht unter seinem Namen abgelegt werden (Name belegt).");
    }

    /// <summary>Nach einer Verschiebung: alle bisherigen Versionen auch im neuen Ordner (unter neuem Namen) ablegen.</summary>
    public void ReplicateVersions(DocumentRecord doc)
    {
        foreach (var version in catalog.GetVersions(doc.Id))
            PlaceVersion(doc.Id, doc.Path, version.Hash, version.Size, version.LastWriteUtc, staged: null);
    }

    private void Place(string hash, long size, DateTime lastWriteUtc, StagedContent? staged, string relative)
    {
        var stored = catalog.GetBlobPath(hash);
        if (stored is not null)
        {
            try
            {
                store.PlaceLink(stored, relative);
                if (staged is not null)
                    ArchiveStore.Discard(staged.TempPath);
                return;
            }
            catch (FileNotFoundException) when (staged is not null)
            {
                // Die gespeicherte Datei wurde von Hand entfernt – mit dem frischen Inhalt neu ablegen.
                store.PlaceStaged(staged, relative, lastWriteUtc);
                catalog.UpdateBlobPath(hash, relative);
                BytesStored += size;
                return;
            }
        }
        if (staged is null)
            throw new InvalidOperationException($"Inhalt {hash} ist weder gespeichert noch vorbereitet.");
        store.PlaceStaged(staged, relative, lastWriteUtc);
        catalog.InsertBlob(hash, size, relative, nowUtc);
        BytesStored += size;
    }
}
