using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Fileman.Core.Catalog;

public sealed class FindQuery
{
    /// <summary>Suchbegriffe (durch Leerzeichen getrennt); alle müssen im aktuellen oder einem früheren Pfad vorkommen.</summary>
    public string? Text { get; set; }
    public bool IncludeDeleted { get; set; } = true;
    /// <summary>Nur Dokumente unterhalb dieses Ordners (aktueller Pfad).</summary>
    public string? UnderPath { get; set; }
    public DateTime? ChangedAfterUtc { get; set; }
    public DateTime? ChangedBeforeUtc { get; set; }
    public int Limit { get; set; } = 500;
}

/// <summary>
/// Der Katalog (SQLite): welches Dokument hatte wann unter welchem Pfad welchen Inhalt.
/// Es wird nur hinzugefügt; Dokumente werden bei Löschung lediglich als gelöscht markiert.
/// </summary>
public sealed class CatalogDatabase : IDisposable
{
    private const int SchemaVersion = 3;

    private readonly SqliteConnection _connection;
    private SqliteTransaction? _transaction;

    private CatalogDatabase(SqliteConnection connection)
    {
        _connection = connection;
        _connection.CreateFunction("fm_contains", (string? text, string? word) =>
            text is not null && word is not null && text.Contains(word, StringComparison.CurrentCultureIgnoreCase));
    }

    public string FilePath => _connection.DataSource;

