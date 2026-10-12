using Fileman.Core.Catalog;

namespace Fileman.Core.Legacy;

/// <summary>
/// Vorgeschlagene Zuordnung: Ordner <see cref="LegacyPrefix"/> innerhalb eines Altbestands entspricht dem Ordner
/// <see cref="TargetPrefix"/> im Master (einem gesicherten Quellordner).
/// </summary>
/// <param name="LegacyPrefix">Relativ zum Altbestand-Ordner; "" = der Altbestand-Ordner selbst.</param>
/// <param name="Files">Dateien, die über diese Zuordnung übernommen würden.</param>
/// <param name="Matched">Davon im Master (heute oder früher) unter dem Zielpfad bekannt.</param>
/// <param name="NewerThanMaster">Davon mit jüngerem Änderungsdatum als der aktuelle Stand im Master (unplausibel).</param>
public sealed record LegacyMapping(long SourceId, string SourcePath, string LegacyPrefix, string TargetPrefix,
    int Files, int Matched, int NewerThanMaster)
{
    public double MatchRate => Files == 0 ? 0 : (double)Matched / Files;
}

/// <summary>Mehrere Altbestände mit derselben Zuordnung (typisch: jeder Datumsordner enthält denselben Teilbaum).</summary>
public sealed record LegacyMappingGroup(string LegacyPrefix, string TargetPrefix, IReadOnlyList<LegacyMapping> Mappings)
{
    public int Files => Mappings.Sum(m => m.Files);
    public int Matched => Mappings.Sum(m => m.Matched);
    public int NewerThanMaster => Mappings.Sum(m => m.NewerThanMaster);
    public double MatchRate => Files == 0 ? 0 : (double)Matched / Files;
}

public sealed class LegacyProposal
{
    public required IReadOnlyList<LegacySourceRecord> Sources { get; init; }
    public required IReadOnlyList<LegacyMapping> Mappings { get; init; }
    /// <summary>Dateien je Altbestand, für die keine Zuordnung gefunden wurde (bleiben nur im Bereich _Altbestand).</summary>
    public required IReadOnlyDictionary<long, int> UnmappedFiles { get; init; }
    /// <summary>Beispiele nicht zuordenbarer Dateien (Altbestand-Ordner + relativer Pfad).</summary>
    public required IReadOnlyList<string> UnmappedExamples { get; init; }

    /// <summary>Gleiche Zuordnungen über alle Altbestände zusammengefasst – für die Anzeige.</summary>
    public IReadOnlyList<LegacyMappingGroup> Groups =>
        Mappings.GroupBy(m => (Legacy: PathUtil.Key(m.LegacyPrefix), Target: PathUtil.Key(m.TargetPrefix)))
            .Select(g => new LegacyMappingGroup(g.First().LegacyPrefix, g.First().TargetPrefix, g.ToList()))
            .OrderBy(g => g.TargetPrefix, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g.LegacyPrefix, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}

/// <summary>
/// Findet automatisch heraus, welche Ordner der Altbestände welchen Ordnern im Master (gesicherte Quellordner) entsprechen.
/// <para>
/// Für jede Altbestand-Datei werden Dokumente gleichen Namens gesucht und bewertet: gleicher Inhalt (Hash eines
/// bekannten Stands) zählt am meisten, danach die Zahl übereinstimmender Pfadebenen von hinten
/// ("Huber GmbH\2023\Bilanz.xlsx"). Jede Datei „stimmt“ so für eine Zuordnung Altbestand-Ordner → Master-Ordner.
/// Pro Altbestand-Ordner gewinnt die Zuordnung mit klarer Mehrheit. Zuordnungen, die sich schon aus einer übergeordneten
/// ergeben, entfallen; übrig bleiben die Hauptzuordnung und Ausnahmen (z. B. umbenannte Mandantenordner).
/// </para>
/// <para>
/// Die Stimmen aller Altbestände werden gemeinsam ausgewertet: Altbestände sind Momentaufnahmen desselben Baums. Was in
/// einer Sicherung eindeutig belegt ist (z. B. per Inhalt), gilt so auch für ältere Sicherungen, in denen die Dateien
/// noch andere Inhalte hatten.
/// </para>
/// </summary>
public static class LegacyMapper
{
    private const double MinShare = 0.6;
    private const double MinWeight = 2;

