using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Fileman.Core.Metadata;

/// <summary>
/// Liest eingebettete Dokument-Eigenschaften für den Versionsvergleich:
/// Office (docx/xlsx/pptx …: Titel, Autor, zuletzt gespeichert von, Revision …) und PDF (Dokumentinfo).
/// Bewusst ohne Fremdbibliotheken; unbekannte Formate liefern eine leere Liste.
/// </summary>
public static partial class DocumentMetadataReader
{
    private const long MaxOfficeSize = 200L * 1024 * 1024;
    private const int PdfScanBytes = 256 * 1024;

    private static readonly HashSet<string> OfficeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".docm", ".dotx", ".xlsx", ".xlsm", ".xltx", ".pptx", ".pptm", ".potx",
    };

    public static IReadOnlyList<KeyValuePair<string, string>> Read(Stream content, string fileName)
    {
        try
        {
            var extension = Path.GetExtension(fileName);
            if (OfficeExtensions.Contains(extension))
                return ReadOffice(content);
            if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                return ReadPdf(content);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Xml.XmlException)
        {
            return [new("Hinweis", "Eigenschaften konnten nicht gelesen werden (Datei beschädigt?)")];
        }
        return [];
    }

    // ---------- Office (Open XML) ----------

    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace DcTerms = "http://purl.org/dc/terms/";
    private static readonly XNamespace Cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
    private static readonly XNamespace App = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";

    private static List<KeyValuePair<string, string>> ReadOffice(Stream content)
    {
        using var seekable = ToSeekable(content, MaxOfficeSize);
        if (seekable is null)
            return [];
        using var zip = new ZipArchive(seekable, ZipArchiveMode.Read);
        var result = new List<KeyValuePair<string, string>>();

        if (zip.GetEntry("docProps/core.xml") is { } core)
        {
            using var stream = core.Open();
            var xml = XDocument.Load(stream);
            Add(result, "Titel", xml.Descendants(Dc + "title").FirstOrDefault()?.Value);
            Add(result, "Betreff", xml.Descendants(Dc + "subject").FirstOrDefault()?.Value);
            Add(result, "Autor", xml.Descendants(Dc + "creator").FirstOrDefault()?.Value);
            Add(result, "Zuletzt gespeichert von", xml.Descendants(Cp + "lastModifiedBy").FirstOrDefault()?.Value);
            Add(result, "Erstellt", FormatDate(xml.Descendants(DcTerms + "created").FirstOrDefault()?.Value));
            Add(result, "Geändert", FormatDate(xml.Descendants(DcTerms + "modified").FirstOrDefault()?.Value));
            Add(result, "Revision", xml.Descendants(Cp + "revision").FirstOrDefault()?.Value);
            Add(result, "Stichwörter", xml.Descendants(Cp + "keywords").FirstOrDefault()?.Value);
        }
        if (zip.GetEntry("docProps/app.xml") is { } app)
        {
            using var stream = app.Open();
            var xml = XDocument.Load(stream);
            Add(result, "Anwendung", xml.Descendants(App + "Application").FirstOrDefault()?.Value);
            Add(result, "Firma", xml.Descendants(App + "Company").FirstOrDefault()?.Value);
            Add(result, "Seiten", xml.Descendants(App + "Pages").FirstOrDefault()?.Value);
            Add(result, "Wörter", xml.Descendants(App + "Words").FirstOrDefault()?.Value);
            Add(result, "Bearbeitungszeit (Min.)", xml.Descendants(App + "TotalTime").FirstOrDefault()?.Value);
        }
        return result;
    }

    // ---------- PDF (Info-Dictionary, einfache Fälle) ----------

    [GeneratedRegex(@"/(Title|Author|Subject|Creator|Producer|CreationDate|ModDate)\s*\(((?:\\.|[^\\)])*)\)")]
    private static partial Regex PdfInfoEntry();

    private static List<KeyValuePair<string, string>> ReadPdf(Stream content)
    {
        // Die Dokumentinfo steht meist am Anfang oder Ende der Datei.
        using var seekable = ToSeekable(content, long.MaxValue);
        if (seekable is null)
            return [];
        var head = ReadBytes(seekable, 0, PdfScanBytes);
        var tail = seekable.Length > PdfScanBytes ? ReadBytes(seekable, seekable.Length - PdfScanBytes, PdfScanBytes) : [];
        var text = Encoding.Latin1.GetString(tail) + "\n" + Encoding.Latin1.GetString(head);

        var labels = new Dictionary<string, string>
        {
            ["Title"] = "Titel", ["Author"] = "Autor", ["Subject"] = "Betreff", ["Creator"] = "Erstellt mit",
            ["Producer"] = "PDF-Erzeuger", ["CreationDate"] = "Erstellt", ["ModDate"] = "Geändert",
        };
        var result = new List<KeyValuePair<string, string>>();
        var pageCount = Regex.Matches(Encoding.Latin1.GetString(head), @"/Type\s*/Page[^s]").Count;
        foreach (Match match in PdfInfoEntry().Matches(text))
        {
            var label = labels[match.Groups[1].Value];
            if (result.Any(r => r.Key == label))
                continue;
            var value = DecodePdfString(match.Groups[2].Value);
            Add(result, label, match.Groups[1].Value.EndsWith("Date", StringComparison.Ordinal) ? FormatPdfDate(value) : value);
        }
        if (pageCount > 0 && seekable.Length <= PdfScanBytes)
            Add(result, "Seiten", pageCount.ToString(CultureInfo.InvariantCulture));
        return result;
    }

    private static string DecodePdfString(string raw)
    {
        var unescaped = Regex.Replace(raw, @"\\([nrtbf()\\]|[0-7]{1,3})", m => m.Groups[1].Value switch
        {
            "n" => "\n", "r" => "\r", "t" => "\t", "b" => "\b", "f" => "\f",
            "(" => "(", ")" => ")", "\\" => "\\",
            var octal => ((char)Convert.ToInt32(octal, 8)).ToString(),
        });
        // UTF-16BE mit Byte-Order-Mark
        if (unescaped.Length >= 2 && unescaped[0] == 'þ' && unescaped[1] == 'ÿ')
            return Encoding.BigEndianUnicode.GetString(Encoding.Latin1.GetBytes(unescaped[2..]));
        return unescaped;
    }

    /// <summary>PDF-Datum "D:20261012143200+02'00'" lesbar machen.</summary>
    private static string FormatPdfDate(string value)
    {
        var match = Regex.Match(value, @"^D?:?(\d{4})(\d{2})?(\d{2})?(\d{2})?(\d{2})?");
        if (!match.Success)
            return value;
        string Part(int i, string fallback) => match.Groups[i].Success ? match.Groups[i].Value : fallback;
        return $"{Part(3, "01")}.{Part(2, "01")}.{Part(1, "")} {Part(4, "00")}:{Part(5, "00")}";
    }

    private static string? FormatDate(string? iso) =>
        DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)
            ? utc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
            : iso;

    private static void Add(List<KeyValuePair<string, string>> list, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            list.Add(new(key, value.Trim()));
    }

    private static MemoryStream? ToSeekable(Stream content, long maxSize)
    {
        var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = content.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (memory.Length + read > maxSize)
            {
                memory.Dispose();
                return null;
            }
            memory.Write(buffer, 0, read);
        }
        memory.Position = 0;
        return memory;
    }

    private static byte[] ReadBytes(Stream stream, long offset, int count)
    {
        stream.Position = offset;
        var buffer = new byte[Math.Min(count, stream.Length - offset)];
        stream.ReadExactly(buffer);
        return buffer;
    }
}
