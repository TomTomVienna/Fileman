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
        Blobs = new BlobStore(Path.Combine(directory, "blobs"));
        Catalog = CatalogDatabase.Open(Path.Combine(directory, CatalogFileName), readOnly);
    }

    public string Directory { get; }
    public TargetMarker Marker { get; }
    public CatalogDatabase Catalog { get; }
    public BlobStore Blobs { get; }
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
                archive.Blobs.CleanupTemp();
            return archive;
        }
        catch
        {
            lockStream?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Catalog.Dispose();
        _lock?.Dispose();
    }
}

public sealed class ArchiveBusyException(Exception inner)
    : FilemanException("Das Archiv wird gerade von einem anderen Fileman-Programm verwendet. Bitte später erneut versuchen.", inner);
