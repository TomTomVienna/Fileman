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

        var root = new RootFolder { Path = full, Name = string.IsNullOrWhiteSpace(name) ? null : name };
        settings.Roots.Add(root);
        return root;
    }

    public static bool RemoveRoot(FilemanSettings settings, string pathOrName)
    {
        var removed = settings.Roots.RemoveAll(r =>
            string.Equals(r.Name, pathOrName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(PathUtil.Normalize(r.Path), PathUtil.Normalize(pathOrName), PathUtil.Comparison));
        return removed > 0;
    }
}
