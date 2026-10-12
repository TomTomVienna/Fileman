using System.Text.RegularExpressions;

namespace Fileman.Core.Scanning;

/// <summary>Prüft Datei- und Ordnernamen gegen einfache Muster wie "~$*" oder "*.tmp".</summary>
public sealed class ExclusionFilter
{
    private readonly Regex? _regex;

    public ExclusionFilter(IEnumerable<string> patterns)
    {
        var parts = patterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => "^" + Regex.Escape(p.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$")
            .ToList();
        if (parts.Count > 0)
            _regex = new Regex(string.Join("|", parts), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    public bool IsExcluded(string name) => _regex?.IsMatch(name) ?? false;

    /// <summary>True, wenn der Pfad selbst oder einer seiner Ordner unterhalb von <paramref name="root"/> ausgeschlossen ist.</summary>
    public bool IsExcludedPath(string fullPath, string root)
    {
        if (_regex is null || !PathUtil.IsUnder(fullPath, root) || fullPath.Length <= root.Length)
            return false;
        var relative = fullPath[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(IsExcluded);
    }
}
