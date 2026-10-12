using System.Text.Json;

namespace Fileman.Core.Archive;

/// <summary>Inhalt der Datei fileman-target.json im Archivordner. Identifiziert die Sicherungsplatte.</summary>
public sealed class TargetMarker
{
    public const string FileName = "fileman-target.json";

    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public int FormatVersion { get; set; } = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static TargetMarker? TryRead(string archiveDirectory)
    {
        var file = Path.Combine(archiveDirectory, FileName);
        try
        {
            if (!File.Exists(file))
                return null;
            return JsonSerializer.Deserialize<TargetMarker>(File.ReadAllText(file), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Write(string archiveDirectory) =>
        File.WriteAllText(Path.Combine(archiveDirectory, FileName), JsonSerializer.Serialize(this, JsonOptions));
}
