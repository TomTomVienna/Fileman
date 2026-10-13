using Fileman.Core.Archive;
using Fileman.Core.Catalog;
using Fileman.Core.Storage;

namespace Fileman.Core.Copies;

public sealed record CopyMergeResult(int VersionsAdded, int AlreadyKnown, int OnlyInCopy);

/// <summary>
/// Führt die Geschichte einer Kopie mit dem Original zusammen: Jeder Stand der Kopie, den das Original-Dokument noch
/// nicht kennt, wird dort als ältere Version (Art „Altbestand“) eingetragen. Nur Hardlinks und Katalogeinträge –
/// an den Ordnern des Anwenders ändert sich nichts. Wiederholbar ohne Doppelungen.
/// </summary>
public sealed class CopyMerger(BackupArchive archive, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public CopyMergeResult Merge(CopyCandidate candidate)
    {
        var catalog = archive.Catalog;
        var layout = new ArchiveLayout(archive.Store, catalog, _time.GetUtcNow().UtcDateTime);
        var nested = PathUtil.IsUnder(candidate.CopyFolder, candidate.OriginalFolder) ? null : candidate.OriginalFolder;
        var copies = catalog.GetLiveDocuments()
            .Where(d => PathUtil.IsUnder(d.Path, candidate.CopyFolder) && (nested is null || !PathUtil.IsUnder(d.Path, nested)))
            .ToList();
        int added = 0, known = 0, onlyCopy = 0;
        catalog.BeginTransaction();
        try
        {
            foreach (var copy in copies)
            {
                var target = Path.Combine(candidate.OriginalFolder, Path.GetRelativePath(candidate.CopyFolder, copy.Path));
                var original = catalog.FindDocumentByAnyPath(target);
                if (original is null || original.IsDeleted)
                {
                    onlyCopy++;
                    continue;
                }
                foreach (var version in catalog.GetVersions(copy.Id))
                {
                    if (catalog.HasVersionWithHash(original.Id, version.Hash))
                    {
                        known++;
                        continue;
                    }
                    var archivePath = layout.PlaceVersion(original.Id, target, version.Hash, version.Size, version.LastWriteUtc, staged: null);
                    catalog.InsertVersion(new VersionRecord
                    {
                        DocumentId = original.Id,
                        Hash = version.Hash,
                        Size = version.Size,
                        LastWriteUtc = version.LastWriteUtc,
                        Path = target,
                        // Einreihen nach dem Dateistand, damit die Geschichte zeitlich stimmt.
                        CapturedUtc = version.LastWriteUtc,
                        Trigger = BackupTrigger.Legacy,
                        ArchivePath = archivePath,
                    }, runId: null);
                    added++;
                }
            }
            catalog.Commit();
        }
        catch
        {
            catalog.Rollback();
            throw;
        }
        return new CopyMergeResult(added, known, onlyCopy);
    }
}