    public static CatalogDatabase Open(string file, bool readOnly = false)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        var catalog = new CatalogDatabase(connection);
        if (!readOnly)
        {
            // Klassisches Rollback-Journal: eine einzige Datei, robust beim Abziehen eines USB-Laufwerks.
            catalog.Execute("PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;");
            catalog.EnsureSchema();
        }
        return catalog;
    }

    private void EnsureSchema()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version"));
        if (version > SchemaVersion)
            throw new FilemanException("Das Archiv wurde mit einer neueren Fileman-Version erstellt. Bitte Fileman aktualisieren.");
        if (version == SchemaVersion)
            return;
        if (version == 1)
            throw new FilemanException("Dieses Archiv hat ein veraltetes Format aus einer Vorabversion. Bitte ein neues Archiv anlegen.");
        if (version == 0)
            CreateSchemaV2();
        // Version 2 → 3: Altbestände (rein additiv, vorhandene Daten bleiben unverändert)
        CreateLegacyTables();
        Execute($"PRAGMA user_version = {SchemaVersion}");
    }

    private void CreateLegacyTables() =>
        Execute("""
            -- Alte Sicherungen (z. B. Datumsordner auf einer externen Platte), einmalig "as is" eingelesen.
            CREATE TABLE IF NOT EXISTS LegacySources (
                Id INTEGER PRIMARY KEY,
                Path TEXT NOT NULL,
                PathKey TEXT NOT NULL UNIQUE,
                Name TEXT NOT NULL,
                SnapshotUtc TEXT NOT NULL,
                ImportedUtc TEXT NULL,
                ConsolidatedUtc TEXT NULL,
                Files INTEGER NOT NULL DEFAULT 0,
                Bytes INTEGER NOT NULL DEFAULT 0
            );

            -- Jede Datei eines Altbestands; ArchivePath liegt unter "_Altbestand" im Ordner "Dateien".
            CREATE TABLE IF NOT EXISTS LegacyFiles (
                Id INTEGER PRIMARY KEY,
                SourceId INTEGER NOT NULL REFERENCES LegacySources(Id),
                RelativePath TEXT NOT NULL,
                RelativeKey TEXT NOT NULL,
                Size INTEGER NOT NULL,
                LastWriteUtc TEXT NOT NULL,
                Hash TEXT NOT NULL REFERENCES Blobs(Hash),
                ArchivePath TEXT NOT NULL,
                UNIQUE (SourceId, RelativeKey)
            );
            CREATE INDEX IF NOT EXISTS IX_LegacyFiles_Hash ON LegacyFiles(Hash);
            CREATE INDEX IF NOT EXISTS IX_PathHistory_OldPath ON PathHistory(OldPath);
            """);

    private void CreateSchemaV2()
    {

        Execute("""
            CREATE TABLE IF NOT EXISTS Documents (
                Id INTEGER PRIMARY KEY,
                Path TEXT NOT NULL,
                PathKey TEXT NOT NULL,
                FileName TEXT NOT NULL,
                FileId TEXT NULL,
                Size INTEGER NOT NULL,
                LastWriteUtc TEXT NOT NULL,
                Hash TEXT NOT NULL,
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                FirstSeenUtc TEXT NOT NULL,
                DeletedUtc TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Documents_PathKey ON Documents(PathKey);
            CREATE INDEX IF NOT EXISTS IX_Documents_FileId ON Documents(FileId);
            CREATE INDEX IF NOT EXISTS IX_Documents_Hash ON Documents(Hash);

            -- Jeder Inhalt einmal; StoredPath = Datei unter "Dateien", die ihn tatsächlich enthält (weitere Namen sind Hardlinks).
            CREATE TABLE IF NOT EXISTS Blobs (
                Hash TEXT PRIMARY KEY,
                Size INTEGER NOT NULL,
                StoredPath TEXT NOT NULL,
                StoredUtc TEXT NOT NULL
            );

            -- Alle von Fileman unter "Dateien" angelegten Namen (Versionen und aktueller Stand).
            CREATE TABLE IF NOT EXISTS ArchiveFiles (
                PathKey TEXT PRIMARY KEY,
                RelativePath TEXT NOT NULL,
                Hash TEXT NOT NULL REFERENCES Blobs(Hash),
                DocumentId INTEGER NOT NULL REFERENCES Documents(Id),
                IsCurrent INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Runs (
                Id INTEGER PRIMARY KEY,
                StartedUtc TEXT NOT NULL,
                FinishedUtc TEXT NULL,
                Trigger TEXT NOT NULL,
                FilesScanned INTEGER NOT NULL DEFAULT 0,
                Changes INTEGER NOT NULL DEFAULT 0,
                Errors INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS Versions (
                Id INTEGER PRIMARY KEY,
                DocumentId INTEGER NOT NULL REFERENCES Documents(Id),
                Hash TEXT NOT NULL REFERENCES Blobs(Hash),
                Size INTEGER NOT NULL,
                LastWriteUtc TEXT NOT NULL,
                Path TEXT NOT NULL,
                CapturedUtc TEXT NOT NULL,
                Trigger TEXT NOT NULL,
                RunId INTEGER NULL REFERENCES Runs(Id),
                ArchivePath TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Versions_DocumentId ON Versions(DocumentId);

            CREATE TABLE IF NOT EXISTS PathHistory (
                Id INTEGER PRIMARY KEY,
                DocumentId INTEGER NOT NULL REFERENCES Documents(Id),
                OldPath TEXT NOT NULL,
                NewPath TEXT NOT NULL,
                ChangedUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_PathHistory_DocumentId ON PathHistory(DocumentId);

            CREATE TABLE IF NOT EXISTS Events (
                Id INTEGER PRIMARY KEY,
                Utc TEXT NOT NULL,
                DocumentId INTEGER NOT NULL REFERENCES Documents(Id),
                Kind TEXT NOT NULL,
                Path TEXT NOT NULL,
                OldPath TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Events_Utc ON Events(Utc);
            """);
    }

    // ---------- Transaktionen ----------

    public void BeginTransaction() => _transaction ??= _connection.BeginTransaction();

    public void Commit()
    {
        _transaction?.Commit();
        _transaction?.Dispose();
        _transaction = null;
    }

    public void Rollback()
    {
        _transaction?.Rollback();
        _transaction?.Dispose();
        _transaction = null;
    }

    // ---------- Schreiben ----------

    public long InsertDocument(DocumentRecord doc)
    {
        doc.Id = InsertAndGetId("""
            INSERT INTO Documents (Path, PathKey, FileName, FileId, Size, LastWriteUtc, Hash, IsDeleted, FirstSeenUtc, DeletedUtc)
            VALUES ($path, $key, $name, $fileId, $size, $mtime, $hash, $deleted, $first, $deletedUtc)
            """, DocumentParameters(doc));
        return doc.Id;
    }

    public void UpdateDocument(DocumentRecord doc)
    {
        var parameters = DocumentParameters(doc);
        parameters.Add(("$id", doc.Id));
        Execute("""
            UPDATE Documents SET Path=$path, PathKey=$key, FileName=$name, FileId=$fileId, Size=$size, LastWriteUtc=$mtime,
                Hash=$hash, IsDeleted=$deleted, FirstSeenUtc=$first, DeletedUtc=$deletedUtc
            WHERE Id=$id
            """, parameters);
    }

    private static List<(string, object?)> DocumentParameters(DocumentRecord doc) =>
    [
        ("$path", doc.Path),
        ("$key", PathUtil.Key(doc.Path)),
        ("$name", doc.FileName),
        ("$fileId", doc.FileId),
        ("$size", doc.Size),
        ("$mtime", ToDb(doc.LastWriteUtc)),
        ("$hash", doc.Hash),
        ("$deleted", doc.IsDeleted ? 1 : 0),
        ("$first", ToDb(doc.FirstSeenUtc)),
        ("$deletedUtc", doc.DeletedUtc is { } d ? ToDb(d) : null),
    ];

    public void InsertBlob(string hash, long size, string storedPath, DateTime utc) =>
        Execute("INSERT INTO Blobs (Hash, Size, StoredPath, StoredUtc) VALUES ($h, $s, $p, $u)",
            [("$h", hash), ("$s", size), ("$p", storedPath), ("$u", ToDb(utc))]);

    public void UpdateBlobPath(string hash, string storedPath) =>
        Execute("UPDATE Blobs SET StoredPath = $p WHERE Hash = $h", [("$h", hash), ("$p", storedPath)]);

    public string? GetBlobPath(string hash) =>
        ScalarWith("SELECT StoredPath FROM Blobs WHERE Hash = $h", [("$h", hash)]) as string;

    public ArchiveFileRecord? GetArchiveFile(string relativePath) =>
        Query("SELECT RelativePath, Hash, DocumentId, IsCurrent FROM ArchiveFiles WHERE PathKey = $k",
            [("$k", PathUtil.Key(relativePath))], r => new ArchiveFileRecord(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3) != 0)).FirstOrDefault();

    public void InsertArchiveFile(ArchiveFileRecord file) =>
        Execute("INSERT INTO ArchiveFiles (PathKey, RelativePath, Hash, DocumentId, IsCurrent) VALUES ($k, $p, $h, $d, $c)",
            [("$k", PathUtil.Key(file.RelativePath)), ("$p", file.RelativePath), ("$h", file.Hash), ("$d", file.DocumentId), ("$c", file.IsCurrent ? 1 : 0)]);

    public void DeleteArchiveFile(string relativePath) =>
        Execute("DELETE FROM ArchiveFiles WHERE PathKey = $k", [("$k", PathUtil.Key(relativePath))]);

    public long InsertVersion(VersionRecord version, long? runId)
    {
        version.Id = InsertAndGetId("""
            INSERT INTO Versions (DocumentId, Hash, Size, LastWriteUtc, Path, CapturedUtc, Trigger, RunId, ArchivePath)
            VALUES ($doc, $hash, $size, $mtime, $path, $captured, $trigger, $run, $archive)
            """,
            [("$doc", version.DocumentId), ("$hash", version.Hash), ("$size", version.Size), ("$mtime", ToDb(version.LastWriteUtc)),
             ("$path", version.Path), ("$captured", ToDb(version.CapturedUtc)), ("$trigger", version.Trigger.ToString()), ("$run", runId),
             ("$archive", version.ArchivePath)]);
        return version.Id;
    }

    public void InsertPathChange(PathChangeRecord change) =>
        Execute("INSERT INTO PathHistory (DocumentId, OldPath, NewPath, ChangedUtc) VALUES ($d, $o, $n, $u)",
            [("$d", change.DocumentId), ("$o", change.OldPath), ("$n", change.NewPath), ("$u", ToDb(change.ChangedUtc))]);

    public void InsertEvent(ChangeEvent change) =>
        change.Id = InsertAndGetId("INSERT INTO Events (Utc, DocumentId, Kind, Path, OldPath) VALUES ($u, $d, $k, $p, $o)",
            [("$u", ToDb(change.Utc)), ("$d", change.DocumentId), ("$k", change.Kind.ToString()), ("$p", change.Path), ("$o", change.OldPath)]);

    public long StartRun(BackupTrigger trigger, DateTime utc) =>
        InsertAndGetId("INSERT INTO Runs (StartedUtc, Trigger) VALUES ($u, $t)", [("$u", ToDb(utc)), ("$t", trigger.ToString())]);

    public void FinishRun(long runId, DateTime utc, int filesScanned, int changes, int errors) =>
        Execute("UPDATE Runs SET FinishedUtc=$u, FilesScanned=$f, Changes=$c, Errors=$e WHERE Id=$id",
            [("$u", ToDb(utc)), ("$f", filesScanned), ("$c", changes), ("$e", errors), ("$id", runId)]);

    // ---------- Altbestände ----------

    public LegacySourceRecord? GetLegacySource(string path) =>
        Query("SELECT Id, Path, Name, SnapshotUtc, ImportedUtc, ConsolidatedUtc, Files, Bytes FROM LegacySources WHERE PathKey = $k",
            [("$k", PathUtil.Key(path))], ReadLegacySource).FirstOrDefault();

    public List<LegacySourceRecord> GetLegacySources() =>
        Query("SELECT Id, Path, Name, SnapshotUtc, ImportedUtc, ConsolidatedUtc, Files, Bytes FROM LegacySources ORDER BY SnapshotUtc, Id",
            [], ReadLegacySource);

    public long InsertLegacySource(string path, string name, DateTime snapshotUtc) =>
        InsertAndGetId("INSERT INTO LegacySources (Path, PathKey, Name, SnapshotUtc) VALUES ($p, $k, $n, $s)",
            [("$p", path), ("$k", PathUtil.Key(path)), ("$n", name), ("$s", ToDb(snapshotUtc))]);

    public void MarkLegacySourceImported(long id, DateTime snapshotUtc, DateTime utc, int files, long bytes) =>
        Execute("UPDATE LegacySources SET SnapshotUtc=$s, ImportedUtc=$u, Files=$f, Bytes=$b WHERE Id=$id",
            [("$s", ToDb(snapshotUtc)), ("$u", ToDb(utc)), ("$f", files), ("$b", bytes), ("$id", id)]);

    public void MarkLegacySourceConsolidated(long id, DateTime utc) =>
        Execute("UPDATE LegacySources SET ConsolidatedUtc=$u WHERE Id=$id", [("$u", ToDb(utc)), ("$id", id)]);

    public void InsertLegacyFile(LegacyFileRecord file) =>
        Execute("""
            INSERT OR IGNORE INTO LegacyFiles (SourceId, RelativePath, RelativeKey, Size, LastWriteUtc, Hash, ArchivePath)
            VALUES ($s, $p, $k, $size, $t, $h, $a)
            """,
            [("$s", file.SourceId), ("$p", file.RelativePath), ("$k", PathUtil.Key(file.RelativePath)), ("$size", file.Size),
             ("$t", ToDb(file.LastWriteUtc)), ("$h", file.Hash), ("$a", file.ArchivePath)]);

    public List<LegacyFileRecord> GetLegacyFiles(long? sourceId = null) =>
        Query("SELECT SourceId, RelativePath, Size, LastWriteUtc, Hash, ArchivePath FROM LegacyFiles"
              + (sourceId is null ? "" : " WHERE SourceId = $s") + " ORDER BY SourceId, RelativeKey",
            sourceId is null ? [] : [("$s", sourceId.Value)],
            r => new LegacyFileRecord(r.GetInt64(0), r.GetString(1), r.GetInt64(2), FromDb(r.GetString(3)), r.GetString(4), r.GetString(5)));

    private static LegacySourceRecord ReadLegacySource(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), FromDb(r.GetString(3)),
        r.IsDBNull(4) ? null : FromDb(r.GetString(4)), r.IsDBNull(5) ? null : FromDb(r.GetString(5)), r.GetInt32(6), r.GetInt64(7));

    /// <summary>Alle Dokumente (auch gelöschte) – für die Zuordnung von Altbeständen.</summary>
    public List<DocumentRecord> GetAllDocuments() =>
        Query($"SELECT {DocumentColumns} FROM Documents d", [], ReadDocument);

    /// <summary>Alle Versions-Hashes je Dokument.</summary>
    public Dictionary<long, HashSet<string>> GetVersionHashesByDocument()
    {
        var result = new Dictionary<long, HashSet<string>>();
        foreach (var (doc, hash) in Query("SELECT DocumentId, Hash FROM Versions", [], r => (r.GetInt64(0), r.GetString(1))))
        {
            if (!result.TryGetValue(doc, out var set))
                result[doc] = set = [];
            set.Add(hash);
        }
        return result;
    }

    // ---------- Lesen ----------

    private const string DocumentColumns =
        "d.Id, d.Path, d.FileName, d.FileId, d.Size, d.LastWriteUtc, d.Hash, d.IsDeleted, d.FirstSeenUtc, d.DeletedUtc, " +
        "(SELECT COUNT(*) FROM Versions v WHERE v.DocumentId = d.Id)";

    public List<DocumentRecord> GetLiveDocuments() =>
        Query($"SELECT {DocumentColumns} FROM Documents d WHERE d.IsDeleted = 0", [], ReadDocument);

    public List<DocumentRecord> GetDeletedSince(DateTime utc) =>
        Query($"SELECT {DocumentColumns} FROM Documents d WHERE d.IsDeleted = 1 AND d.DeletedUtc >= $u ORDER BY d.DeletedUtc DESC",
            [("$u", ToDb(utc))], ReadDocument);

    public DocumentRecord? GetDocument(long id) =>
        Query($"SELECT {DocumentColumns} FROM Documents d WHERE d.Id = $id", [("$id", id)], ReadDocument).FirstOrDefault();

    public List<DocumentRecord> Find(FindQuery query)
    {
        var conditions = new List<string>();
        var parameters = new List<(string, object?)>();
        var words = (query.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < words.Length; i++)
        {
            conditions.Add($"(fm_contains(d.Path, $w{i}) OR EXISTS (SELECT 1 FROM PathHistory h WHERE h.DocumentId = d.Id AND fm_contains(h.OldPath, $w{i})))");
            parameters.Add(($"$w{i}", words[i]));
        }
        if (!query.IncludeDeleted)
            conditions.Add("d.IsDeleted = 0");
        if (!string.IsNullOrWhiteSpace(query.UnderPath))
        {
            var key = PathUtil.Key(PathUtil.Normalize(query.UnderPath));
            conditions.Add("(d.PathKey = $under OR substr(d.PathKey, 1, length($underSep)) = $underSep)");
            parameters.Add(("$under", key));
            parameters.Add(("$underSep", key.EndsWith(Path.DirectorySeparatorChar) ? key : key + Path.DirectorySeparatorChar));
        }
        if (query.ChangedAfterUtc is { } after)
        {
            conditions.Add("EXISTS (SELECT 1 FROM Events e WHERE e.DocumentId = d.Id AND e.Utc >= $after)");
            parameters.Add(("$after", ToDb(after)));
        }
        if (query.ChangedBeforeUtc is { } before)
        {
            conditions.Add("EXISTS (SELECT 1 FROM Events e WHERE e.DocumentId = d.Id AND e.Utc < $before)");
            parameters.Add(("$before", ToDb(before)));
        }
        var where = conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions);
        parameters.Add(("$limit", query.Limit));
        return Query($"SELECT {DocumentColumns} FROM Documents d {where} ORDER BY d.IsDeleted, d.FileName COLLATE NOCASE LIMIT $limit",
            parameters, ReadDocument);
    }

    public List<VersionRecord> GetVersions(long documentId) =>
        Query("SELECT Id, DocumentId, Hash, Size, LastWriteUtc, Path, CapturedUtc, Trigger, ArchivePath FROM Versions WHERE DocumentId = $d ORDER BY CapturedUtc DESC, Id DESC",
            [("$d", documentId)], ReadVersion);

    public VersionRecord? GetVersion(long versionId) =>
        Query("SELECT Id, DocumentId, Hash, Size, LastWriteUtc, Path, CapturedUtc, Trigger, ArchivePath FROM Versions WHERE Id = $id",
            [("$id", versionId)], ReadVersion).FirstOrDefault();

    public List<PathChangeRecord> GetPathHistory(long documentId) =>
        Query("SELECT DocumentId, OldPath, NewPath, ChangedUtc FROM PathHistory WHERE DocumentId = $d ORDER BY Id",
            [("$d", documentId)], r => new PathChangeRecord
            {
                DocumentId = r.GetInt64(0),
                OldPath = r.GetString(1),
                NewPath = r.GetString(2),
                ChangedUtc = FromDb(r.GetString(3)),
            });

    /// <summary>
    /// Sucht das Dokument, das zuletzt unter <paramref name="path"/> lag: zuerst vorhandene, dann frühere Pfade, dann gelöschte.
    /// </summary>
    public DocumentRecord? FindDocumentByAnyPath(string path)
    {
        var key = PathUtil.Key(path);
        return Query($"SELECT {DocumentColumns} FROM Documents d WHERE d.PathKey = $k AND d.IsDeleted = 0 LIMIT 1", [("$k", key)], ReadDocument).FirstOrDefault()
            ?? Query($"SELECT {DocumentColumns} FROM Documents d JOIN PathHistory h ON h.DocumentId = d.Id WHERE h.OldPath = $p ORDER BY h.Id DESC LIMIT 1",
                [("$p", path)], ReadDocument).FirstOrDefault()
            ?? Query($"SELECT {DocumentColumns} FROM Documents d WHERE d.PathKey = $k ORDER BY d.DeletedUtc DESC LIMIT 1", [("$k", key)], ReadDocument).FirstOrDefault();
    }

    public bool HasVersionWithHash(long documentId, string hash) =>
        Convert.ToInt64(ScalarWith("SELECT COUNT(*) FROM Versions WHERE DocumentId = $d AND Hash = $h", [("$d", documentId), ("$h", hash)])) > 0;

    public List<ChangeEvent> GetRecentEvents(int count) =>
        Query("SELECT Id, Utc, DocumentId, Kind, Path, OldPath FROM Events ORDER BY Id DESC LIMIT $n", [("$n", count)], ReadEvent);

    public List<ChangeEvent> GetEventsForDocument(long documentId) =>
        Query("SELECT Id, Utc, DocumentId, Kind, Path, OldPath FROM Events WHERE DocumentId = $d ORDER BY Id DESC", [("$d", documentId)], ReadEvent);

    public List<(string Hash, string StoredPath)> GetAllBlobs() =>
        Query("SELECT Hash, StoredPath FROM Blobs ORDER BY Hash", [], r => (r.GetString(0), r.GetString(1)));

    public RunRecord? GetLastRun() =>
        Query("SELECT Id, StartedUtc, FinishedUtc, Trigger, FilesScanned, Changes, Errors FROM Runs WHERE FinishedUtc IS NOT NULL ORDER BY Id DESC LIMIT 1",
            [], r => new RunRecord
            {
                Id = r.GetInt64(0),
                StartedUtc = FromDb(r.GetString(1)),
                FinishedUtc = r.IsDBNull(2) ? null : FromDb(r.GetString(2)),
                Trigger = Enum.Parse<BackupTrigger>(r.GetString(3)),
                FilesScanned = r.GetInt32(4),
                Changes = r.GetInt32(5),
                Errors = r.GetInt32(6),
            }).FirstOrDefault();

    public CatalogStatistics GetStatistics() =>
        Query("""
            SELECT (SELECT COUNT(*) FROM Documents WHERE IsDeleted = 0),
                   (SELECT COUNT(*) FROM Documents WHERE IsDeleted = 1),
                   (SELECT COUNT(*) FROM Versions),
                   (SELECT COUNT(*) FROM Blobs),
                   (SELECT IFNULL(SUM(Size), 0) FROM Versions),
                   (SELECT IFNULL(SUM(Size), 0) FROM Blobs)
            """, [], r => new CatalogStatistics
        {
            LiveDocuments = r.GetInt32(0),
            DeletedDocuments = r.GetInt32(1),
            Versions = r.GetInt32(2),
            Blobs = r.GetInt32(3),
            OriginalBytes = r.GetInt64(4),
            StoredBytes = r.GetInt64(5),
        }).Single();

    /// <summary>Schreibt eine konsistente Kopie des Katalogs (z. B. die lokale Katalog-Kopie).</summary>
    public void BackupTo(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var temp = file + ".tmp";
        if (File.Exists(temp))
            File.Delete(temp);
        using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Pooling = false }.ToString()))
        {
            target.Open();
            _connection.BackupDatabase(target);
        }
        File.Move(temp, file, overwrite: true);
    }

    public void Dispose()
    {
        Rollback();
        _connection.Dispose();
    }

    // ---------- Hilfsfunktionen ----------

    private static DocumentRecord ReadDocument(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Path = r.GetString(1),
        FileName = r.GetString(2),
        FileId = r.IsDBNull(3) ? null : r.GetString(3),
        Size = r.GetInt64(4),
        LastWriteUtc = FromDb(r.GetString(5)),
        Hash = r.GetString(6),
        IsDeleted = r.GetInt64(7) != 0,
        FirstSeenUtc = FromDb(r.GetString(8)),
        DeletedUtc = r.IsDBNull(9) ? null : FromDb(r.GetString(9)),
        VersionCount = r.GetInt32(10),
    };

    private static VersionRecord ReadVersion(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        DocumentId = r.GetInt64(1),
        Hash = r.GetString(2),
        Size = r.GetInt64(3),
        LastWriteUtc = FromDb(r.GetString(4)),
        Path = r.GetString(5),
        CapturedUtc = FromDb(r.GetString(6)),
        Trigger = Enum.Parse<BackupTrigger>(r.GetString(7)),
        ArchivePath = r.GetString(8),
    };

    private static ChangeEvent ReadEvent(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Utc = FromDb(r.GetString(1)),
        DocumentId = r.GetInt64(2),
        Kind = Enum.Parse<ChangeKind>(r.GetString(3)),
        Path = r.GetString(4),
        OldPath = r.IsDBNull(5) ? null : r.GetString(5),
    };

    internal static string ToDb(DateTime value) =>
        DateTime.SpecifyKind(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value, DateTimeKind.Utc)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    internal static DateTime FromDb(string value) =>
        DateTime.ParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private SqliteCommand CreateCommand(string sql, IEnumerable<(string Name, object? Value)> parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _transaction;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private void Execute(string sql, IEnumerable<(string, object?)>? parameters = null)
    {
        using var command = CreateCommand(sql, parameters ?? []);
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql) => ScalarWith(sql, []);

    private object? ScalarWith(string sql, IEnumerable<(string, object?)> parameters)
    {
        using var command = CreateCommand(sql, parameters);
        return command.ExecuteScalar();
    }

    private long InsertAndGetId(string sql, IEnumerable<(string, object?)> parameters)
    {
        using var command = CreateCommand(sql + "; SELECT last_insert_rowid();", parameters);
        return (long)command.ExecuteScalar()!;
    }

    private List<T> Query<T>(string sql, IEnumerable<(string, object?)> parameters, Func<SqliteDataReader, T> read)
    {
        using var command = CreateCommand(sql, parameters);
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read())
            result.Add(read(reader));
        return result;
    }
}
