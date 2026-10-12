using System.Collections.Concurrent;
using Fileman.Core;
using Fileman.Core.Archive;
using Fileman.Core.Automation;
using Fileman.Core.Backup;
using Fileman.Core.Catalog;
using Fileman.Core.Configuration;

namespace Fileman.Tests;

public class BackupServiceTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly TestEnvironment _env = new();
    private readonly ConcurrentQueue<BackupResult> _results = new();
    private FilemanWorkspace _workspace = null!;
    private BackupService _service = null!;

    private string SsdDir => Path.Combine(_env.Base, "SSD");

    public Task InitializeAsync()
    {
        _workspace = new FilemanWorkspace(new AppPaths(Path.Combine(_env.Base, "home")), _env.Identity, driveRoots: () => []);
        _workspace.Settings.Target = new TargetSettings { Id = TargetMarker.TryRead(_env.ArchiveDir)!.Id, LastKnownPath = _env.ArchiveDir };
        SettingsStore.AddRoot(_workspace.Settings, _env.RootA, "A");
        SettingsStore.AddRoot(_workspace.Settings, _env.RootB, "B");
        _workspace.SaveSettings();
        _service = new BackupService(_workspace, new BackupServiceOptions
        {
            Debounce = TimeSpan.FromMilliseconds(300),
            PollInterval = TimeSpan.FromMilliseconds(100),
            ReconcileInterval = TimeSpan.FromHours(1),
            RetryDelay = TimeSpan.FromMilliseconds(200),
        });
        _service.BackupCompleted += (_, r) => _results.Enqueue(r);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        _env.Dispose();
    }

    private async Task<BackupResult> NextResult(Func<BackupResult, bool>? match = null)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_results.TryDequeue(out var result) && (match is null || match(result)))
                return result;
            await Task.Delay(50);
        }
        throw new TimeoutException("Keine passende Sicherung abgeschlossen.");
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Zeitüberschreitung: " + what);
            await Task.Delay(50);
        }
    }

    private async Task StartAndWaitForInitialReconcile()
    {
        _service.Start();
        var initial = await NextResult();
        Assert.Equal(BackupTrigger.Reconcile, initial.Trigger);
    }

    [Fact]
    public async Task NewFile_IsBackedUpAutomatically_AfterQuietPeriod()
    {
        await StartAndWaitForInitialReconcile();

        _env.Write(Path.Combine(_env.RootA, "Mandant", "Neu.docx"), "neu");

        var result = await NextResult(r => r.TotalChanges > 0);
        Assert.Equal(BackupTrigger.Watcher, result.Trigger);
        Assert.Equal(1, result.Created);
        Assert.Equal(ServiceStatus.Idle, (await WaitForStatus(ServiceStatus.Idle)).Status);
    }

    [Fact]
    public async Task RenamedFolder_IsRecognizedAsMove()
    {
        for (var i = 0; i < 3; i++)
            _env.Write(Path.Combine(_env.RootA, "Huber", $"Beleg {i}.pdf"), "beleg " + i);
        await StartAndWaitForInitialReconcile();

        Directory.Move(Path.Combine(_env.RootA, "Huber"), Path.Combine(_env.RootA, "Huber GmbH"));

        var result = await NextResult(r => r.TotalChanges > 0);
        Assert.Equal(3, result.Moved);
        Assert.Equal(0, result.Created + result.Deleted);
    }

    [Fact]
    public async Task WithoutDisk_ChangesAreSpooled_AndImportedWhenDiskReturns()
    {
        var file = _env.Write(Path.Combine(_env.RootA, "Bilanz.xlsx"), "Stand 1");
        await StartAndWaitForInitialReconcile();

        Directory.Move(SsdDir, SsdDir + "-abgezogen");
        await WaitForStatus(ServiceStatus.TargetMissing);

        File.WriteAllText(file, "Stand 2 (unterwegs)");
        await WaitUntil(() => _workspace.Spool.Count == 1, "erster Zwischenstand");
        File.WriteAllText(file, "Stand 3 (aktuell)");
        await WaitUntil(() => _workspace.Spool.Count == 2, "zweiter Zwischenstand");

        Directory.Move(SsdDir + "-abgezogen", SsdDir);
        var result = await NextResult(r => r.Trigger == BackupTrigger.Reconcile);

        Assert.Equal(1, result.Modified);           // Stand 3 durch den Abgleich
        Assert.Equal(1, result.ImportedFromSpool);  // Stand 2 aus dem Zwischenspeicher (Stand 3 nicht doppelt)
        Assert.Equal(0, _workspace.Spool.Count);
        var versions = _env.WithCatalog(c => c.GetVersions(_env.SingleDocument("Bilanz").Id));
        Assert.Equal(3, versions.Count);
        using var archive = BackupArchive.Open(_env.ArchiveDir, readOnly: true);
        using var reader = new StreamReader(archive.Store.OpenRead(versions[1].ArchivePath));
        Assert.Equal("Stand 2 (unterwegs)", reader.ReadToEnd());
    }

    [Fact]
    public async Task BackupNow_WithoutDisk_ReportsTargetNotConnected()
    {
        Directory.Move(SsdDir, SsdDir + "-abgezogen");
        _service.Start();

        await Assert.ThrowsAsync<TargetNotConnectedException>(() => _service.BackupNowAsync());
    }

    [Fact]
    public async Task BackupNow_RunsFullBackup_EvenWhenPaused()
    {
        await StartAndWaitForInitialReconcile();
        _service.Pause();
        _env.Write(Path.Combine(_env.RootB, "Notiz.txt"), "x");
        await Task.Delay(800);
        Assert.True(_results.IsEmpty); // pausiert: keine automatische Sicherung

        var result = await _service.BackupNowAsync().WaitAsync(Timeout);

        Assert.Equal(BackupTrigger.Manual, result.Trigger);
        Assert.Equal(1, result.Created);
    }

    [Fact]
    public void BuildScopes_DropsPathsInsideOtherScopes()
    {
        var a = Path.Combine(_env.RootA, "x");
        var scopes = BackupService.BuildScopes([Path.Combine(a, "1.txt"), a, Path.Combine(a, "y", "2.txt"), Path.Combine(_env.RootB, "3.txt")]);

        Assert.Equal(2, scopes.Count);
        Assert.Contains(scopes, s => s.Path == a);
    }

    private async Task<ServiceState> WaitForStatus(ServiceStatus status)
    {
        await WaitUntil(() => _service.State.Status == status, $"Status {status}");
        return _service.State;
    }
}

public class SpoolTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public void FileCreatedAndDeletedWhileDiskWasMissing_IsKeptAsDeletedDocument()
    {
        var spool = new Spool(Path.Combine(_env.Base, "spool"));
        var file = _env.Write(Path.Combine(_env.RootA, "Kurzlebig.txt"), "nur kurz da");
        spool.Add(file, DateTime.UtcNow);
        File.Delete(file);

        using (var archive = BackupArchive.Open(_env.ArchiveDir))
            Assert.Equal(1, spool.ImportInto(archive, DateTime.UtcNow));

        var doc = _env.SingleDocument("Kurzlebig");
        Assert.True(doc.IsDeleted);
        Assert.Equal(1, doc.VersionCount);
        Assert.Equal(0, spool.Count);
    }
}
