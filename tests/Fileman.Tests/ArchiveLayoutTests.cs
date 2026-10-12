using System.Globalization;
using Fileman.Core.Identity;
using Fileman.Core.Storage;

namespace Fileman.Tests;

/// <summary>Die Ablage auf der Platte muss auch ohne Fileman verständlich sein.</summary>
public class ArchiveLayoutTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    /// <summary>Ort im Archiv, an dem eine Quelldatei bzw. ein Quellordner gespiegelt wird.</summary>
    private string Mirror(string sourcePath) =>
        Path.Combine(_env.ArchiveDir, ArchiveStore.FilesFolderName, ArchiveStore.MirrorPath(sourcePath));

    private static string Stamp(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture);

    [Fact]
    public void MirrorPath_UsesDriveLetterAndUncAsFirstFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(@"D\Mandanten\Bilanz.xlsx", ArchiveStore.MirrorPath(@"D:\Mandanten\Bilanz.xlsx"));
            Assert.Equal(@"UNC\server\freigabe\a.pdf", ArchiveStore.MirrorPath(@"\\server\freigabe\a.pdf"));
        }
        else
        {
            Assert.Equal("home/kanzlei/a.pdf", ArchiveStore.MirrorPath("/home/kanzlei/a.pdf"));
        }
    }

    [Fact]
    public void CurrentStateUnderExactName_AndEveryVersionWithTimestamp()
    {
        var v1Time = new DateTime(2026, 10, 1, 7, 10, 0, DateTimeKind.Utc);
        var v2Time = new DateTime(2026, 10, 12, 12, 32, 0, DateTimeKind.Utc);
        var file = _env.Write(Path.Combine(_env.RootA, "Huber", "Bilanz.xlsx"), "Stand 1", v1Time);
        _env.Backup();
        _env.Write(file, "Stand 2", v2Time);
        _env.Backup();

        var folder = Mirror(Path.GetDirectoryName(file)!);
        Assert.Equal("Stand 2", File.ReadAllText(Path.Combine(folder, "Bilanz.xlsx")));
        Assert.Equal("Stand 1", File.ReadAllText(Path.Combine(folder, $"Bilanz ({Stamp(v1Time)}).xlsx")));
        Assert.Equal("Stand 2", File.ReadAllText(Path.Combine(folder, $"Bilanz ({Stamp(v2Time)}).xlsx")));
        Assert.Equal(3, Directory.GetFiles(folder).Length);
        Assert.True(File.GetAttributes(Path.Combine(folder, "Bilanz.xlsx")).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal(v2Time, File.GetLastWriteTimeUtc(Path.Combine(folder, $"Bilanz ({Stamp(v2Time)}).xlsx")));
        Assert.True(File.Exists(Path.Combine(_env.ArchiveDir, "LIESMICH.txt")));
    }

    [Fact]
    public void CurrentStateAndVersion_ShareStorage_ViaHardLink()
    {
        var file = _env.Write(Path.Combine(_env.RootA, "a.txt"), "inhalt");
        _env.Write(Path.Combine(_env.RootB, "kopie.txt"), "inhalt");
        _env.Backup();

        var ids = FileIdentityProvider.ForCurrentPlatform();
        var folder = Mirror(_env.RootA);
        var current = ids.TryGetFileId(Path.Combine(folder, "a.txt"));
        var version = ids.TryGetFileId(Directory.GetFiles(folder, "a (*).txt").Single());
        var copy = ids.TryGetFileId(Path.Combine(Mirror(_env.RootB), "kopie.txt"));
        Assert.NotNull(current);
        Assert.Equal(current, version);
        Assert.Equal(current, copy); // gleicher Inhalt in anderer Datei: ebenfalls nur einmal gespeichert
        Assert.Equal(1, _env.WithCatalog(c => c.GetStatistics()).Blobs);
    }

    [Fact]
    public void DeletedFile_StaysInArchiveUnderItsName()
    {
        var file = _env.Write(Path.Combine(_env.RootA, "Vertrag.pdf"), "wichtig");
        _env.Backup();
        File.Delete(file);
        _env.Backup();

        Assert.Equal("wichtig", File.ReadAllText(Path.Combine(Mirror(_env.RootA), "Vertrag.pdf")));
    }

    [Fact]
    public void MovedFile_HasAllVersionsInOldAndNewFolder()
    {
        var t1 = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc);
        var file = _env.Write(Path.Combine(_env.RootA, "Laufend", "Steuer.xlsx"), "eins", t1);
        _env.Backup();
        _env.Write(file, "zwei", t2);
        _env.Backup();
        var moved = Path.Combine(_env.RootB, "Erledigt", "Steuer 2025.xlsx");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(file, moved);
        _env.Backup();

        var oldFolder = Mirror(Path.GetDirectoryName(file)!);
        var newFolder = Mirror(Path.GetDirectoryName(moved)!);
        Assert.Equal(["Steuer (" + Stamp(t1) + ").xlsx", "Steuer (" + Stamp(t2) + ").xlsx", "Steuer.xlsx"],
            Directory.GetFiles(oldFolder).Select(Path.GetFileName).Order());
        Assert.Equal(["Steuer 2025 (" + Stamp(t1) + ").xlsx", "Steuer 2025 (" + Stamp(t2) + ").xlsx", "Steuer 2025.xlsx"],
            Directory.GetFiles(newFolder).Select(Path.GetFileName).Order());
        Assert.Equal("zwei", File.ReadAllText(Path.Combine(newFolder, "Steuer 2025.xlsx")));
        Assert.Equal(2, _env.WithCatalog(c => c.GetStatistics()).Blobs); // nichts doppelt gespeichert
    }

    [Fact]
    public void MovedFolder_GetsMarkerInOldArchiveFolder()
    {
        _env.Write(Path.Combine(_env.RootA, "Mandanten", "Huber", "2025", "Bilanz.xlsx"), "b");
        _env.Write(Path.Combine(_env.RootA, "Mandanten", "Huber", "Brief.docx"), "c");
        _env.Backup();
        var target = Path.Combine(_env.RootB, "Ehemalige");
        Directory.CreateDirectory(target);
        Directory.Move(Path.Combine(_env.RootA, "Mandanten", "Huber"), Path.Combine(target, "Huber"));

        var result = _env.Backup();

        Assert.Equal(2, result.Moved);
        var marker = Path.Combine(Mirror(Path.Combine(_env.RootA, "Mandanten", "Huber")), "_Fileman – Ordner verschoben.txt");
        Assert.True(File.Exists(marker));
        Assert.Contains(Path.Combine(target, "Huber"), File.ReadAllText(marker));
        // nur der oberste verschobene Ordner bekommt einen Hinweis
        Assert.False(File.Exists(Path.Combine(Mirror(Path.Combine(_env.RootA, "Mandanten", "Huber", "2025")), "_Fileman – Ordner verschoben.txt")));
        Assert.False(File.Exists(Path.Combine(Mirror(Path.Combine(_env.RootA, "Mandanten")), "_Fileman – Ordner verschoben.txt")));
        Assert.True(File.Exists(Path.Combine(Mirror(Path.Combine(target, "Huber", "2025")), "Bilanz.xlsx")));
    }

    [Fact]
    public void RenamedFolder_GetsMarker_ButSingleFileMoveDoesNot()
    {
        var single = _env.Write(Path.Combine(_env.RootA, "Ablage", "einzeln.txt"), "x");
        _env.Write(Path.Combine(_env.RootA, "Ablage", "bleibt.txt"), "y");
        _env.Write(Path.Combine(_env.RootA, "Alt", "a.txt"), "z");
        _env.Backup();
        File.Move(single, Path.Combine(_env.RootB, "einzeln.txt"));
        Directory.Move(Path.Combine(_env.RootA, "Alt"), Path.Combine(_env.RootA, "Neu"));

        _env.Backup();

        Assert.False(File.Exists(Path.Combine(Mirror(Path.Combine(_env.RootA, "Ablage")), "_Fileman – Ordner verschoben.txt")));
        Assert.True(File.Exists(Path.Combine(Mirror(Path.Combine(_env.RootA, "Alt")), "_Fileman – Ordner verschoben.txt")));
    }

    [Fact]
    public void UserFileLookingLikeAVersionName_IsNeverOverwritten()
    {
        var time = new DateTime(2026, 10, 12, 12, 32, 0, DateTimeKind.Utc);
        _env.Write(Path.Combine(_env.RootA, $"Bilanz ({Stamp(time)}).xlsx"), "eigene Datei des Anwenders", time);
        _env.Write(Path.Combine(_env.RootA, "Bilanz.xlsx"), "Bilanz", time);

        var result = _env.Backup();

        Assert.Equal(2, result.Created);
        var folder = Mirror(_env.RootA);
        Assert.Equal("eigene Datei des Anwenders", File.ReadAllText(Path.Combine(folder, $"Bilanz ({Stamp(time)}).xlsx")));
        Assert.Equal("Bilanz", File.ReadAllText(Path.Combine(folder, "Bilanz.xlsx")));
    }
}
