using System.Globalization;
using Fileman.Core.Archive;
using Fileman.Core.Catalog;

namespace Fileman.Core.Metadata;

/// <summary>Eine Zeile im Vergleich zweier Versionen.</summary>
public sealed record ComparisonRow(string Property, string Left, string Right)
{
    public bool Differs => !string.Equals(Left, Right, StringComparison.Ordinal);
}

/// <summary>Stellt Datei- und Dokument-Eigenschaften zweier Versionen nebeneinander.</summary>
public static class VersionComparison
{
    public static IReadOnlyList<ComparisonRow> Compare(BackupArchive archive, VersionRecord left, VersionRecord right)
    {
        var leftProps = Properties(archive, left);
        var rightProps = Properties(archive, right);
        var keys = leftProps.Select(p => p.Key).Concat(rightProps.Select(p => p.Key)).Distinct().ToList();
        return keys.Select(k => new ComparisonRow(k,
                leftProps.FirstOrDefault(p => p.Key == k).Value ?? "",
                rightProps.FirstOrDefault(p => p.Key == k).Value ?? ""))
            .ToList();
    }

    public static List<KeyValuePair<string, string>> Properties(BackupArchive archive, VersionRecord version)
    {
        var culture = CultureInfo.GetCultureInfo("de-AT");
        var list = new List<KeyValuePair<string, string>>
        {
            new("Dateiname", Path.GetFileName(version.Path)),
            new("Ordner", Path.GetDirectoryName(version.Path) ?? ""),
            new("Dateistand", version.LastWriteUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", culture)),
            new("Gesichert am", version.CapturedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", culture)),
            new("Größe (Bytes)", version.Size.ToString("N0", culture)),
            new("Prüfsumme (SHA-256)", version.Hash),
        };
        try
        {
            using var content = archive.Blobs.OpenRead(version.Hash);
            list.AddRange(DocumentMetadataReader.Read(content, version.Path));
        }
        catch (FilemanException ex)
        {
            list.Add(new("Hinweis", ex.Message));
        }
        return list;
    }
}
