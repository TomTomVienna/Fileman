using Fileman.Core.Archive;
using Fileman.Core.Catalog;
using Fileman.Core.Storage;

namespace Fileman.Core.Legacy;

public sealed class LegacyConsolidationResult
{
    /// <summary>Neu angelegte Versionen (bisher unbekannte Stände).</summary>
    public int VersionsAdded { get; set; }
    /// <summary>Stände, die schon als Version vorhanden waren (Duplikate) – übersprungen.</summary>
    public int DuplicatesSkipped { get; set; }
    /// <summary>Dateien, die es im Master nicht (mehr) gibt – als gelöscht mit ihrer Geschichte angelegt.</summary>
    public int DocumentsCreated { get; set; }
    /// <summary>Dateien ohne bestätigte Zuordnung – bleiben nur im Bereich _Altbestand.</summary>
    public int Unmapped { get; set; }
}

/// <summary>
/// Übernimmt eingelesene Altbestände gemäß bestätigter Zuordnung in die Versionsgeschichte der Master-Dokumente.
/// Es werden keine Inhalte kopiert, nur Hardlinks und Katalogeinträge angelegt. Bereits vorhandene Stände werden
/// übersprungen – die Konsolidierung kann daher gefahrlos wiederholt werden.
/// </summary>
public sealed class LegacyConsolidator(BackupArchive archive, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public LegacyConsolidationResult Consolidate(IReadOnlyList<LegacyMapping> accepted, IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new LegacyConsolidationResult();
        var catalog = archive.Catalog;
        var now = _time.GetUtcNow().UtcDateTime;
        var layout = new ArchiveLayout(archive.Store, catalog, now);
        var bySource = accepted.GroupBy(m => m.SourceId).ToDictionary(g => g.Key, g => g.Select(m => (m.LegacyPrefix, m.TargetPrefix)).ToList());

        // Älteste Sicherung zuerst, damit "nur im Altbestand" vorhandene Dokumente am Ende den jüngsten Stand tragen.
        var sources = catalog.GetLegacySources().Where(s => bySource.ContainsKey(s.Id)).OrderBy(s => s.SnapshotUtc).ToList();
        var work = sources.SelectMany(s => catalog.GetLegacyFiles(s.Id).Select(f => (Source: s, File: f))).ToList();
        var created = new Dictionary<long, DocumentRecord>();

        catalog.BeginTransaction();
        try
        {
            for (var i = 0; i < work.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (source, file) = work[i];
                if (LegacyMapper.Resolve(bySource[source.Id], file.RelativePath) is not { } mapping)
                {
                    result.Unmapped++;
                    continue;
                }
                var target = LegacyMapper.Target(mapping, file.RelativePath);

                var doc = catalog.FindDocumentByAnyPath(target);
                if (doc is null)
                {
                    // Gibt es im Master nicht (mehr): als gelöschtes Dokument mit Geschichte anlegen.
                    doc = new DocumentRecord
                    {
                        Path = target,
                        FileName = Path.GetFileName(target),
                        Size = file.Size,
                        LastWriteUtc = file.LastWriteUtc,
                        Hash = file.Hash,
                        FirstSeenUtc = source.SnapshotUtc,
                        IsDeleted = true,
                        DeletedUtc = now,
                    };
                    catalog.InsertDocument(doc);
                    created[doc.Id] = doc;
                    result.DocumentsCreated++;
                }

                if (catalog.HasVersionWithHash(doc.Id, file.Hash))
                {
                    result.DuplicatesSkipped++;
                }
                else
                {
                    var archivePath = layout.PlaceVersion(doc.Id, target, file.Hash, file.Size, file.LastWriteUtc, staged: null);
                    catalog.InsertVersion(new VersionRecord
                    {
                        DocumentId = doc.Id,
                        Hash = file.Hash,
                        Size = file.Size,
                        LastWriteUtc = file.LastWriteUtc,
                        Path = target,
                        CapturedUtc = source.SnapshotUtc,
                        Trigger = BackupTrigger.Legacy,
                        ArchivePath = archivePath,
                    }, runId: null);
                    result.VersionsAdded++;
                }

                if (created.TryGetValue(doc.Id, out var legacyOnly) && file.LastWriteUtc >= legacyOnly.LastWriteUtc)
                {
                    legacyOnly.Hash = file.Hash;
                    legacyOnly.Size = file.Size;
                    legacyOnly.LastWriteUtc = file.LastWriteUtc;
                }
                if (i % 200 == 0)
                {
                    progress?.Report((i, work.Count));
                    catalog.Commit();
                    catalog.BeginTransaction();
                }
            }

            // Dokumente, die es nur im Altbestand gibt: jüngster Stand auch unter exaktem Namen ablegen.
            foreach (var doc in created.Values)
            {
                catalog.UpdateDocument(doc);
                layout.SetCurrent(doc);
            }
            foreach (var source in sources)
                catalog.MarkLegacySourceConsolidated(source.Id, now);
            progress?.Report((work.Count, work.Count));
        }
        finally
        {
            catalog.Commit();
        }
        return result;
    }
}
