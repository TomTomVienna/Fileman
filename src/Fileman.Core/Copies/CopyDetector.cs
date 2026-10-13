using Fileman.Core.Catalog;

namespace Fileman.Core.Copies;

public enum CopyAssessment
{
    /// <summary>Älterer Stand des Originals (nichts Neueres in der Kopie).</summary>
    OlderCopy,
    /// <summary>Inhaltlich identisch mit dem Original.</summary>
    Identical,
    /// <summary>In der Kopie gibt es neuere Stände – vermutlich wurde darin weitergearbeitet.</summary>
    EditedSince,
}

/// <summary>Ein Ordner, der wie eine Kopie eines anderen Ordners aussieht.</summary>
/// <param name="Identical">Dateien mit gleichem relativem Pfad und gleichem Inhalt.</param>
/// <param name="OlderInCopy">Gleicher Pfad, anderer Inhalt, Stand in der Kopie älter.</param>
/// <param name="NewerInCopy">Gleicher Pfad, anderer Inhalt, Stand in der Kopie neuer.</param>
/// <param name="OnlyInCopy">Dateien, die es nur in der Kopie gibt.</param>
public sealed record CopyCandidate(
    string CopyFolder,
    string OriginalFolder,
    int CopyFiles,
    int OriginalFiles,
    int Identical,
    int OlderInCopy,
    int NewerInCopy,
    int OnlyInCopy,
    DateTime CopyNewestUtc,
    DateTime OriginalNewestUtc)
{
    /// <summary>Anteil der Kopie, der auch im Original vorkommt.</summary>
    public double Overlap => CopyFiles == 0 ? 0 : (double)(Identical + OlderInCopy + NewerInCopy) / CopyFiles;

    public CopyAssessment Assessment =>
        NewerInCopy > 0 ? CopyAssessment.EditedSince
        : OlderInCopy == 0 && OnlyInCopy == 0 ? CopyAssessment.Identical
        : CopyAssessment.OlderCopy;

    /// <summary>Stabiler Schlüssel, z. B. zum Ignorieren.</summary>
    public string Key => PathUtil.Key(CopyFolder) + "|" + PathUtil.Key(OriginalFolder);
}

/// <summary>
/// Findet Teilbäume, die Kopien anderer Teilbäume sind – allein aus dem Katalog (Pfade, Hashes, Änderungsdaten),
/// ohne eine Datei zu lesen. Grundlage: Dateien mit gleichem Inhalt an zwei Orten „stimmen“ für das Ordnerpaar,
/// ab dem sich ihre Pfade unterscheiden (z. B. "Huber GmbH" und "Huber GmbH\Kopie 2022").
/// Ein Inhalt zählt umso weniger, je öfter er vorkommt (Gewicht 1/(Anzahl−1)): Eine Datei, die es genau zweimal gibt,
/// ist ein starkes Indiz; eine Vorlage, die in jedem Mandantenordner liegt, praktisch keines.
/// Gleiche Dateinamen mit anderem Inhalt („Bilanz.xlsx“ bei jedem Mandanten) gelten nur als schwaches, ergänzendes Indiz –
/// außer der Inhalt ist ein früherer Stand des anderen Dokuments (aus der Versionsgeschichte): Das ist das stärkste Indiz
/// für eine alte Sicherungskopie.
/// Welche Seite die Kopie ist, entscheidet das Alter: die Seite mit den älteren Ständen.
/// </summary>
public static class CopyDetector
{
    private const int MaxSameContent = 20;   // sehr häufige Inhalte (z. B. Vorlagen) sagen nichts aus
    private const int MaxPairs = 200;
    private static readonly string[] CopyWords = ["kopie", "copy", "alt", "old", "backup", "sicherung", "archiv"];

    /// <param name="minEvidence">Mindest-Summe der gewichteten identischen Dateien.</param>
    /// <param name="history">Alle Versions-Hashes je Dokument (optional): erkennt Kopien, die frühere Stände enthalten.</param>
    public static IReadOnlyList<CopyCandidate> Detect(IReadOnlyList<DocumentRecord> documents,
        IReadOnlyDictionary<long, HashSet<string>>? history = null, double minEvidence = 2, double minOverlap = 0.5,
        CancellationToken cancellationToken = default)
    {
        var live = documents.Where(d => !d.IsDeleted).ToList();
        history ??= new Dictionary<long, HashSet<string>>();
        var hashCounts = live.GroupBy(d => d.Hash).ToDictionary(g => g.Key, g => g.Count());
        var votes = new Dictionary<(string A, string B), double>();

        // Frühere Stände: Inhalt einer Datei = älterer Stand eines anderen Dokuments.
        var olderStates = new Dictionary<string, List<DocumentRecord>>();
        foreach (var doc in live)
        {
            if (!history.TryGetValue(doc.Id, out var hashes))
                continue;
            foreach (var hash in hashes.Where(h => h != doc.Hash))
            {
                if (!olderStates.TryGetValue(hash, out var list))
                    olderStates[hash] = list = [];
                list.Add(doc);
            }
        }
        foreach (var copy in live.Where(d => d.Size > 0))
        {
            if (!olderStates.TryGetValue(copy.Hash, out var owners) || owners.Count > MaxSameContent)
                continue;
            foreach (var owner in owners.Where(o => o.Id != copy.Id))
            {
                if (FolderPair(copy.Path, owner.Path) is { } pair)
                    votes[pair] = votes.GetValueOrDefault(pair) + 1.0 / owners.Count;
            }
        }
        foreach (var group in live.Where(d => d.Size > 0).GroupBy(d => d.Hash))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var members = group.ToList();
            if (members.Count < 2 || members.Count > MaxSameContent)
                continue;
            var weight = 1.0 / (members.Count - 1);
            for (var i = 0; i < members.Count; i++)
            {
                for (var j = i + 1; j < members.Count; j++)
                {
                    if (FolderPair(members[i].Path, members[j].Path) is { } pair)
                        votes[pair] = votes.GetValueOrDefault(pair) + weight;
                }
            }
        }

