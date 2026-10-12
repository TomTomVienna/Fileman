using Fileman.Core;
using Fileman.Core.Archive;
using Fileman.Core.Backup;
using Fileman.Core.Catalog;
using Fileman.Core.Identity;
using Fileman.Core.Restore;

namespace Fileman.Tests;

public class BackupEngineTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public void FirstBackup_StoresAllFilesOfAllRoots_SecondRunChangesNothing()
    {
        _env.Write(Path.Combine(_env.RootA, "Müller GmbH", "Bilanz 2025.xlsx"), "bilanz");
        _env.Write(Path.Combine(_env.RootB, "Notizen.txt"), "notizen");

        var first = _env.Backup();
        Assert.Equal(2, first.Created);
        Assert.Empty(first.Errors);

        var second = _env.Backup();
        Assert.Equal(0, second.TotalChanges);
        Assert.Equal(2, second.Unchanged);
    }

    [Fact]
    public void ModifiedFile_GetsNewVersion_AndOldVersionCanBeRestored()
    {
        var file = _env.Write(Path.Combine(_env.RootA, "Brief.txt"), "Version 1");
        _env.Backup();
        _env.Write(file, "Version 2 – länger", DateTime.UtcNow.AddMinutes(5));

        var result = _env.Backup();

        Assert.Equal(1, result.Modified);
        var doc = _env.SingleDocument("Brief");
        var versions = _env.WithCatalog(c => c.GetVersions(doc.Id));
        Assert.Equal(2, versions.Count);

        using var archive = BackupArchive.Open(_env.ArchiveDir, readOnly: true);
        var restored = new RestoreService(archive).Restore(versions[^1].Id, Path.Combine(_env.Base, "Wiederhergestellt"));
        Assert.Equal("Version 1", File.ReadAllText(restored));
    }

    [Fact]
    public void OnlyTimestampChanged_DoesNotCreateVersion()
    {
        var file = _env.Write(Path.Combine(_env.RootA, "a.txt"), "gleich");
        _env.Backup();
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(1));

        var result = _env.Backup();

        Assert.Equal(0, result.TotalChanges);
        Assert.Single(_env.WithCatalog(c => c.GetVersions(_env.SingleDocument("a.txt").Id)));
    }

    [Fact]
    public void DeletedFile_IsMarkedDeleted_ButBackupStaysRestorable()
    {
        var file = _env.Write(Path.Combine(_env.RootA, "Vertrag.pdf"), "wichtig");
        _env.Backup();
        File.Delete(file);

        var result = _env.Backup();

        Assert.Equal(1, result.Deleted);
        var doc = _env.SingleDocument("Vertrag");
        Assert.True(doc.IsDeleted);
        using var archive = BackupArchive.Open(_env.ArchiveDir, readOnly: true);
        var version = archive.Catalog.GetVersions(doc.Id).Single();
        var restored = new RestoreService(archive).Restore(version.Id);
        Assert.Equal(file, restored);
        Assert.Equal("wichtig", File.ReadAllText(restored));
    }

    [Fact]
    public void RenamedFile_IsSameDocument_AndFoundByOldName()
    {
        var file = _env.Write(Path.Combine(_env.RootA, "Entwurf.docx"), "inhalt");
        _env.Backup();
        File.Move(file, Path.Combine(_env.RootA, "Endfassung.docx"));

        var result = _env.Backup();

        Assert.Equal(1, result.Moved);
        Assert.Equal(0, result.Created + result.Deleted);
        var doc = _env.SingleDocument("Entwurf");
        Assert.Equal("Endfassung.docx", doc.FileName);
        Assert.False(doc.IsDeleted);
        Assert.Contains(_env.WithCatalog(c => c.GetRecentEvents(10)), e => e.Kind == ChangeKind.Renamed);
    }

    [Fact]
    public void CaseOnlyRename_IsRecorded()
    {
        var file = _env.Write(Path.Combine(_env.RootA, "brief.txt"), "x");
        _env.Backup();
        File.Move(file, Path.Combine(_env.RootA, "Brief.txt"));

        var result = _env.Backup();

        Assert.Equal(1, result.Moved);
        Assert.Equal("Brief.txt", _env.SingleDocument("brief").FileName);
    }

    [Fact]
    public void FileMovedToOtherRoot_AndModified_IsSameDocument()
    {
        var file = _env.Write(Path.Combine(_env.RootA, "Laufend", "Steuer.xlsx"), "alt");
        _env.Backup();
        var target = Path.Combine(_env.RootB, "Archiv 2025", "Steuer.xlsx");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(file, target);
        _env.Write(target, "neu und geändert", DateTime.UtcNow.AddMinutes(3));

        var result = _env.Backup();

        Assert.Equal(1, result.Moved);
        Assert.Equal(1, result.Modified);
        var doc = _env.SingleDocument("Steuer");
        Assert.Equal(target, doc.Path);
        Assert.Equal(2, doc.VersionCount);
        Assert.Single(_env.WithCatalog(c => c.GetPathHistory(doc.Id)));
    }

    [Fact]
    public void CopyThenDeleteOriginal_IsRecognizedByContent()
    {
        _env.Identity = new NullFileIdentityProvider(); // z. B. Verschieben über Laufwerksgrenzen
        var file = _env.Write(Path.Combine(_env.RootA, "Beleg.pdf"), "beleginhalt");
        _env.Backup();
        File.Copy(file, Path.Combine(_env.RootB, "Beleg umbenannt.pdf"));
        File.Delete(file);

        var result = _env.Backup();

        Assert.Equal(1, result.Moved);
        Assert.Equal(0, result.Created + result.Deleted);
    }

    [Fact]
    public void DeletedAndReappearingWithinWindow_IsTreatedAsMove()
    {
        // Automatik-Modus: Löschung und Neuanlage kommen in getrennten Läufen an.
        _env.Identity = new NullFileIdentityProvider();
        var file = _env.Write(Path.Combine(_env.RootA, "x", "Akte.txt"), "akte");
        _env.Backup();
        var copy = Path.Combine(_env.RootB, "Akte.txt");
        File.Copy(file, copy);
        File.Delete(file);

        var deleted = _env.Backup([new ScanScope(Path.Combine(_env.RootA, "x"))], BackupTrigger.Watcher);
        var reappeared = _env.Backup([new ScanScope(_env.RootB)], BackupTrigger.Watcher);

        Assert.Equal(1, deleted.Deleted);
        Assert.Equal(1, reappeared.Moved);
        Assert.Equal(0, reappeared.Created);
        Assert.False(_env.SingleDocument("Akte").IsDeleted);
    }

    [Fact]
    public void MissingRoot_DoesNotMarkItsFilesAsDeleted()
    {
        _env.Write(Path.Combine(_env.RootB, "Netz.txt"), "netz");
        _env.Backup();
        Directory.Move(_env.RootB, _env.RootB + " (getrennt)");

        var result = _env.Backup();

        Assert.Equal(0, result.Deleted);
        Assert.Contains(_env.RootB, result.SkippedRoots);
        Assert.False(_env.SingleDocument("Netz").IsDeleted);
    }

    [Fact]
    public void ScopedRun_OnlyDetectsDeletionsInsideScope()
    {
        var a = _env.Write(Path.Combine(_env.RootA, "eins", "a.txt"), "a");
        var b = _env.Write(Path.Combine(_env.RootA, "zwei", "b.txt"), "b");
        _env.Backup();
        File.Delete(a);
        File.Delete(b);

        var result = _env.Backup([new ScanScope(Path.Combine(_env.RootA, "eins"))], BackupTrigger.Watcher);

        Assert.Equal(1, result.Deleted);
        Assert.False(_env.SingleDocument("b.txt").IsDeleted);
    }

    [Fact]
    public void ExcludedFiles_AreNotBackedUp()
    {
        _env.Write(Path.Combine(_env.RootA, "~$Bilanz.xlsx"), "lock");
        _env.Write(Path.Combine(_env.RootA, "temp.tmp"), "tmp");
        _env.Write(Path.Combine(_env.RootA, "Bilanz.xlsx"), "echt");

        var result = _env.Backup();

        Assert.Equal(1, result.Created);
    }

    [Fact]
    public void IdenticalContent_IsStoredOnlyOnce()
    {
        _env.Write(Path.Combine(_env.RootA, "1.txt"), "derselbe Inhalt");
        _env.Write(Path.Combine(_env.RootB, "2.txt"), "derselbe Inhalt");

        _env.Backup();

        var stats = _env.WithCatalog(c => c.GetStatistics());
        Assert.Equal(2, stats.LiveDocuments);
        Assert.Equal(1, stats.Blobs);
    }

    [Fact]
    public void ArchiveInsideRoot_IsNeverBackedUpItself()
    {
        _env.Roots.Add(Path.Combine(_env.Base, "SSD"));
        _env.Write(Path.Combine(_env.Base, "SSD", "andere Datei.txt"), "x");

        var result = _env.Backup();

        Assert.Equal(1, result.Created);
    }

    [Fact]
    public void Find_IgnoresCaseIncludingUmlauts_AndRequiresAllWords()
    {
        _env.Write(Path.Combine(_env.RootA, "ÄRZTE Meier", "Rechnung März.pdf"), "r");
        _env.Write(Path.Combine(_env.RootA, "Bäcker", "Rechnung April.pdf"), "s");
        _env.Backup();

        var hits = _env.WithCatalog(c => c.Find(new FindQuery { Text = "ärzte rechnung" }));

        Assert.Equal("Rechnung März.pdf", Assert.Single(hits).FileName);
    }

    [Fact]
    public void SecondWriter_IsRejectedWhileArchiveIsOpen()
    {
        using var first = BackupArchive.Open(_env.ArchiveDir);
        Assert.Throws<ArchiveBusyException>(() => BackupArchive.Open(_env.ArchiveDir));
    }
}