    public static LegacyProposal Propose(CatalogDatabase catalog, IReadOnlyList<string> roots)
    {
        var rootPaths = roots.Select(PathUtil.Normalize).ToList();
        var sources = catalog.GetLegacySources().Where(s => s.ImportedUtc is not null).ToList();
        var documents = catalog.GetAllDocuments();
        var versionHashes = catalog.GetVersionHashesByDocument();
        var byName = documents
            .Select(d => (Doc: d, Segments: Split(d.Path), Root: rootPaths.FirstOrDefault(r => PathUtil.IsUnder(d.Path, r))))
            .Where(c => c.Root is not null)
            .GroupBy(c => c.Doc.FileName.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.Select(c => (c.Doc, c.Segments, RootDepth: Split(c.Root!).Length)).ToList());
        var docsByPath = documents.GroupBy(d => PathUtil.Key(d.Path)).ToDictionary(g => g.Key, g => g.OrderBy(d => d.IsDeleted).First());

        var filesBySource = sources.ToDictionary(s => s.Id, s => catalog.GetLegacyFiles(s.Id));
        var votes = new Dictionary<(string Legacy, string Target), double>();
        foreach (var files in filesBySource.Values)
        {
            foreach (var (key, weight) in Vote(files, byName, versionHashes))
                votes[key] = votes.GetValueOrDefault(key) + weight;
        }
        var global = Decide(votes);

        var mappings = new List<LegacyMapping>();
        var unmapped = new Dictionary<long, int>();
        var unmappedExamples = new List<string>();
        foreach (var source in sources)
        {
            var files = filesBySource[source.Id];
            var accepted = global;

            var stats = accepted.ToDictionary(m => m, _ => (Files: 0, Matched: 0, Newer: 0));
            var unmappedCount = 0;
            foreach (var file in files)
            {
                var mapping = Resolve(accepted, file.RelativePath);
                if (mapping is null)
                {
                    unmappedCount++;
                    if (unmappedExamples.Count < 50)
                        unmappedExamples.Add(Path.Combine(source.Path, file.RelativePath));
                    continue;
                }
                var target = Target(mapping.Value, file.RelativePath);
                var s = stats[mapping.Value];
                s.Files++;
                if (docsByPath.TryGetValue(PathUtil.Key(target), out var doc))
                {
                    s.Matched++;
                    if (!doc.IsDeleted && file.LastWriteUtc > doc.LastWriteUtc.AddSeconds(2))
                        s.Newer++;
                }
                stats[mapping.Value] = s;
            }
            unmapped[source.Id] = unmappedCount;
            // Nur Zuordnungen, die in diesem Altbestand tatsächlich Dateien betreffen.
            mappings.AddRange(accepted.Where(m => stats[m].Files > 0).Select(m => new LegacyMapping(source.Id, source.Path, m.Legacy, m.Target,
                stats[m].Files, stats[m].Matched, stats[m].Newer)));
        }
        return new LegacyProposal { Sources = sources, Mappings = mappings, UnmappedFiles = unmapped, UnmappedExamples = unmappedExamples };
    }

    /// <summary>Zielpfad einer Altbestand-Datei nach einer Zuordnung.</summary>
    public static string Target((string Legacy, string Target) mapping, string relativePath)
    {
        var rest = mapping.Legacy.Length == 0 ? relativePath : relativePath[(mapping.Legacy.Length + 1)..];
        return mapping.Target + Path.DirectorySeparatorChar + rest;
    }