        var candidates = new List<CopyCandidate>();
        foreach (var ((a, b), _) in votes.Where(v => v.Value >= minEvidence).OrderByDescending(v => v.Value).Take(MaxPairs))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Evaluate(live, a, b, hashCounts, history) is not ({ } candidate, var evidence))
                continue;
            // Genug eindeutige gemeinsame Inhalte (mind. 20 % der Kopie) und insgesamt überwiegend gleiche Struktur.
            if (evidence >= minEvidence && evidence >= 0.2 * candidate.CopyFiles && candidate.Overlap >= minOverlap)
                candidates.Add(candidate);
        }

        // Unterordner-Paare, die sich schon aus einem übergeordneten Paar ergeben, entfallen.
        var result = new List<CopyCandidate>();
        foreach (var c in candidates.OrderByDescending(c => c.CopyFiles))
        {
            var covered = result.Any(r =>
                PathUtil.IsUnder(c.CopyFolder, r.CopyFolder) && PathUtil.IsUnder(c.OriginalFolder, r.OriginalFolder)
                && string.Equals(Path.GetRelativePath(r.CopyFolder, c.CopyFolder), Path.GetRelativePath(r.OriginalFolder, c.OriginalFolder), PathUtil.Comparison));
            if (!covered && !result.Any(r => r.Key == c.Key))
                result.Add(c);
        }
        return result;
    }

    /// <summary>Die beiden Ordner, ab denen sich die Pfade zweier gleicher Dateien unterscheiden.</summary>
    private static (string, string)? FolderPair(string pathA, string pathB)
    {
        var a = pathA.Split(Path.DirectorySeparatorChar);
        var b = pathB.Split(Path.DirectorySeparatorChar);
        var k = 0;
        while (k < Math.Min(a.Length, b.Length) - 1 && string.Equals(a[^(k + 1)], b[^(k + 1)], StringComparison.OrdinalIgnoreCase))
            k++;
        if (k == 0)
            return null; // verschiedene Dateinamen: keine Kopie im Sinne eines Teilbaums
        var dirA = string.Join(Path.DirectorySeparatorChar, a[..^k]);
        var dirB = string.Join(Path.DirectorySeparatorChar, b[..^k]);
        if (string.Equals(dirA, dirB, PathUtil.Comparison))
            return null;
        return PathUtil.Comparer.Compare(dirA, dirB) < 0 ? (dirA, dirB) : (dirB, dirA);
    }

    private static (CopyCandidate, double Evidence)? Evaluate(List<DocumentRecord> live, string dirA, string dirB,
        Dictionary<string, int> hashCounts, IReadOnlyDictionary<long, HashSet<string>> history)
    {
        // Liegt ein Ordner im anderen (z. B. "Huber\Kopie 2022" in "Huber"), gehört der innere nicht zum äußeren.
        Dictionary<string, DocumentRecord> Files(string dir, string other)
        {
            var nested = PathUtil.IsUnder(other, dir);
            return live.Where(d => PathUtil.IsUnder(d.Path, dir) && !(nested && PathUtil.IsUnder(d.Path, other)))
                .GroupBy(d => PathUtil.Key(Path.GetRelativePath(dir, d.Path)))
                .ToDictionary(g => g.Key, g => g.First());
        }
        var filesA = Files(dirA, dirB);
        var filesB = Files(dirB, dirA);
        if (filesA.Count == 0 || filesB.Count == 0)
            return null;

        var newestA = filesA.Values.Max(d => d.LastWriteUtc);
        var newestB = filesB.Values.Max(d => d.LastWriteUtc);
        var aIsCopy = newestA != newestB ? newestA < newestB : LooksLikeCopy(dirA, dirB);
        var (copyDir, copy, origDir, orig, copyNewest, origNewest) = aIsCopy
            ? (dirA, filesA, dirB, filesB, newestA, newestB)
            : (dirB, filesB, dirA, filesA, newestB, newestA);

        int identical = 0, older = 0, newer = 0, onlyCopy = 0;
        var evidence = 0.0;
        foreach (var (key, file) in copy)
        {
            if (!orig.TryGetValue(key, out var original))
                onlyCopy++;
            else if (original.Hash == file.Hash)
            {
                identical++;
                if (file.Size > 0)
                    evidence += 1.0 / Math.Max(1, hashCounts.GetValueOrDefault(file.Hash, 2) - 1);
            }
            else if (file.LastWriteUtc > original.LastWriteUtc)
                newer++;
            else
            {
                older++;
                // Belegt: Inhalt ist ein früherer Stand genau dieses Original-Dokuments.
                if (history.TryGetValue(original.Id, out var states) && states.Contains(file.Hash))
                    evidence += 1;
            }
        }
        return (new CopyCandidate(copyDir, origDir, copy.Count, orig.Count, identical, older, newer, onlyCopy, copyNewest, origNewest), evidence);
    }

    /// <summary>Bei gleichem Alter: Name wie "Kopie …"/"alt", oder der innere Ordner ist die Kopie.</summary>
    private static bool LooksLikeCopy(string dirA, string dirB)
    {
        static bool Named(string dir) => CopyWords.Any(w => Path.GetFileName(dir).Contains(w, StringComparison.OrdinalIgnoreCase));
        if (Named(dirA) != Named(dirB))
            return Named(dirA);
        if (PathUtil.IsUnder(dirA, dirB) != PathUtil.IsUnder(dirB, dirA))
            return PathUtil.IsUnder(dirA, dirB);
        return false;
    }
}
