using Fileman.Core.Catalog;
using Fileman.Core.Storage;

namespace Fileman.Core.Archive;

/// <summary>
/// Ein geöffnetes Archiv auf der Sicherungsplatte: Markierung, Katalog und Inhaltsspeicher.
/// Beim Öffnen zum Schreiben wird eine Sperre gehalten, damit nie zwei Programme (z. B. Tray und CLI) gleichzeitig schreiben.
/// </summary>
public sealed class BackupArchive : IDisposable
{
    public const string DefaultFolderName = "FilemanArchiv";
    private const string CatalogFileName = "catalog.db";
    private const string LockFileName = "fileman.lock";

    private readonly FileStream? _lock;

    private BackupArchive(string directory, TargetMarker marker, FileStream? lockStream, bool readOnly)
    {
        Directory = directory;
        Marker = marker;
        _lock = lockStream;
        IsReadOnly = readOnly;
        Store = new ArchiveStore(directory);
        Catalog = CatalogDatabase.Open(Path.Combine(directory, CatalogFileName), readOnly);
    }

    public string Directory { get; }
    public TargetMarker Marker { get; }
    public CatalogDatabase Catalog { get; }
    /// <summary>Die lesbare Ablage (Ordner "Dateien").</summary>
    public ArchiveStore Store { get; }
    public bool IsReadOnly { get; }

    /// <summary>Richtet ein neues Archiv ein (Ordner und Markierungsdatei).</summary>
    public static TargetMarker Initialize(string directory, string name)
    {
        directory = PathUtil.Normalize(directory);
        if (TargetMarker.TryRead(directory) is { } existing)
            return existing;
        System.IO.Directory.CreateDirectory(directory);
        var marker = new TargetMarker { Id = Guid.NewGuid(), Name = name, CreatedUtc = DateTime.UtcNow };
        marker.Write(directory);
        CatalogDatabase.Open(Path.Combine(directory, CatalogFileName)).Dispose();
        File.WriteAllText(Path.Combine(directory, "LIESMICH.txt"), ReadmeText);
        return marker;
    }

    public static BackupArchive Open(string directory, bool readOnly = false)
    {
        directory = PathUtil.Normalize(directory);
        var marker = TargetMarker.TryRead(directory)
            ?? throw new FilemanException($"In \"{directory}\" wurde kein Fileman-Archiv gefunden.");

        if (readOnly && !File.Exists(Path.Combine(directory, CatalogFileName)))
            throw new FilemanException($"Das Archiv in \"{directory}\" enthält noch keinen Katalog.");

        FileStream? lockStream = null;
        if (!readOnly)
        {
            try
            {
                lockStream = new FileStream(Path.Combine(directory, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                throw new ArchiveBusyException(ex);
            }
        }

        try
        {
            var archive = new BackupArchive(directory, marker, lockStream, readOnly);
            if (!readOnly)
                archive.Store.CleanupTemp();
            return archive;
        }
        catch
        {
            lockStream?.Dispose();
            throw;
        }
    }

    private const string ReadmeText = """
        FILEMAN – SICHERUNGSARCHIV
        ==========================

        Dieses Laufwerk enthält Sicherungskopien, die das Programm Fileman angelegt hat.
        Alle Dateien sind auch OHNE Fileman zugänglich:

        Ordner "Dateien"
          Darunter liegt die Ordnerstruktur der gesicherten Quellordner. Der erste Ordner ist
          der Laufwerksbuchstabe (z. B. "D" für D:\), Netzwerkpfade beginnen mit "UNC".

          In jedem Ordner liegt
            - der zuletzt gesicherte Stand unter dem exakten Dateinamen, z. B.  Bilanz.xlsx
            - jede gesicherte Version mit ihrem Änderungszeitpunkt, z. B.     Bilanz (2026-10-12 14-32).xlsx

          Gelöschte Dateien bleiben hier erhalten.
          Wurde eine Datei verschoben oder umbenannt, liegen ihre Versionen sowohl im alten als auch
          im neuen Ordner. Wurde ein ganzer Ordner verschoben, steht im alten Ordner die Datei
          "_Fileman – Ordner verschoben.txt" mit dem neuen Ort.

          Gleiche Inhalte belegen dank „Hardlinks“ nur einmal Platz, auch wenn sie mehrfach erscheinen.
          Die Dateien sind schreibgeschützt. Zum Bearbeiten bitte erst an einen anderen Ort kopieren.

        catalog.db           Verzeichnis aller Versionen für Fileman (SQLite-Datenbank).
        fileman-target.json  Kennung dieses Laufwerks. Damit erkennt Fileman es auch unter anderem Laufwerksbuchstaben.
        fileman.lock         Verhindert, dass zwei Fileman-Programme gleichzeitig schreiben.
        tmp                  Zwischenablage während einer Sicherung (kann gelöscht werden, wenn Fileman nicht läuft).

        Bitte in diesem Ordner nichts umbenennen oder löschen.
        """;

    public void Dispose()
    {
        Catalog.Dispose();
        _lock?.Dispose();
    }
}

public sealed class ArchiveBusyException(Exception inner)
    : FilemanException("Das Archiv wird gerade von einem anderen Fileman-Programm verwendet. Bitte später erneut versuchen.", inner);
