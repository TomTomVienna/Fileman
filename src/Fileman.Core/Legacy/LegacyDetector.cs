namespace Fileman.Core.Legacy;

/// <summary>Ein Ordner, der wie eine alte Sicherung (Momentaufnahme) aussieht – oder eben nicht.</summary>
/// <param name="SiblingSimilarity">Größte Ähnlichkeit der Ordnerstruktur mit einem anderen Kandidaten (0–1).</param>
/// <param name="MasterOverlap">Anteil der Ordner eines gesicherten Quellordners, die auch hier vorkommen (0–1).</param>
/// <param name="Suggested">Vorschlag: als Altbestand übernehmen.</param>
/// <param name="Reason">Begründung für die Anzeige.</param>
public sealed record LegacyCandidate(
    string Path,
    DateTime? DateFromName,
    int Folders,
    double SiblingSimilarity,
    double MasterOverlap,
    bool Suggested,
    bool AlreadyAdded,
    string Reason);

/// <summary>
/// Findet in einem Oberordner (z. B. "E:\Sicherungen" oder "E:\") die Ordner, die alte Sicherungen sind:
/// Datum im Namen oder gleiche Ordnerstruktur wie andere Datumsordner. Eine Zwischenebene ohne Datum, die selbst
/// Datumsordner enthält ("E:\Sicherungen"), wird automatisch durchsucht. Gelesen werden nur Ordnernamen.
/// </summary>
public static class LegacyDetector
{
    private const int FingerprintDepth = 3;
    private const double SimilarityThreshold = 0.5;

    /// <param name="parent">Der gewählte Oberordner.</param>
    /// <param name="roots">Gesicherte Quellordner (Master) – für den Hinweis „passt zu …“ und zum Ausschließen.</param>
    /// <param name="excluded">Ordner, die nie vorgeschlagen werden (z. B. das Sicherungsarchiv).</param>
    /// <param name="alreadyAdded">Bereits eingetragene Altbestände.</param>
    public static IReadOnlyList<LegacyCandidate> Detect(string parent, IReadOnlyList<string> roots, IReadOnlyList<string> excluded,
        IReadOnlyCollection<string> alreadyAdded, CancellationToken cancellationToken = default)
    {
        parent = PathUtil.Normalize(parent);
        if (!Directory.Exists(parent))
            throw new FilemanException($"Der Ordner \"{parent}\" existiert nicht.");

        var candidates = new List<string>();
        foreach (var child in SubFolders(parent))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Zwischenebene ohne Datum, die selbst mehrere Datumsordner enthält (z. B. "Sicherungen"): eine Ebene tiefer.
            var datedChildren = LegacyAnalyzer.DateFromName(System.IO.Path.GetFileName(child)) is null
                ? SubFolders(child).Where(c => LegacyAnalyzer.DateFromName(System.IO.Path.GetFileName(c)) is not null).ToList()
                : [];
            if (datedChildren.Count >= 2)
                candidates.AddRange(SubFolders(child));
            else
                candidates.Add(child);
        }

        var blocked = roots.Concat(excluded).Select(PathUtil.Normalize).ToList();
        candidates = candidates.Where(c => !blocked.Any(b => PathUtil.IsUnder(c, b) || PathUtil.IsUnder(b, c))).ToList();

        var fingerprints = candidates.ToDictionary(c => c, c => Fingerprint(c, cancellationToken), PathUtil.Comparer);
        // Struktur unterhalb der gesicherten Ordner; der Name des Ordners selbst spielt keine Rolle
        // (gesichert als "D:\Kanzlei", in der alten Sicherung vielleicht "Mandanten").
        var masterPrints = roots.Select(PathUtil.Normalize).Where(Directory.Exists)
            .Select(r => Fingerprint(r, cancellationToken))
            .Where(m => m.Count > 0)
            .ToList();
        var added = alreadyAdded.Select(PathUtil.Normalize).ToHashSet(PathUtil.Comparer);

        var result = new List<LegacyCandidate>();
        foreach (var candidate in candidates)
        {
            var print = fingerprints[candidate];
            var similarity = candidates.Where(other => !PathUtil.Comparer.Equals(other, candidate))
                .Select(other => Jaccard(print, fingerprints[other]))
                .DefaultIfEmpty(0).Max();
            var suffixes = WithoutLeadingFolders(print);
            var master = masterPrints.Select(m => (double)m.Count(suffixes.Contains) / m.Count).DefaultIfEmpty(0).Max();
            var date = LegacyAnalyzer.DateFromName(System.IO.Path.GetFileName(candidate));

            var reasons = new List<string>();
            if (date is not null)
                reasons.Add("Datum im Namen");
            if (similarity >= SimilarityThreshold)
                reasons.Add($"Struktur wie andere Sicherungen ({similarity:P0})");
            if (master >= 0.3)
                reasons.Add($"passt zu einem gesicherten Ordner ({master:P0})");
            var empty = !HasEntries(candidate);
            var suggested = !added.Contains(candidate) && !empty && (date is not null || similarity >= SimilarityThreshold);
            if (empty)
                reasons = ["leer"];
            else if (reasons.Count == 0)
                reasons.Add("keine Ähnlichkeit mit den anderen Ordnern");
            if (added.Contains(candidate))
                reasons.Insert(0, "bereits eingetragen");

            result.Add(new LegacyCandidate(candidate, date, print.Count, similarity, master, suggested, added.Contains(candidate), string.Join(", ", reasons)));
        }
        return result.OrderBy(c => c.DateFromName ?? DateTime.MaxValue).ThenBy(c => c.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static bool HasEntries(string folder)
    {
        try { return Directory.EnumerateFileSystemEntries(folder).Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static List<string> SubFolders(string folder)
    {
        try
        {
            return Directory.EnumerateDirectories(folder)
                .Where(d => !Configuration.FilemanSettings.DefaultExcludePatterns.Contains(System.IO.Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                .Where(d => (File.GetAttributes(d) & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Relative Ordnerpfade bis zur dritten Ebene – „wie sieht der Baum aus“.</summary>
    private static HashSet<string> Fingerprint(string folder, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = FingerprintDepth - 1, IgnoreInaccessible = true };
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(folder, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(System.IO.Path.GetRelativePath(folder, dir));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return result;
    }

    /// <summary>Ordnerpfade zusätzlich ohne die ersten ein, zwei Ebenen ("Mandanten\Huber\2022" → "Huber\2022", "2022").</summary>
    private static HashSet<string> WithoutLeadingFolders(HashSet<string> paths)
    {
        var result = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var parts = path.Split(System.IO.Path.DirectorySeparatorChar);
            for (var skip = 1; skip < Math.Min(parts.Length, 3); skip++)
                result.Add(string.Join(System.IO.Path.DirectorySeparatorChar, parts[skip..]));
        }
        return result;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0;
        var intersection = a.Count(b.Contains);
        return (double)intersection / (a.Count + b.Count - intersection);
    }
}