    /// <summary>Die spezifischste Zuordnung (längster Altbestand-Ordner), die die Datei umfasst.</summary>
    public static (string Legacy, string Target)? Resolve(IEnumerable<(string Legacy, string Target)> mappings, string relativePath)
    {
        (string, string)? best = null;
        var bestLength = -1;
        foreach (var m in mappings)
        {
            var covers = m.Legacy.Length == 0
                         || relativePath.StartsWith(m.Legacy + Path.DirectorySeparatorChar, PathUtil.Comparison);
            if (covers && m.Legacy.Length > bestLength)
            {
                best = m;
                bestLength = m.Legacy.Length;
            }
        }
        return best;
    }

    private static Dictionary<(string Legacy, string Target), double> Vote(
        List<LegacyFileRecord> files,
        Dictionary<string, List<(DocumentRecord Doc, string[] Segments, int RootDepth)>> byName,
        Dictionary<long, HashSet<string>> versionHashes)
    {
        var votes = new Dictionary<(string, string), double>();
        foreach (var file in files)
        {
            var legacy = Split(file.RelativePath);
            if (!byName.TryGetValue(legacy[^1].ToUpperInvariant(), out var candidates))
                continue;

            var scored = new List<(bool Hash, int K, string Legacy, string Target)>();
            foreach (var (doc, segments, rootDepth) in candidates)
            {
                // Übereinstimmende Ebenen von hinten – höchstens bis zum Quellordner (Ziel muss darin liegen).
                var maxK = Math.Min(legacy.Length, segments.Length - rootDepth);
                var k = 0;
                while (k < maxK && string.Equals(legacy[^(k + 1)], segments[^(k + 1)], StringComparison.OrdinalIgnoreCase))
                    k++;
                var hash = doc.Hash == file.Hash || (versionHashes.TryGetValue(doc.Id, out var set) && set.Contains(file.Hash));
                if (k == 0 || (k < 2 && !hash))
                    continue;
                scored.Add((hash, k, Join(legacy[..^k]), Join(segments[..^k])));
            }
            if (scored.Count == 0)
                continue;

            // Gleicher Inhalt schlägt Pfadähnlichkeit; bei Gleichstand teilen sich die Kandidaten die Stimme.
            var best = scored.Where(s => s.Hash == scored.Any(x => x.Hash)).ToList();
            var maxLevels = best.Max(s => s.K);
            best = best.Where(s => s.K == maxLevels).DistinctBy(s => (PathUtil.Key(s.Legacy), PathUtil.Key(s.Target))).ToList();
            var weight = (best[0].Hash ? 3.0 : 1.0) / best.Count;
            foreach (var s in best)
                votes[(s.Legacy, s.Target)] = votes.GetValueOrDefault((s.Legacy, s.Target)) + weight;
        }
        return votes;
    }

    private static List<(string Legacy, string Target)> Decide(Dictionary<(string Legacy, string Target), double> votes)
    {
        var winners = votes
            .GroupBy(v => PathUtil.Key(v.Key.Legacy))
            .Select(g =>
            {
                var total = g.Sum(v => v.Value);
                var top = g.MaxBy(v => v.Value);
                return (top.Key, top.Value, Share: top.Value / total);
            })
            .Where(w => w.Value >= MinWeight && w.Share >= MinShare)
            .Select(w => w.Key)
            .OrderBy(w => w.Legacy.Length)
            .ToList();

        // Zuordnungen, die schon aus einer übergeordneten folgen, sind überflüssig.
        var result = new List<(string Legacy, string Target)>();
        foreach (var candidate in winners)
        {
            var parent = Resolve(result, candidate.Legacy + Path.DirectorySeparatorChar + "x");
            if (parent is { } p && string.Equals(Target(p, candidate.Legacy), candidate.Target, PathUtil.Comparison))
                continue;
            result.Add(candidate);
        }
        return result;
    }

    private static string[] Split(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string Join(IEnumerable<string> segments) => string.Join(Path.DirectorySeparatorChar, segments);
}
