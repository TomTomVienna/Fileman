namespace Fileman.Core.Configuration;

/// <summary>Benutzereinstellungen (settings.json).</summary>
public sealed class FilemanSettings
{
    public static readonly string[] DefaultExcludePatterns =
    [
        "~$*",
        "~*.tmp",
        "*.tmp",
        ".~lock.*",
        "Thumbs.db",
        "desktop.ini",
        ".DS_Store",
        "$RECYCLE.BIN",
        "System Volume Information",
    ];

    /// <summary>Überwachte Quellordner.</summary>
    public List<RootFolder> Roots { get; set; } = [];

    /// <summary>Die Sicherungsplatte; null, solange keine eingerichtet ist.</summary>
    public TargetSettings? Target { get; set; }

    /// <summary>Muster (Datei- oder Ordnernamen, * und ? erlaubt), die nicht gesichert werden.</summary>
    public List<string> ExcludePatterns { get; set; } = [.. DefaultExcludePatterns];

    /// <summary>Ruhezeit nach der letzten Änderung, bevor automatisch gesichert wird.</summary>
    public int DebounceSeconds { get; set; } = 10;

    /// <summary>Abstand der vollständigen Abgleichsläufe im Automatik-Modus.</summary>
    public int ReconcileMinutes { get; set; } = 60;

    /// <summary>Höchstgröße des lokalen Zwischenspeichers, solange die Sicherungsplatte fehlt.</summary>
    public int SpoolMaxMegabytes { get; set; } = 2048;

    /// <summary>Gleichzeitig durchsuchte Ordner (0 = automatisch nach Prozessorkernen).</summary>
    public int ScanThreads { get; set; }

    /// <summary>Gleichzeitig gesicherte bzw. geprüfte Dateien (0 = automatisch).</summary>
    public int FileThreads { get; set; }
}

public sealed class RootFolder
{
    public string Path { get; set; } = "";

    /// <summary>Optionaler Anzeigename, z. B. "Mandanten".</summary>
    public string? Name { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Path : Name!;
}

public sealed class TargetSettings
{
    /// <summary>Kennung aus der Markierungsdatei auf der Sicherungsplatte.</summary>
    public Guid Id { get; set; }

    /// <summary>Zuletzt bekannter Archivordner, z. B. "E:\FilemanArchiv". Der Laufwerksbuchstabe darf sich ändern.</summary>
    public string LastKnownPath { get; set; } = "";
}
