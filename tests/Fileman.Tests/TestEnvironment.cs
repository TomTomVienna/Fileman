using Fileman.Core;
using Fileman.Core.Archive;
using Fileman.Core.Backup;
using Fileman.Core.Catalog;
using Fileman.Core.Configuration;
using Fileman.Core.Identity;
using Fileman.Core.Scanning;

namespace Fileman.Tests;

public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Temporäre Ordnerstruktur mit zwei Quellordnern und einem Archiv.</summary>
public sealed class TestEnvironment : IDisposable
{
    public TestEnvironment()
    {
        Base = Path.Combine(Path.GetTempPath(), "fileman-tests", Guid.NewGuid().ToString("N"));
        RootA = Directory.CreateDirectory(Path.Combine(Base, "Quelle A")).FullName;
        RootB = Directory.CreateDirectory(Path.Combine(Base, "Quelle B")).FullName;
        ArchiveDir = Path.Combine(Base, "SSD", BackupArchive.DefaultFolderName);
        BackupArchive.Initialize(ArchiveDir, "Test");
        Roots = [RootA, RootB];
    }

    public string Base { get; }
    public string RootA { get; }
    public string RootB { get; }
    public string ArchiveDir { get; }
    public List<string> Roots { get; }
    public ManualTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));
    public IFileIdentityProvider Identity { get; set; } = FileIdentityProvider.ForCurrentPlatform();
    public List<string> Excludes { get; } = [.. FilemanSettings.DefaultExcludePatterns];
    public int FileParallelism { get; set; } = 4;

    public string Write(string path, string content, DateTime? lastWriteUtc = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, lastWriteUtc ?? Time.Now.UtcDateTime.AddMinutes(-1));
        return path;
    }

    public BackupResult Backup(IReadOnlyList<ScanScope>? scopes = null, BackupTrigger trigger = BackupTrigger.Manual)
    {
        using var archive = BackupArchive.Open(ArchiveDir);
        var engine = new BackupEngine(archive, Identity, new ExclusionFilter(Excludes), Time);
        var result = engine.RunAsync(new BackupOptions { Roots = Roots, Scopes = scopes, Trigger = trigger, FileParallelism = FileParallelism })
            .GetAwaiter().GetResult();
        Time.Advance(TimeSpan.FromMinutes(1));
        return result;
    }

    public T WithCatalog<T>(Func<CatalogDatabase, T> action)
    {
        using var archive = BackupArchive.Open(ArchiveDir, readOnly: true);
        return action(archive.Catalog);
    }

    public DocumentRecord SingleDocument(string text) =>
        WithCatalog(c => Assert.Single(c.Find(new FindQuery { Text = text })));

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Base, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Base, recursive: true);
        }
        catch (IOException) { }
    }
}
