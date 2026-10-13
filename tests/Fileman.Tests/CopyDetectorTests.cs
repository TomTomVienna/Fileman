using Fileman.Core;
using Fileman.Core.Archive;
using Fileman.Core.Catalog;
using Fileman.Core.Configuration;
using Fileman.Core.Copies;

namespace Fileman.Tests;

public class CopyDetectorTests : IDisposable
{
    private static readonly DateTime Old = new(2022, 3, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime New = new(2025, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly TestEnvironment _env = new();
    private readonly FilemanWorkspace _workspace;

    public CopyDetectorTests()
    {
        _workspace = new FilemanWorkspace(new AppPaths(Path.Combine(_env.Base, "home")), _env.Identity, _env.Time, () => []);
        _workspace.Settings.Target = new TargetSettings { Id = TargetMarker.TryRead(_env.ArchiveDir)!.Id, LastKnownPath = _env.ArchiveDir };
        SettingsStore.AddRoot(_workspace.Settings, _env.RootA, "A");
        SettingsStore.AddRoot(_workspace.Settings, _env.RootB, "B");
        _workspace.SaveSettings();
    }

    public void Dispose() => _env.Dispose();

    private void Write(string root, string relative, string content, DateTime utc) => _env.Write(Path.Combine(root, relative), content, utc);

    [Fact]
    public async Task OldCopyInsideTheOriginal_IsFound_AndHistoryCanBeMerged()
    {
        var huber = Path.Combine(_env.RootA, "Huber GmbH");
        foreach (var f in new[] { "2021/Bilanz.xlsx", "2021/UVA.xlsx", "2022/Bilanz.xlsx", "Vollmacht.docx" })
        {
            Write(huber, f, "aktuell " + f, New);
            Write(huber, Path.Combine("Kopie 2022", f), f.StartsWith("2021") ? "aktuell " + f : "alt " + f, f.StartsWith("2021") ? New : Old);
        }
        // Die Kopie ist insgesamt älter: Ihr jüngster Stand muss vor dem des Originals liegen.
        File.SetLastWriteTimeUtc(Path.Combine(huber, "Kopie 2022", "2021", "Bilanz.xlsx"), Old);
        File.SetLastWriteTimeUtc(Path.Combine(huber, "Kopie 2022", "2021", "UVA.xlsx"), Old);
        File.SetLastWriteTimeUtc(Path.Combine(huber, "2021", "Bilanz.xlsx"), New);
        await _workspace.RunBackupAsync(BackupTrigger.Manual);

        var candidate = Assert.Single(_workspace.DetectCopies());

        Assert.Equal(Path.Combine(huber, "Kopie 2022"), candidate.CopyFolder);
        Assert.Equal(huber, candidate.OriginalFolder);
        Assert.Equal(4, candidate.CopyFiles);
        Assert.Equal(2, candidate.Identical);
        Assert.Equal(2, candidate.OlderInCopy);
        Assert.Equal(CopyAssessment.OlderCopy, candidate.Assessment);

        var merged = await _workspace.MergeCopyAsync(candidate);
        Assert.Equal(2, merged.VersionsAdded);
        Assert.Equal(2, merged.AlreadyKnown);
        _env.WithCatalog(c =>
        {
            var bilanz = c.FindDocumentByAnyPath(Path.Combine(huber, "2022", "Bilanz.xlsx"))!;
            var versions = c.GetVersions(bilanz.Id);
            Assert.Equal(2, versions.Count);
            Assert.Equal(BackupTrigger.Legacy, versions[^1].Trigger);
            return 0;
        });
        Assert.Equal(0, (await _workspace.MergeCopyAsync(candidate)).VersionsAdded);
    }

    [Fact]
    public async Task IdenticalCopyInOtherRoot_IsFound()
    {
        foreach (var client in new[] { "Huber", "Maier", "Gruber" })
        {
            Write(_env.RootA, Path.Combine("Mandanten", client, "Bilanz.xlsx"), client, New);
            Write(_env.RootB, Path.Combine("Mandanten alt", client, "Bilanz.xlsx"), client, New);
        }
        await _workspace.RunBackupAsync(BackupTrigger.Manual);

        var candidate = Assert.Single(_workspace.DetectCopies());

        Assert.Equal(CopyAssessment.Identical, candidate.Assessment);
        Assert.Equal(Path.Combine(_env.RootB, "Mandanten alt"), candidate.CopyFolder); // gleich alt: der Name entscheidet
    }

    [Fact]
    public async Task CopyThatWasEditedFurther_IsFlagged()
    {
        foreach (var f in new[] { "a.docx", "b.docx", "c.docx", "d.docx" })
        {
            Write(_env.RootA, Path.Combine("Projekt", f), "x " + f, Old);
            Write(_env.RootA, Path.Combine("Projekt Arbeitskopie", f), "x " + f, Old);
        }
        Write(_env.RootA, Path.Combine("Projekt", "a.docx"), "im Original geändert", New);
        Write(_env.RootA, Path.Combine("Projekt Arbeitskopie", "b.docx"), "in der Kopie geändert", New.AddDays(1));
        await _workspace.RunBackupAsync(BackupTrigger.Manual);

        var candidate = Assert.Single(_workspace.DetectCopies());

        Assert.Equal(CopyAssessment.EditedSince, candidate.Assessment);
    }

    [Fact]
    public async Task SameTemplatesInManyClientFolders_AreNotACopy()
    {
        foreach (var client in new[] { "Huber", "Maier", "Gruber", "Wimmer" })
        {
            Write(_env.RootA, Path.Combine(client, "Vollmacht.docx"), "Vorlage Vollmacht", Old);
            Write(_env.RootA, Path.Combine(client, "Datenschutz.pdf"), "Vorlage Datenschutz", Old);
            Write(_env.RootA, Path.Combine(client, "AGB.pdf"), "Vorlage AGB", Old);
            for (var i = 0; i < 10; i++)
                Write(_env.RootA, Path.Combine(client, $"Beleg {i}.pdf"), $"{client} Beleg {i}", Old);
        }
        await _workspace.RunBackupAsync(BackupTrigger.Manual);

        Assert.Empty(_workspace.DetectCopies());
    }

    [Fact]
    public async Task IgnoredCopy_IsNotSuggestedAgain()
    {
        foreach (var f in new[] { "1.txt", "2.txt", "3.txt" })
        {
            Write(_env.RootA, Path.Combine("X", f), f, Old);
            Write(_env.RootA, Path.Combine("X Kopie", f), f, Old);
        }
        await _workspace.RunBackupAsync(BackupTrigger.Manual);
        var candidate = Assert.Single(_workspace.DetectCopies());

        _workspace.IgnoreCopy(candidate);

        Assert.Empty(_workspace.DetectCopies());
    }
}

public class CopyDetectorHistoryTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task CopyContainingOnlyEarlierStates_IsFoundViaVersionHistory()
    {
        var workspace = new FilemanWorkspace(new AppPaths(Path.Combine(_env.Base, "home")), _env.Identity, _env.Time, () => []);
        workspace.Settings.Target = new TargetSettings { Id = TargetMarker.TryRead(_env.ArchiveDir)!.Id, LastKnownPath = _env.ArchiveDir };
        SettingsStore.AddRoot(workspace.Settings, _env.RootA, "A");
        var huber = Path.Combine(_env.RootA, "Huber GmbH");
        string[] files = ["2021/Bilanz.xlsx", "2021/UVA.xlsx", "2022/Bilanz.xlsx"];
        var t1 = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var f in files)
            _env.Write(Path.Combine(huber, f), "Stand 1 " + f, t1);
        await workspace.RunBackupAsync(BackupTrigger.Manual);
        // Damals als Sicherung kopiert, danach im Original weitergearbeitet:
        foreach (var f in files)
        {
            _env.Write(Path.Combine(huber, "Kopie 2022", f), "Stand 1 " + f, t1);
            _env.Write(Path.Combine(huber, f), "Stand 2 " + f, t2);
        }
        await workspace.RunBackupAsync(BackupTrigger.Manual);

        var candidate = Assert.Single(workspace.DetectCopies());

        Assert.Equal(Path.Combine(huber, "Kopie 2022"), candidate.CopyFolder);
        Assert.Equal(0, candidate.Identical);
        Assert.Equal(3, candidate.OlderInCopy);
        Assert.Equal(CopyAssessment.OlderCopy, candidate.Assessment);
    }
}
