using Fileman.Core.Archive;
using Fileman.Core.Automation;
using Fileman.Core.Backup;
using Fileman.Core.Catalog;
using Fileman.Core.Configuration;
using Fileman.Core.Identity;
using Fileman.Core.Protection;
using Fileman.Core.Scanning;

namespace Fileman.Core;

/// <summary>
/// Einstiegspunkt für Oberflächen (CLI, Tray-Anwendung): Einstellungen laden, Sicherungsplatte finden,
/// Sicherung ausführen, Katalog zum Suchen öffnen.
/// </summary>
public sealed class FilemanWorkspace
{
    private readonly IFileIdentityProvider _identity;
    private readonly TimeProvider _time;
    private readonly Func<IEnumerable<string>>? _driveRoots;

    public FilemanWorkspace(AppPaths paths, IFileIdentityProvider? identity = null, TimeProvider? time = null,
        Func<IEnumerable<string>>? driveRoots = null)
    {
        Paths = paths;
        _identity = identity ?? FileIdentityProvider.ForCurrentPlatform();
        _time = time ?? TimeProvider.System;
        _driveRoots = driveRoots;
        Settings = SettingsStore.Load(paths.SettingsFile);
        Spool = new Spool(paths.SpoolDirectory);
    }

    /// <summary>Lokaler Zwischenspeicher für Zeiten ohne Sicherungsplatte.</summary>
    public Spool Spool { get; }

    public TimeProvider Time => _time;

    public ExclusionFilter CreateExclusionFilter() => new(Settings.ExcludePatterns);

    /// <summary>Wird nur ein Laufwerk angegeben (z. B. "E:\"), kommt das Archiv in den Unterordner "FilemanArchiv".</summary>
    public static string ResolveTargetDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        return string.Equals(PathUtil.Normalize(full), PathUtil.Normalize(Path.GetPathRoot(full)!), StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(full, BackupArchive.DefaultFolderName)
            : full;
    }

    public AppPaths Paths { get; }

    public FilemanSettings Settings { get; private set; }

    public void SaveSettings() => SettingsStore.Save(Settings, Paths.SettingsFile);

    public void ReloadSettings() => Settings = SettingsStore.Load(Paths.SettingsFile);

    /// <summary>Richtet einen Ordner (typisch: auf der USB-SSD) als Sicherungsziel ein und speichert die Einstellung.</summary>
    public TargetMarker InitializeTarget(string directory, string? name = null)
    {
        var full = PathUtil.Normalize(directory);
        if (Settings.Roots.Any(r => PathUtil.IsUnder(full, r.Path) || PathUtil.IsUnder(r.Path, full)))
            throw new FilemanException("Das Sicherungsziel darf nicht in einem überwachten Quellordner liegen.");
        var marker = BackupArchive.Initialize(full, name ?? "Fileman-Sicherung");
        Settings.Target = new TargetSettings { Id = marker.Id, LastKnownPath = full };
        SaveSettings();
        return marker;
    }

    /// <summary>Sucht die Sicherungsplatte. Hat sich der Laufwerksbuchstabe geändert, wird die Einstellung angepasst.</summary>
    public string? LocateArchive()
    {
        if (Settings.Target is not { } target)
            return null;
        var found = TargetLocator.Locate(target, _driveRoots);
        if (found is not null && !string.Equals(found, target.LastKnownPath, PathUtil.Comparison))
        {
            target.LastKnownPath = found;
            // Nur den Pfad nachtragen: frisch von der Platte lesen, damit gleichzeitig gespeicherte Änderungen
            // (z. B. aus dem Einstellungsfenster) nicht überschrieben werden.
            var current = SettingsStore.Load(Paths.SettingsFile);
            if (current.Target?.Id == target.Id)
            {
                current.Target.LastKnownPath = found;
                SettingsStore.Save(current, Paths.SettingsFile);
            }
        }
        return found;
    }

    public BackupArchive OpenArchive(bool readOnly = false)
    {
        if (Settings.Target is null)
            throw new FilemanException("Es ist noch keine Sicherungsplatte eingerichtet.");
        var directory = LocateArchive() ?? throw new TargetNotConnectedException();
        return BackupArchive.Open(directory, readOnly);
    }

