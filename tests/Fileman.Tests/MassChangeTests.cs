using System.IO.Compression;
using Fileman.Core;
using Fileman.Core.Archive;
using Fileman.Core.Automation;
using Fileman.Core.Backup;
using Fileman.Core.Catalog;
using Fileman.Core.Configuration;
using Fileman.Core.Protection;
using Fileman.Core.Storage;

namespace Fileman.Tests;

public class ContentSignatureTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fileman-sig", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void ValidFilesPass_EncryptedLookingFilesFail()
    {
        var random = new byte[2048];
        new Random(42).NextBytes(random);
        random[0] = 0x13; // sicher keine gültige Signatur

        Assert.False(ContentSignature.LooksWrong("a.docx", Write("a.docx", MassChangeTests.Docx("x"))));
        Assert.False(ContentSignature.LooksWrong("a.pdf", Write("a.pdf", "%PDF-1.7\n..."u8.ToArray())));
        Assert.False(ContentSignature.LooksWrong("a.xls", Write("a.xls", "<html><table>"u8.ToArray()))); // HTML-Export
        Assert.True(ContentSignature.LooksWrong("b.docx", Write("b.docx", random)));
        Assert.True(ContentSignature.LooksWrong("b.pdf", Write("b.pdf", random)));
        Assert.False(ContentSignature.LooksWrong("b.txt", Write("b.txt", random))); // unbekanntes Format: keine Aussage
        Assert.False(ContentSignature.LooksWrong("leer.docx", Write("leer.docx", [])));
    }
}

public class MassChangeTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    internal static byte[] Docx(string text)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open()))
            writer.Write(text);
        return memory.ToArray();
    }

    private MassChangeGuard Guard(int minFiles = 20, int minPercent = 10, int suspicious = 10) =>
        new(new MassChangeSettings { MinFiles = minFiles, MinPercent = minPercent, SuspiciousContentMinFiles = suspicious }, _env.Time);

    private List<string> CreateFiles(int count, string folder = "Akten", string extension = ".txt")
    {
        var files = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(_env.RootA, folder, $"Datei {i:000}{extension}");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (extension == ".docx")
                File.WriteAllBytes(path, Docx("Inhalt " + i));
            else
                File.WriteAllText(path, "Inhalt " + i);
            File.SetLastWriteTimeUtc(path, _env.Time.Now.UtcDateTime.AddDays(-1));
            files.Add(path);
        }
        return files;
    }

    private static void Modify(IEnumerable<string> files, Func<int, byte[]>? content = null)
    {
        var i = 0;
        foreach (var file in files)
        {
            File.WriteAllBytes(file, content?.Invoke(i) ?? "geändert"u8.ToArray());
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
            i++;
        }
    }

    [Fact]
    public void ManyModifications_StopBeforeAnythingIsWritten()
    {
        var files = CreateFiles(30);
        _env.Backup(guard: Guard());
        Modify(files.Take(25));

        var ex = Assert.Throws<MassChangeException>(() => _env.Backup(guard: Guard()));

        Assert.Equal(25, ex.Report.ChangedFiles);
        Assert.NotEmpty(ex.Report.Examples);
        Assert.Equal(30, _env.WithCatalog(c => c.GetStatistics()).Versions);
        var current = Path.Combine(_env.ArchiveDir, ArchiveStore.FilesFolderName, ArchiveStore.MirrorPath(files[0]));
        Assert.Equal("Inhalt 0", File.ReadAllText(current));
    }

    [Fact]
    public void ManyDeletions_Stop()
    {
        var files = CreateFiles(30);
        _env.Backup(guard: Guard());
        foreach (var file in files.Take(22))
            File.Delete(file);

        var ex = Assert.Throws<MassChangeException>(() => _env.Backup(guard: Guard()));

        Assert.Equal(22, ex.Report.DeletedFiles);
        Assert.Equal(0, _env.WithCatalog(c => c.GetStatistics()).DeletedDocuments);
    }

    [Fact]
    public void FullRun_UsesShareOfAllFiles()
    {
        var files = CreateFiles(100);
        _env.Backup(guard: Guard());
        Modify(files.Take(25));

        // 25 von 100 Dateien, Grenze = max(20 Dateien, 30 %) = 30 → normal sichern
        var result = _env.Backup(guard: Guard(minFiles: 20, minPercent: 30));

        Assert.Equal(25, result.Modified);
    }

    [Fact]
    public void MovingAFolderWithManyFiles_IsNotSuspicious()
    {
        CreateFiles(40, "Laufend");
        _env.Backup(guard: Guard());
        Directory.Move(Path.Combine(_env.RootA, "Laufend"), Path.Combine(_env.RootA, "Archiv 2025"));

        var result = _env.Backup(guard: Guard());

        Assert.Equal(40, result.Moved);
    }

    [Fact]
    public void SlowAttackOverSeveralAutomaticRuns_IsDetected()
    {
        var files = CreateFiles(60);
        var guard = Guard();
        _env.Backup(guard: guard);
        var scope = new[] { new ScanScope(Path.Combine(_env.RootA, "Akten")) };

        Modify(files.Take(12));
        Assert.Equal(12, _env.Backup(scope, BackupTrigger.Watcher, guard).Modified);
        Modify(files.Skip(12).Take(12));

        Assert.Throws<MassChangeException>(() => _env.Backup(scope, BackupTrigger.Watcher, guard));
    }

    [Fact]
    public void EncryptedLookingContent_IsDetected_AndNotStored()
    {
        var files = CreateFiles(30, extension: ".docx");
        _env.Backup(guard: Guard(minFiles: 1000));
        var random = new Random(7);
        Modify(files.Take(15), _ => { var b = new byte[4096]; random.NextBytes(b); b[0] = 0x13; return b; });
        Modify(files.Skip(28), i => Docx("legitim geändert " + i)); // zwei gültige Änderungen

        var ex = Assert.Throws<MassChangeException>(() => _env.Backup(guard: Guard(minFiles: 1000)));

        Assert.True(ex.Report.SuspiciousContentFiles >= 10);
        var docs = _env.WithCatalog(c => c.Find(new FindQuery { Text = "Datei" }));
        Assert.All(docs.Where(d => d.FileName.CompareTo("Datei 015") < 0), d => Assert.Equal(1, d.VersionCount));
        var mirror = Path.Combine(_env.ArchiveDir, ArchiveStore.FilesFolderName, ArchiveStore.MirrorPath(files[0]));
        Assert.Equal(Docx("Inhalt 0"), File.ReadAllBytes(mirror)); // aktueller Stand im Archiv nicht ersetzt
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_env.ArchiveDir, "tmp")));
    }

    [Fact]
    public void ConfirmedChanges_AreBackedUpOnce_ThenGuardIsActiveAgain()
    {
        var files = CreateFiles(30);
        var guard = Guard();
        _env.Backup(guard: guard);
        Modify(files.Take(25));
        Assert.Throws<MassChangeException>(() => _env.Backup(guard: guard));

        guard.BypassNextRun = true;
        var result = _env.Backup(guard: guard);

        Assert.Equal(25, result.Modified);
        Assert.False(guard.BypassNextRun);
        Modify(files.Take(25), _ => "noch einmal"u8.ToArray());
        Assert.Throws<MassChangeException>(() => _env.Backup(guard: guard));
    }
}

