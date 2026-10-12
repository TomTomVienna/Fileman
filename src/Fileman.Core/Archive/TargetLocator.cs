using Fileman.Core.Configuration;

namespace Fileman.Core.Archive;

/// <summary>
/// Findet die Sicherungsplatte, auch wenn Windows ihr einen anderen Laufwerksbuchstaben gegeben hat:
/// gesucht wird derselbe Ordner auf allen bereiten Laufwerken, erkannt wird die Platte an der Kennung in der Markierungsdatei.
/// </summary>
public static class TargetLocator
{
    /// <returns>Der Archivordner oder null, wenn die Platte nicht angesteckt ist.</returns>
    public static string? Locate(TargetSettings target, Func<IEnumerable<string>>? driveRoots = null)
    {
        foreach (var candidate in Candidates(target, driveRoots ?? ReadyDriveRoots))
        {
            if (TargetMarker.TryRead(candidate)?.Id == target.Id)
                return candidate;
        }
        return null;
    }

    private static IEnumerable<string> Candidates(TargetSettings target, Func<IEnumerable<string>> driveRoots)
    {
        if (string.IsNullOrWhiteSpace(target.LastKnownPath))
            yield break;
        yield return target.LastKnownPath;

        var drives = driveRoots().ToList();
        // Laufwerk, auf dem das Archiv zuletzt lag (bei Windows z. B. "E:\"), und der Pfad darunter.
        var lastRoot = drives
            .Where(d => target.LastKnownPath.StartsWith(d, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Length)
            .FirstOrDefault() ?? Path.GetPathRoot(target.LastKnownPath);
        if (string.IsNullOrEmpty(lastRoot))
            yield break;
        var relative = target.LastKnownPath[lastRoot.Length..];
        foreach (var drive in drives)
        {
            if (!string.Equals(drive, lastRoot, StringComparison.OrdinalIgnoreCase))
                yield return Path.Combine(drive, relative);
        }
    }

    private static IEnumerable<string> ReadyDriveRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            bool ready;
            try { ready = drive.IsReady && drive.DriveType is DriveType.Removable or DriveType.Fixed; }
            catch (IOException) { ready = false; }
            if (ready)
                yield return drive.RootDirectory.FullName;
        }
    }
}
