namespace Fileman.Core;

/// <summary>Pfad-Hilfen, die sich je nach Betriebssystem korrekt verhalten (Windows: Groß-/Kleinschreibung egal).</summary>
public static class PathUtil
{
    public static bool IgnoreCase { get; } = OperatingSystem.IsWindows();

    public static StringComparison Comparison => IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer Comparer => IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Vollständiger Pfad ohne abschließendes Trennzeichen.</summary>
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Vergleichsschlüssel für die Datenbank.</summary>
    public static string Key(string fullPath) => IgnoreCase ? fullPath.ToUpperInvariant() : fullPath;

    /// <summary>True, wenn <paramref name="path"/> gleich <paramref name="directory"/> ist oder darunter liegt.</summary>
    public static bool IsUnder(string path, string directory)
    {
        if (string.Equals(path, directory, Comparison))
            return true;
        var prefix = directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, Comparison);
    }
}
