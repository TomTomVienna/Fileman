using System.Text.Json;

namespace Fileman.Core.Configuration;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static FilemanSettings Load(string file)
    {
        if (!File.Exists(file))
            return new FilemanSettings();
        using var stream = File.OpenRead(file);
        return JsonSerializer.Deserialize<FilemanSettings>(stream, JsonOptions) ?? new FilemanSettings();
    }

    /// <summary>Unabhängige Kopie, z. B. zum Bearbeiten in einem Dialog.</summary>
    public static FilemanSettings Clone(FilemanSettings settings) =>
        JsonSerializer.Deserialize<FilemanSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)!;

    /// <summary>Speichert atomar (erst temporäre Datei, dann ersetzen), damit nie eine halbe Datei entsteht.</summary>
    public static void Save(FilemanSettings settings, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        using (var stream = File.Create(temp))
            JsonSerializer.Serialize(stream, settings, JsonOptions);
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>Fügt einen Quellordner hinzu und prüft, dass er existiert und sich nicht mit anderen überschneidet.</summary>
    public static RootFolder AddRoot(FilemanSettings settings, string path, string? name)
    {
        var full = PathUtil.Normalize(path);
        if (!Directory.Exists(full))
            throw new FilemanException($"Der Ordner \"{full}\" existiert nicht.");
        foreach (var existing in settings.Roots)
        {
            var other = PathUtil.Normalize(existing.Path);
            if (PathUtil.IsUnder(full, other) || PathUtil.IsUnder(other, full))
                throw new FilemanException($"\"{full}\" überschneidet sich mit dem bereits überwachten Ordner \"{other}\".");
        }
        if (settings.Target is { LastKnownPath.Length: > 0 } target
            && (PathUtil.IsUnder(target.LastKnownPath, full) || PathUtil.IsUnder(full, target.LastKnownPath)))
            throw new FilemanException("Der Quellordner darf das Sicherungsarchiv nicht enthalten und nicht darin liegen.");
        if (settings.LegacyFolders.Any(l => PathUtil.IsUnder(full, l.Path) || PathUtil.IsUnder(l.Path, full)))
            throw new FilemanException($"\"{full}\" überschneidet sich mit einem Altbestand. Altbestände werden nur einmal eingelesen, nicht laufend gesichert.");

        var root = new RootFolder { Path = full, Name = string.IsNullOrWhiteSpace(name) ? null : name };
        settings.Roots.Add(root);
        return root;
    }

    /// <summary>
    /// Fügt Altbestand-Ordner hinzu. Sie dürfen sich nicht mit Quellordnern oder dem Archiv überschneiden;
    /// sie müssen nicht erreichbar sein (externe Platte darf abgesteckt sein).
    /// </summary>
    /// <returns>Anzahl neu hinzugefügter Ordner (bereits eingetragene werden übersprungen).</returns>
    public static int AddLegacyFolders(FilemanSettings settings, IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
        {
            var full = PathUtil.Normalize(path);
            if (settings.LegacyFolders.Any(l => string.Equals(PathUtil.Normalize(l.Path), full, PathUtil.Comparison)))
                continue;
            foreach (var root in settings.Roots)
            {
                if (PathUtil.IsUnder(full, root.Path) || PathUtil.IsUnder(root.Path, full))
                    throw new FilemanException($"\"{full}\" überschneidet sich mit dem gesicherten Ordner \"{root.Path}\". Altbestände müssen getrennt liegen (z. B. auf der externen Platte).");
            }
            if (settings.Target is { LastKnownPath.Length: > 0 } target
                && (PathUtil.IsUnder(full, target.LastKnownPath) || PathUtil.IsUnder(target.LastKnownPath, full)))
                throw new FilemanException("Ein Altbestand darf nicht im Sicherungsarchiv liegen.");
            settings.LegacyFolders.Add(new LegacyFolder { Path = full });
            added++;
        }
        return added;
    }

    public static bool RemoveRoot(FilemanSettings settings, string pathOrName)
    {
        var removed = settings.Roots.RemoveAll(r =>
            string.Equals(r.Name, pathOrName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(PathUtil.Normalize(r.Path), PathUtil.Normalize(pathOrName), PathUtil.Comparison));
        return removed > 0;
    }
}