    /// <summary>
    /// Führt eine Sicherung aus und aktualisiert danach die lokale Katalog-Kopie. Bei einem vollständigen Lauf
    /// (ohne <paramref name="scopes"/>) werden anschließend auch die Zwischenstände aus dem Zwischenspeicher übernommen.
    /// </summary>
    /// <param name="guard">Schutz vor Massenänderungen; ohne Angabe ein neuer nach den Einstellungen.</param>
    public async Task<BackupResult> RunBackupAsync(BackupTrigger trigger, IReadOnlyList<ScanScope>? scopes = null,
        IProgress<BackupProgress>? progress = null, CancellationToken cancellationToken = default, MassChangeGuard? guard = null)
    {
        if (Settings.Roots.Count == 0)
            throw new FilemanException("Es sind noch keine Quellordner eingerichtet.");
        using var archive = OpenArchive();
        var engine = new BackupEngine(archive, _identity, CreateExclusionFilter(), _time);
        var result = await engine.RunAsync(new BackupOptions
        {
            Roots = Settings.Roots.Select(r => r.Path).ToList(),
            Scopes = scopes,
            Trigger = trigger,
            ScanParallelism = Settings.ScanThreads,
            FileParallelism = Settings.FileThreads,
            Guard = guard ?? new MassChangeGuard(Settings.MassChange, _time),
        }, progress, cancellationToken).ConfigureAwait(false);
        if (scopes is null)
            result.ImportedFromSpool = Spool.ImportInto(archive, _time.GetUtcNow().UtcDateTime);
        RefreshCatalogCache(archive);
        return result;
    }

    // ---------- Altbestände ----------

    /// <summary>Liest alle eingetragenen, noch nicht übernommenen Altbestände „as is“ ins Archiv ein.</summary>
    public async Task<Legacy.LegacyImportResult> ImportLegacyAsync(IProgress<Legacy.LegacyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var archive = await OpenArchiveWaitingAsync(cancellationToken).ConfigureAwait(false);
        var result = await new Legacy.LegacyImporter(archive, _time)
            .ImportAsync(Settings.LegacyFolders.Select(l => l.Path).ToList(), progress, cancellationToken, Settings.FileThreads)
            .ConfigureAwait(false);
        RefreshCatalogCache(archive);
        return result;
    }

    /// <summary>Findet in einem Oberordner (z. B. "E:\Sicherungen") die Ordner, die alte Sicherungen sind.</summary>
    public IReadOnlyList<Legacy.LegacyCandidate> DetectLegacyFolders(string parent, CancellationToken cancellationToken = default) =>
        Legacy.LegacyDetector.Detect(parent,
            Settings.Roots.Select(r => r.Path).ToList(),
            Settings.Target is { LastKnownPath.Length: > 0 } target ? [target.LastKnownPath] : [],
            Settings.LegacyFolders.Select(l => l.Path).ToList(),
            cancellationToken);

    /// <summary>Schlägt vor, welche Ordner der Altbestände welchen gesicherten Quellordnern entsprechen.</summary>
    public Legacy.LegacyProposal ProposeLegacyMapping()
    {
        using var archive = OpenArchive(readOnly: true);
        return Legacy.LegacyMapper.Propose(archive.Catalog, Settings.Roots.Select(r => r.Path).ToList());
    }

    /// <summary>Übernimmt die bestätigten Zuordnungen in die Versionsgeschichte.</summary>
    public async Task<Legacy.LegacyConsolidationResult> ConsolidateLegacyAsync(IReadOnlyList<Legacy.LegacyMapping> accepted,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        using var archive = await OpenArchiveWaitingAsync(cancellationToken).ConfigureAwait(false);
        var result = await Task.Run(() => new Legacy.LegacyConsolidator(archive, _time).Consolidate(accepted, progress, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        RefreshCatalogCache(archive);
        return result;
    }

    /// <summary>Öffnet das Archiv zum Schreiben; läuft gerade eine Sicherung, wird bis zu einer Minute gewartet.</summary>
    private async Task<BackupArchive> OpenArchiveWaitingAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return OpenArchive();
            }
            catch (ArchiveBusyException) when (attempt < 60)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Öffnet den Katalog zum Lesen: von der Platte, wenn angesteckt, sonst die lokale Kopie.
    /// </summary>
    public CatalogView OpenCatalogForReading()
    {
        if (Settings.Target is not null && LocateArchive() is { } directory)
        {
            var archive = BackupArchive.Open(directory, readOnly: true);
            return new CatalogView(archive.Catalog, archive, IsCache: false);
        }
        if (File.Exists(Paths.CatalogCacheFile))
            return new CatalogView(CatalogDatabase.Open(Paths.CatalogCacheFile, readOnly: true), null, IsCache: true);
        throw new TargetNotConnectedException();
    }

    private void RefreshCatalogCache(BackupArchive archive)
    {
        try
        {
            archive.Catalog.BackupTo(Paths.CatalogCacheFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            // Die Kopie ist nur eine Bequemlichkeit; die Sicherung selbst ist bereits abgeschlossen.
        }
    }
}

/// <summary>Ein zum Lesen geöffneter Katalog; <see cref="Archive"/> ist null, wenn nur die lokale Kopie verfügbar ist.</summary>
public sealed record CatalogView(CatalogDatabase Catalog, BackupArchive? Archive, bool IsCache) : IDisposable
{
    public void Dispose()
    {
        if (Archive is not null)
            Archive.Dispose();
        else
            Catalog.Dispose();
    }
}

public sealed class TargetNotConnectedException()
    : FilemanException("Die Sicherungsplatte ist nicht angesteckt.");
