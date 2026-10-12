using Fileman.Core.Archive;
using Fileman.Core.Backup;
using Fileman.Core.Catalog;
using Fileman.Core.Identity;
using Fileman.Core.Scanning;

namespace Fileman.Tests;

public class ParallelTests : IDisposable
{
    private readonly TestEnvironment _env = new() { FileParallelism = 8 };

    public void Dispose() => _env.Dispose();

    private sealed class SyncProgress(Action<BackupProgress> action) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => action(value);
    }

    [Fact]
    public void ManyFilesInDeepTree_AreAllBackedUp()
    {
        for (var d = 0; d < 20; d++)
            for (var s = 0; s < 5; s++)
                for (var f = 0; f < 10; f++)
                    _env.Write(Path.Combine(_env.RootA, $"Mandant {d}", $"Jahr {s}", $"Beleg {f}.txt"), $"{d}-{s}-{f}");

        var first = _env.Backup();
        var second = _env.Backup();

        Assert.Equal(1000, first.Created);
        Assert.Empty(first.Errors);
        Assert.Equal(0, second.TotalChanges);
        Assert.Equal(1000, _env.WithCatalog(c => c.GetStatistics()).Blobs);
    }

    [Fact]
    public void Scanner_FindsSameFilesWithOneOrManyThreads()
    {
        for (var i = 0; i < 300; i++)
            _env.Write(Path.Combine(_env.RootA, $"a{i % 7}", $"b{i % 3}", $"{i}.txt"), "x");
        var filter = new ExclusionFilter([]);

        var single = new FileScanner(filter, parallelism: 1).Scan(_env.RootA).Files.Select(f => f.Path).Order().ToList();
        var many = new FileScanner(filter, parallelism: 16).Scan(_env.RootA).Files.Select(f => f.Path).Order().ToList();

        Assert.Equal(300, single.Count);
        Assert.Equal(single, many);
    }

    [Fact]
    public void Assignment_IsDeterministic_DespiteParallelProcessing()
    {
        // Ein fehlendes Dokument, zwei neue Dateien mit gleichem Inhalt: immer die erste (nach Pfad) übernimmt die Historie.
        _env.Identity = new NullFileIdentityProvider();
        var original = _env.Write(Path.Combine(_env.RootA, "alt", "Akte.txt"), "gleicher Inhalt");
        _env.Backup();
        for (var i = 0; i < 30; i++)
            _env.Write(Path.Combine(_env.RootB, $"Datei {i:00}.txt"), "anderer Inhalt " + i);
        File.Copy(original, Path.Combine(_env.RootB, "Kopie 1.txt"));
        File.Copy(original, Path.Combine(_env.RootB, "Kopie 2.txt"));
        File.Delete(original);

        var result = _env.Backup();

        Assert.Equal(1, result.Moved);
        var doc = _env.SingleDocument("alt");
        Assert.Equal("Kopie 1.txt", doc.FileName);
    }

    [Fact]
    public async Task Cancellation_KeepsArchiveConsistent_AndNextRunCompletes()
    {
        for (var i = 0; i < 300; i++)
            _env.Write(Path.Combine(_env.RootA, $"{i:000}.txt"), new string('x', 1000) + i);

        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p.Phase == BackupPhase.Saving && p.Processed >= 100)
                cts.Cancel();
        });
        using (var archive = BackupArchive.Open(_env.ArchiveDir))
        {
            var engine = new BackupEngine(archive, _env.Identity, new ExclusionFilter([]), _env.Time);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                engine.RunAsync(new BackupOptions { Roots = _env.Roots, FileParallelism = 8 }, progress, cts.Token));
        }

        var saved = _env.WithCatalog(c => c.GetStatistics());
        Assert.InRange(saved.LiveDocuments, 100, 299);
        Assert.Equal(0, saved.DeletedDocuments);

        var rest = _env.Backup();
        Assert.Equal(300 - saved.LiveDocuments, rest.Created);
        Assert.Equal(300, _env.WithCatalog(c => c.GetStatistics()).Versions);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_env.ArchiveDir, "blobs", "tmp")));
    }
}
