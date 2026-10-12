using System.Text;

namespace Fileman.Core.Protection;

/// <summary>
/// Prüft, ob der Anfang einer Datei zu ihrer Endung passt. Verschlüsselungstrojaner überschreiben Dateien mit
/// Zufallsdaten – eine ".docx" beginnt dann nicht mehr mit "PK", ein ".pdf" nicht mehr mit "%PDF".
/// </summary>
public static class ContentSignature
{
    private const int HeaderBytes = 1024;

    private static readonly byte[] Zip = "PK\u0003\u0004"u8.ToArray();
    private static readonly byte[] ZipEmpty = "PK\u0005\u0006"u8.ToArray();
    private static readonly byte[] Ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]; // altes Office, auch passwortgeschützte docx/xlsx
    private static readonly byte[] Rtf = "{\\rtf"u8.ToArray();

    private static readonly Dictionary<string, Func<byte[], bool>> Checks = new(StringComparer.OrdinalIgnoreCase)
    {
        [".docx"] = h => StartsWith(h, Zip) || StartsWith(h, ZipEmpty) || StartsWith(h, Ole),
        [".docm"] = h => StartsWith(h, Zip) || StartsWith(h, Ole),
        [".xlsx"] = h => StartsWith(h, Zip) || StartsWith(h, ZipEmpty) || StartsWith(h, Ole),
        [".xlsm"] = h => StartsWith(h, Zip) || StartsWith(h, Ole),
        [".pptx"] = h => StartsWith(h, Zip) || StartsWith(h, Ole),
        [".odt"] = h => StartsWith(h, Zip),
        [".ods"] = h => StartsWith(h, Zip),
        [".zip"] = h => StartsWith(h, Zip) || StartsWith(h, ZipEmpty),
        [".doc"] = h => StartsWith(h, Ole) || StartsWith(h, Rtf) || StartsWith(h, Zip),
        [".xls"] = h => StartsWith(h, Ole) || StartsWith(h, Zip) || LooksLikeText(h), // auch HTML/CSV-Exporte
        [".ppt"] = h => StartsWith(h, Ole),
        [".msg"] = h => StartsWith(h, Ole),
        [".rtf"] = h => StartsWith(h, Rtf),
        [".pdf"] = h => Contains(h, "%PDF"u8),
        [".jpg"] = h => StartsWith(h, [0xFF, 0xD8, 0xFF]),
        [".jpeg"] = h => StartsWith(h, [0xFF, 0xD8, 0xFF]),
        [".png"] = h => StartsWith(h, [0x89, 0x50, 0x4E, 0x47]),
        [".gif"] = h => StartsWith(h, "GIF8"u8),
        [".tif"] = h => StartsWith(h, "II*\0"u8) || StartsWith(h, "MM\0*"u8),
        [".tiff"] = h => StartsWith(h, "II*\0"u8) || StartsWith(h, "MM\0*"u8),
        [".7z"] = h => StartsWith(h, [0x37, 0x7A, 0xBC, 0xAF]),
        [".rar"] = h => StartsWith(h, "Rar!"u8),
    };

    /// <summary>Hat Fileman für diese Endung eine Prüfung?</summary>
    public static bool IsKnownFormat(string fileName) => Checks.ContainsKey(Path.GetExtension(fileName));

    /// <summary>True, wenn die Endung bekannt ist, der Inhalt aber nicht dazu passt. Leere Dateien sind nie verdächtig.</summary>
    public static bool LooksWrong(string fileName, string contentPath)
    {
        if (!Checks.TryGetValue(Path.GetExtension(fileName), out var check))
            return false;
        using var stream = new FileStream(contentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var header = new byte[Math.Min(HeaderBytes, stream.Length)];
        stream.ReadExactly(header);
        return header.Length > 0 && !check(header);
    }

    private static bool StartsWith(byte[] header, ReadOnlySpan<byte> signature) => header.AsSpan().StartsWith(signature);

    private static bool Contains(byte[] header, ReadOnlySpan<byte> text) => header.AsSpan().IndexOf(text) >= 0;

    private static bool LooksLikeText(byte[] header)
    {
        var printable = header.Count(b => b is 9 or 10 or 13 or >= 32 and < 127 or >= 0xC0);
        return printable >= header.Length * 0.9 && Encoding.UTF8.GetString(header).Length > 0;
    }
}
