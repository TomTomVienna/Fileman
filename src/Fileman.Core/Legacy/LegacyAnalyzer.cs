using System.Globalization;
using System.Text.RegularExpressions;
using Fileman.Core.Scanning;

namespace Fileman.Core.Legacy;

/// <summary>
/// Analysiert alte Sicherungen der Form "&lt;Ordner&gt;\&lt;Datumsordner&gt;\…" (z. B. per Explorer kopierte Teilbäume).
/// Liest nur Metadaten (Pfad, Größe, Änderungsdatum) – keine Inhalte – und verändert nichts.
/// </summary>
public static partial class LegacyAnalyzer
{
    /// <param name="root">Ordner, dessen Unterordner die einzelnen Sicherungen sind (z. B. "E:\Sicherungen").</param>
    public static LegacyAnalysis Analyze(string root, IProgress<string>? progress = null, CancellationToken cancellationToken = default,
        int parallelism = 0)
    {
        root = PathUtil.Normalize(root);
        if (!Directory.Exists(root))
            throw new FilemanException($"Der Ordner \"{root}\" existiert nicht.");

        var folders = Directory.EnumerateDirectories(root)
            .Where(d => !Configuration.FilemanSettings.DefaultExcludePatterns.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (folders.Count == 0)
            throw new FilemanException($"In \"{root}\" gibt es keine Unterordner. Bitte den Ordner wählen, in dem die Datumsordner liegen.");
        return Analyze(folders, root, progress, cancellationToken, parallelism);
    }

    /// <summary>Analysiert die angegebenen Datumsordner (z. B. die eingetragenen Altbestände).</summary>
    /// <param name="title">Bezeichnung im Bericht.</param>
    public static LegacyAnalysis Analyze(IReadOnlyList<string> folders, string title, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default, int parallelism = 0)
    {
        if (folders.Count == 0)
            throw new FilemanException("Es sind keine erreichbaren Altbestand-Ordner vorhanden.");
        var scanner = new FileScanner(new ExclusionFilter(Configuration.FilemanSettings.DefaultExcludePatterns), parallelism: parallelism);

        var scanned = new List<(LegacySnapshot Snapshot, IReadOnlyList<DiskFile> Files)>();
        var unreadable = new List<string>();
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Lese {Path.GetFileName(folder)} …");
            var scan = scanner.Scan(folder, cancellationToken: cancellationToken);
            unreadable.AddRange(scan.FailedDirectories.Select(f => f.Directory));
            var files = scan.Files;
            var topFolders = Directory.EnumerateDirectories(folder).Select(Path.GetFileName).OfType<string>().Order(StringComparer.CurrentCultureIgnoreCase).ToList();
            var snapshot = new LegacySnapshot(
                folder,
                Path.GetFileName(folder),
                DateFromName(Path.GetFileName(folder)),
                files.Count == 0 ? Directory.GetLastWriteTimeUtc(folder) : files.Max(f => f.LastWriteUtc),
                files.Count,
                files.Sum(f => f.Size),
                topFolders);
            scanned.Add((snapshot, files));
        }

        scanned.Sort((a, b) => a.Snapshot.EffectiveDate.CompareTo(b.Snapshot.EffectiveDate));
        progress?.Report("Vergleiche Stände …");

        // Stand = relativer Pfad + Größe + Änderungsdatum (auf 2 s gerundet: FAT/exFAT-Platten speichern ungenauer).
        var states = new Dictionary<(string Key, long Size, long Time), (string Path, long Size, DateTime Utc, List<int> Snapshots)>();
        for (var i = 0; i < scanned.Count; i++)
        {
            var (snapshot, files) = scanned[i];
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(snapshot.Path, file.Path);
                var key = (PathUtil.Key(relative), file.Size, RoundedTime(file.LastWriteUtc));
                if (!states.TryGetValue(key, out var state))
                    states[key] = state = (relative, file.Size, file.LastWriteUtc, []);
                if (state.Snapshots.Count == 0 || state.Snapshots[^1] != i)
                    state.Snapshots.Add(i);
            }
        }

        var stateList = states.Values
            .Select(s => new LegacyFileState(s.Path, s.Size, s.Utc, s.Snapshots))
            .OrderBy(s => s.RelativePath, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(s => s.LastWriteUtc)
            .ToList();

        // Gleiche Datei (Name, Größe, Datum) unter verschiedenen Ordnern: vermutlich verschoben/umbenannter Ordner.
        var moves = stateList
            .Where(s => s.Size > 0)
            .GroupBy(s => (Name: Path.GetFileName(s.RelativePath).ToUpperInvariant(), s.Size, Time: RoundedTime(s.LastWriteUtc)))
            .Select(g => g.Select(s => s.RelativePath).Distinct(PathUtil.Comparer).ToList())
            .Where(paths => paths.Count > 1)
            .Select(paths =>
            {
                var first = stateList.First(s => PathUtil.Comparer.Equals(s.RelativePath, paths[0]));
                return new LegacyPossibleMove(Path.GetFileName(paths[0]), first.Size, first.LastWriteUtc, paths);
            })
            .ToList();

        return new LegacyAnalysis
        {
            RootPath = title,
            CreatedUtc = DateTime.UtcNow,
            Snapshots = scanned.Select(s => s.Snapshot).ToList(),
            States = stateList,
            PossibleMoves = moves,
            UnreadableFolders = unreadable,
        };
    }

    private static long RoundedTime(DateTime utc) => utc.Ticks / TimeSpan.TicksPerSecond / 2;

    [GeneratedRegex(@"(?<y>(19|20)\d{2})[-_.]?(?<m>0[1-9]|1[0-2])[-_.]?(?<d>0[1-9]|[12]\d|3[01])")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"(?<d>0?[1-9]|[12]\d|3[01])\.(?<m>0?[1-9]|1[0-2])\.(?<y>(19|20)?\d{2})")]
    private static partial Regex GermanDate();

    [GeneratedRegex(@"(?<y>(19|20)\d{2})[-_.](?<m>0[1-9]|1[0-2])(?!\d)")]
    private static partial Regex YearMonth();

    /// <summary>Erkennt Datumsangaben in Ordnernamen: 2024-06-30, 20240630, 2024_06_30, 30.06.2024, 30.6.24, 2024-06.</summary>
    public static DateTime? DateFromName(string name)
    {
        foreach (var regex in new[] { IsoDate(), GermanDate(), YearMonth() })
        {
            var match = regex.Match(name);
            if (!match.Success)
                continue;
            var year = int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);
            if (year < 100)
                year += 2000;
            var month = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
            var day = match.Groups["d"].Success ? int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture) : 1;
            if (day <= DateTime.DaysInMonth(year, month))
                return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Local).ToUniversalTime();
        }
        return null;
    }
}
