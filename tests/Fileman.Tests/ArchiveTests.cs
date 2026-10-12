using Fileman.Core;
using Fileman.Core.Archive;
using Fileman.Core.Configuration;
using Fileman.Core.Restore;

namespace Fileman.Tests;

public class ArchiveTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public void Restore_NeverOverwritesExistingFile()
    {
        var file = _env.Write(Path.Combine(_env.RootA, "Brief.txt"), "alt");
        _env.Backup();
        _env.Write(file, "aktuell", DateTime.UtcNow.AddMinutes(1));

        using var archive = BackupArchive.Open(_env.ArchiveDir, readOnly: true);
        var version = archive.Catalog.GetVersions(_env.SingleDocument("Brief").Id).Single();
        var restored = new RestoreService(archive).Restore(version.Id);

        Assert.NotEqual(file, restored);
        Assert.Contains("(Stand ", restored);
        Assert.Equal("aktuell", File.ReadAllText(file));
        Assert.Equal("alt", File.ReadAllText(restored));
    }

    [Fact]
    public void Verify_DetectsDamagedContent()
    {
        _env.Write(Path.Combine(_env.RootA, "a.txt"), "aaa");
        _env.Write(Path.Combine(_env.RootA, "b.txt"), "bbb");
        _env.Backup();
        using var archive = BackupArchive.Open(_env.ArchiveDir, readOnly: true);
        var hash = archive.Catalog.GetAllBlobHashes()[0];
        var blob = archive.Blobs.GetPath(hash);
        File.SetAttributes(blob, FileAttributes.Normal);
        File.WriteAllBytes(blob, [1, 2, 3]);

        var result = ArchiveVerifier.Verify(archive);

        Assert.Equal(2, result.Checked);
        Assert.Equal(hash, Assert.Single(result.DamagedOrMissing));
    }

    [Fact]
    public void Locator_FindsArchiveOnDifferentDriveLetter()
    {
        // Die Platte war zuletzt "Laufwerk E", steckt jetzt aber als "Laufwerk SSD".
        var marker = TargetMarker.TryRead(_env.ArchiveDir)!;
        var oldDrive = Path.Combine(_env.Base, "E") + Path.DirectorySeparatorChar;
        var newDrive = Path.Combine(_env.Base, "SSD") + Path.DirectorySeparatorChar;
        var target = new TargetSettings { Id = marker.Id, LastKnownPath = Path.Combine(oldDrive, BackupArchive.DefaultFolderName) };

        Assert.Equal(_env.ArchiveDir, TargetLocator.Locate(target, () => [oldDrive, newDrive]));
        Assert.Null(TargetLocator.Locate(target, () => [oldDrive]));
    }

    [Fact]
    public void Locator_IgnoresArchiveWithDifferentId()
    {
        var other = new TargetSettings { Id = Guid.NewGuid(), LastKnownPath = _env.ArchiveDir };
        Assert.Null(TargetLocator.Locate(other, () => []));
    }

    [Fact]
    public void Settings_RejectOverlappingRoots()
    {
        var settings = new FilemanSettings();
        SettingsStore.AddRoot(settings, _env.RootA, "A");
        var nested = Directory.CreateDirectory(Path.Combine(_env.RootA, "Unterordner")).FullName;

        Assert.Throws<FilemanException>(() => SettingsStore.AddRoot(settings, nested, null));
        SettingsStore.AddRoot(settings, _env.RootB, "B");
        Assert.Equal(2, settings.Roots.Count);
    }

    [Fact]
    public async Task Workspace_BackupUpdatesLocalCatalogCopy_UsedWhenDiskIsUnplugged()
    {
        var home = Path.Combine(_env.Base, "home");
        var workspace = new FilemanWorkspace(new AppPaths(home), _env.Identity, _env.Time, () => []);
        workspace.Settings.Target = new TargetSettings { Id = TargetMarker.TryRead(_env.ArchiveDir)!.Id, LastKnownPath = _env.ArchiveDir };
        SettingsStore.AddRoot(workspace.Settings, _env.RootA, "Mandanten");
        workspace.SaveSettings();
        _env.Write(Path.Combine(_env.RootA, "Akte.txt"), "akte");

        var result = await workspace.RunBackupAsync(Core.Catalog.BackupTrigger.Manual);
        Assert.Equal(1, result.Created);

        // Platte "abziehen"
        Directory.Move(Path.Combine(_env.Base, "SSD"), Path.Combine(_env.Base, "SSD-weg"));
        Assert.Null(workspace.LocateArchive());
        using var view = workspace.OpenCatalogForReading();
        Assert.True(view.IsCache);
        Assert.Single(view.Catalog.Find(new Core.Catalog.FindQuery { Text = "Akte" }));
        await Assert.ThrowsAsync<TargetNotConnectedException>(() => workspace.RunBackupAsync(Core.Catalog.BackupTrigger.Manual));
    }
}