public class MassChangeServiceTests : IAsyncLifetime
{
    private readonly TestEnvironment _env = new();
    private FilemanWorkspace _workspace = null!;
    private BackupService _service = null!;
    private readonly List<string> _files = [];

    public Task InitializeAsync()
    {
        for (var i = 0; i < 30; i++)
            _files.Add(_env.Write(Path.Combine(_env.RootA, $"Akte {i}.txt"), "inhalt " + i));
        _workspace = CreateWorkspace();
        _service = CreateService();
        return Task.CompletedTask;
    }

    private FilemanWorkspace CreateWorkspace()
    {
        var workspace = new FilemanWorkspace(new AppPaths(Path.Combine(_env.Base, "home")), _env.Identity, driveRoots: () => []);
        workspace.Settings.Target = new TargetSettings { Id = TargetMarker.TryRead(_env.ArchiveDir)!.Id, LastKnownPath = _env.ArchiveDir };
        if (workspace.Settings.Roots.Count == 0)
            SettingsStore.AddRoot(workspace.Settings, _env.RootA, "A");
        workspace.Settings.MassChange = new MassChangeSettings { MinFiles = 20 };
        workspace.SaveSettings();
        return workspace;
    }

    private BackupService CreateService() => new(_workspace, new BackupServiceOptions
    {
        Debounce = TimeSpan.FromMilliseconds(300),
        PollInterval = TimeSpan.FromMilliseconds(100),
        ReconcileInterval = TimeSpan.FromHours(1),
    });

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        _env.Dispose();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException();
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Alarm_StopsAutomation_SurvivesRestart_AndCanBeConfirmed()
    {
        MassChangeReport? detected = null;
        _service.MassChangeDetected += (_, r) => detected = r;
        _service.Start();
        await WaitUntil(() => _service.State is { Status: ServiceStatus.Idle, LastBackupUtc: not null });

        foreach (var file in _files.Take(25))
            File.AppendAllText(file, " verschlüsselt");
        await WaitUntil(() => _service.State.Status == ServiceStatus.MassChange);

        Assert.NotNull(detected);
        Assert.True(File.Exists(_workspace.Paths.MassChangeAlarmFile));
        await Assert.ThrowsAsync<MassChangeException>(() => _service.BackupNowAsync());

        // Neustart: der Alarm bleibt bestehen
        await _service.DisposeAsync();
        _service = CreateService();
        Assert.NotNull(_service.Alarm);
        _service.Start();
        await WaitUntil(() => _service.State.Status == ServiceStatus.MassChange);

        BackupResult? completed = null;
        _service.BackupCompleted += (_, r) => completed = r;
        _service.ConfirmMassChange();
        await WaitUntil(() => completed is not null);

        Assert.Equal(25, completed!.Modified);
        Assert.False(File.Exists(_workspace.Paths.MassChangeAlarmFile));
        Assert.Null(_service.Alarm);
    }
}
