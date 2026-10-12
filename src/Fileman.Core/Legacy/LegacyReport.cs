using System.Globalization;
using System.Net;
using System.Text;

namespace Fileman.Core.Legacy;

/// <summary>Schreibt die Analyse als HTML-Bericht (zum Lesen im Browser) und als CSV (alle Stände, für Excel).</summary>
public static class LegacyReport
{
    private const int MaxListEntries = 300;
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-AT");

    /// <summary>Standard-Ablage: "Dokumente\Fileman-Analyse JJJJ-MM-TT HH-MM.html".</summary>
    public static string DefaultReportPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            $"Fileman-Analyse {System.DateTime.Now:yyyy-MM-dd HH-mm}.html");

    /// <returns>Pfad der CSV-Datei, die neben dem HTML-Bericht abgelegt wird.</returns>
    public static string Write(LegacyAnalysis analysis, string htmlFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(htmlFile))!);
        var csvFile = Path.ChangeExtension(htmlFile, ".csv");
        File.WriteAllText(htmlFile, Html(analysis, Path.GetFileName(csvFile)), new UTF8Encoding(true));
        File.WriteAllText(csvFile, Csv(analysis), new UTF8Encoding(true));
        return csvFile;
    }

    private static string N(long value) => value.ToString("N0", German);
    private static string E(string text) => WebUtility.HtmlEncode(text);
    private static string Date(DateTime utc) => utc.ToLocalTime().ToString("dd.MM.yyyy", German);
    private static string DateTime(DateTime utc) => utc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", German);
    private static string Percent(long part, long whole) => whole == 0 ? "0 %" : (100.0 * part / whole).ToString("0", German) + " %";

    public static string Html(LegacyAnalysis a, string csvName)
    {
        var several = a.FilesWithSeveralStates;
        var missing = a.MissingInNewest;
        var html = new StringBuilder();
        html.Append($$"""
            <!doctype html>
            <html lang="de"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Analyse alter Sicherungen</title>
            <style>
              :root { --ink:#1d2433; --muted:#5d6678; --line:#e3e6ec; --accent:#2e7d32; --warn:#9a6700; --bg:#fafbfc; }
              body { font-family: "Segoe UI", system-ui, sans-serif; color:var(--ink); background:var(--bg); margin:0; }
              main { max-width:1100px; margin:0 auto; padding:24px 16px 48px; }
              h1 { font-size:26px; margin:0 0 4px; } h2 { font-size:19px; margin:32px 0 8px; }
              .sub { color:var(--muted); margin:0 0 20px; }
              .tiles { display:grid; grid-template-columns:repeat(auto-fit,minmax(170px,1fr)); gap:12px; }
              .tile { background:#fff; border:1px solid var(--line); border-radius:8px; padding:12px 14px; }
              .tile b { display:block; font-size:24px; } .tile span { color:var(--muted); font-size:13px; }
              .summary { background:#fff; border-left:4px solid var(--accent); padding:12px 16px; margin-top:16px; line-height:1.55; }
              table { border-collapse:collapse; width:100%; background:#fff; font-size:13px; }
              th, td { text-align:left; padding:6px 8px; border-bottom:1px solid var(--line); vertical-align:top; }
              th { background:#f1f3f6; font-weight:600; } td.num, th.num { text-align:right; white-space:nowrap; }
              .note { color:var(--muted); font-size:13px; } .warn { color:var(--warn); }
              .wrap { overflow-x:auto; } code { font-size:12px; }
            </style></head><body><main>
            <h1>Analyse alter Sicherungen</h1>
            <p class="sub">{{E(a.RootPath)}} · erstellt am {{DateTime(a.CreatedUtc)}} · es wurde nichts verändert</p>
            <div class="tiles">
              <div class="tile"><b>{{N(a.Snapshots.Count)}}</b><span>Sicherungen (Datumsordner)</span></div>
              <div class="tile"><b>{{N(a.TotalFiles)}}</b><span>Dateien insgesamt · {{DisplayText.Size(a.TotalBytes)}}</span></div>
              <div class="tile"><b>{{Percent(a.DuplicateFiles, a.TotalFiles)}}</b><span>davon reine Kopien ({{N(a.DuplicateFiles)}})</span></div>
              <div class="tile"><b>{{N(a.DistinctPaths)}}</b><span>unterschiedliche Dateien</span></div>
              <div class="tile"><b>{{N(several.Count)}}</b><span>davon mit mehreren Ständen</span></div>
              <div class="tile"><b>{{N(missing.Count)}}</b><span>nicht mehr in der jüngsten Sicherung</span></div>
            </div>
            <div class="summary">
              In {{N(a.Snapshots.Count)}} Sicherungen liegen {{N(a.TotalFiles)}} Dateien ({{DisplayText.Size(a.TotalBytes)}}).
              Davon sind <b>{{N(a.DuplicateFiles)}} reine Kopien</b> eines bereits vorhandenen Stands.
              Übrig bleiben <b>{{N(a.DistinctStates)}} unterschiedliche Stände</b> ({{DisplayText.Size(a.DistinctBytes)}}) von {{N(a.DistinctPaths)}} Dateien.
              {{N(several.Count)}} Dateien wurden zwischen den Sicherungen geändert und haben daher mehrere Stände;
              {{N(missing.Count)}} Dateien gibt es in der jüngsten Sicherung nicht mehr (gelöscht, umbenannt oder verschoben).
              <br>Bei einer Übernahme in Fileman würde jeder Stand genau einmal abgelegt – als Version mit Datum im Namen.
            </div>
            """);

        // Sicherungen
        var commonTop = a.Snapshots.SelectMany(s => s.TopFolders).GroupBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() * 2 > a.Snapshots.Count).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        html.Append("""
            <h2>Sicherungen in zeitlicher Reihenfolge</h2>
            <p class="note">„Neue Stände“: Dateien, die in dieser Sicherung erstmals oder in geänderter Form vorkommen. Kursiv: Datum aus dem jüngsten Dateidatum, weil der Ordnername keines enthält.
            Ordner der ersten Ebene, die nur in wenigen Sicherungen vorkommen, sind <span class="warn">markiert</span>.</p>
            <div class="wrap"><table><tr><th>Ordner</th><th>Datum</th><th class="num">Dateien</th><th class="num">Größe</th><th class="num">Neue Stände</th><th>Ordner der ersten Ebene</th></tr>
            """);
        for (var i = 0; i < a.Snapshots.Count; i++)
        {
            var s = a.Snapshots[i];
            var date = s.DateFromName is { } d ? Date(d) : $"<i>{Date(s.NewestFileUtc)}</i>";
            var top = string.Join(", ", s.TopFolders.Select(f => commonTop.Contains(f) ? E(f) : $"<span class=\"warn\">{E(f)}</span>"));
            html.Append($"<tr><td>{E(s.Name)}</td><td>{date}</td><td class=\"num\">{N(s.Files)}</td><td class=\"num\">{DisplayText.Size(s.Bytes)}</td><td class=\"num\">{N(a.NewStatesIn(i))}</td><td>{top}</td></tr>\n");
        }
        html.Append("</table></div>");

        // Dateien mit mehreren Ständen
        html.Append($"""
            <h2>Dateien mit mehreren Ständen ({N(several.Count)})</h2>
            <p class="note">Diese Dateien wurden zwischen den Sicherungen geändert. Jeder Stand würde als eigene Version übernommen.{(several.Count > MaxListEntries ? $" Gezeigt werden die {MaxListEntries} mit den meisten Ständen; alle stehen in der CSV-Datei." : "")}</p>
            <div class="wrap"><table><tr><th>Datei</th><th class="num">Stände</th><th>Änderungsdaten der Stände</th></tr>
            """);
        foreach (var group in several.OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase).Take(MaxListEntries))
            html.Append($"<tr><td><code>{E(group.Key)}</code></td><td class=\"num\">{group.Count()}</td><td>{string.Join(" · ", group.Select(s => DateTime(s.LastWriteUtc)))}</td></tr>\n");
        html.Append("</table></div>");

        // Nicht mehr in der jüngsten Sicherung
        html.Append($"""
            <h2>Nicht mehr in der jüngsten Sicherung ({N(missing.Count)})</h2>
            <p class="note">Diese Dateien wurden später gelöscht, umbenannt oder verschoben. Bei einer Übernahme blieben sie als „gelöscht“ auffindbar.{(missing.Count > MaxListEntries ? $" Gezeigt werden die ersten {MaxListEntries}." : "")}</p>
            <div class="wrap"><table><tr><th>Datei</th><th>Zuletzt enthalten in</th></tr>
            """);
        foreach (var path in missing.Take(MaxListEntries))
        {
            var last = a.States.Where(s => PathUtil.Comparer.Equals(s.RelativePath, path)).Max(s => s.SnapshotIndexes[^1]);
            html.Append($"<tr><td><code>{E(path)}</code></td><td>{E(a.Snapshots[last].Name)}</td></tr>\n");
        }
        html.Append("</table></div>");

        // Vermutlich verschoben
        html.Append($"""
            <h2>Vermutlich verschoben oder umbenannt ({N(a.PossibleMoves.Count)})</h2>
            <p class="note">Gleicher Dateiname, gleiche Größe und gleiches Änderungsdatum an verschiedenen Orten. Fileman würde diese als dieselbe Datei erkennen.{(a.PossibleMoves.Count > MaxListEntries ? $" Gezeigt werden die ersten {MaxListEntries}." : "")}</p>
            <div class="wrap"><table><tr><th>Datei</th><th>Orte</th></tr>
            """);
        foreach (var move in a.PossibleMoves.Take(MaxListEntries))
            html.Append($"<tr><td>{E(move.FileName)}</td><td>{string.Join("<br>", move.RelativePaths.Select(p => $"<code>{E(p)}</code>"))}</td></tr>\n");
        html.Append("</table></div>");

        if (a.UnreadableFolders.Count > 0)
        {
            html.Append($"<h2 class=\"warn\">Nicht lesbare Ordner ({N(a.UnreadableFolders.Count)})</h2><ul>");
            foreach (var folder in a.UnreadableFolders.Take(MaxListEntries))
                html.Append($"<li><code>{E(folder)}</code></li>");
            html.Append("</ul>");
        }

        html.Append($"""
            <p class="note" style="margin-top:32px">Alle {N(a.DistinctStates)} Stände mit den Sicherungen, in denen sie vorkommen: <b>{E(csvName)}</b> (öffnet sich in Excel).<br>
            Grundlage: relativer Pfad, Größe und Änderungsdatum. Explorer-Kopien behalten Größe und Datum, gleiche Werte bedeuten daher denselben Stand.</p>
            </main></body></html>
            """);
        return html.ToString();
    }

    public static string Csv(LegacyAnalysis a)
    {
        static string Q(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        var csv = new StringBuilder();
        csv.AppendLine("Datei;Größe (Bytes);Änderungsdatum;Anzahl Sicherungen;Erste Sicherung;Letzte Sicherung;Stände dieser Datei;Sicherungen");
        var stateCounts = a.States.GroupBy(s => s.RelativePath, PathUtil.Comparer).ToDictionary(g => g.Key, g => g.Count(), PathUtil.Comparer);
        foreach (var s in a.States)
        {
            csv.Append(Q(s.RelativePath)).Append(';')
               .Append(s.Size.ToString(CultureInfo.InvariantCulture)).Append(';')
               .Append(DateTime(s.LastWriteUtc)).Append(';')
               .Append(s.SnapshotIndexes.Count).Append(';')
               .Append(Q(a.Snapshots[s.SnapshotIndexes[0]].Name)).Append(';')
               .Append(Q(a.Snapshots[s.SnapshotIndexes[^1]].Name)).Append(';')
               .Append(stateCounts[s.RelativePath]).Append(';')
               .AppendLine(Q(string.Join(", ", s.SnapshotIndexes.Select(i => a.Snapshots[i].Name))));
        }
        return csv.ToString();
    }
}
