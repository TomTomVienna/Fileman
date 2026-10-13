using Fileman.Core.Legacy;

namespace Fileman.Tests;

public class LegacyDetectorTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    private readonly string _disk;

    public LegacyDetectorTests()
    {
        _disk = Path.Combine(_env.Base, "Externe Platte");
    }

    public void Dispose() => _env.Dispose();

    private void Tree(string folder, params string[] clients)
    {
        foreach (var client in clients)
        {
            foreach (var year in new[] { "2021", "2022" })
                _env.Write(Path.Combine(folder, "Mandanten", client, year, "Bilanz.xlsx"), "x");
        }
    }

    [Fact]
    public void FindsDatedSnapshots_UndatedCopies_AndSkipsUnrelatedFolders()
    {
        // Master
        foreach (var client in new[] { "Huber", "Maier", "Gruber" })
            _env.Write(Path.Combine(_env.RootA, client, "2022", "Bilanz.xlsx"), "x");
        // Platte: Zwischenebene "Sicherungen" mit Datumsordnern, dazu eine Kopie ohne Datum und fremde Ordner
        Tree(Path.Combine(_disk, "Sicherungen", "Sicherung 2022-01-31"), "Huber", "Maier");
        Tree(Path.Combine(_disk, "Sicherungen", "Sicherung 2023-01-31"), "Huber", "Maier", "Gruber");
        Tree(Path.Combine(_disk, "Sicherungen", "15.06.2023"), "Huber", "Maier", "Gruber");
        Tree(Path.Combine(_disk, "Sicherungen", "Kopie Mandanten"), "Huber", "Maier", "Gruber");
        _env.Write(Path.Combine(_disk, "Sicherungen", "Vorlagen", "Briefkopf.dotx"), "x");
        _env.Write(Path.Combine(_disk, "Privat", "Urlaub", "Foto.jpg"), "x");
        Directory.CreateDirectory(Path.Combine(_disk, "Sicherungen", "Neuer Ordner"));

        var candidates = LegacyDetector.Detect(_disk, [_env.RootA], [], []);

        var suggested = candidates.Where(c => c.Suggested).Select(c => Path.GetFileName(c.Path)).ToList();
        Assert.Equal(["Sicherung 2022-01-31", "Sicherung 2023-01-31", "15.06.2023", "Kopie Mandanten"], suggested);
        Assert.Contains(candidates, c => Path.GetFileName(c.Path) == "Vorlagen" && !c.Suggested);
        Assert.Contains(candidates, c => Path.GetFileName(c.Path) == "Neuer Ordner" && !c.Suggested && c.Reason == "leer");
        Assert.All(candidates.Where(c => c.Suggested), c => Assert.True(c.MasterOverlap > 0.5));
    }

    [Fact]
    public void AlreadyAddedFolders_AreMarkedButNotSuggestedAgain()
    {
        Tree(Path.Combine(_disk, "2022-01-31"), "Huber");
        Tree(Path.Combine(_disk, "2023-01-31"), "Huber");

        var candidates = LegacyDetector.Detect(_disk, [], [], [Path.Combine(_disk, "2022-01-31")]);

        var first = Assert.Single(candidates, c => c.AlreadyAdded);
        Assert.False(first.Suggested);
        Assert.Single(candidates, c => c.Suggested);
    }

    [Fact]
    public void MasterAndArchive_AreNeverCandidates()
    {
        Tree(Path.Combine(_env.Base, "Sicherung 2022-01-31"), "Huber");

        var candidates = LegacyDetector.Detect(_env.Base, [_env.RootA, _env.RootB], [Path.Combine(_env.Base, "SSD")], []);

        Assert.DoesNotContain(candidates, c => c.Path == _env.RootA || c.Path.Contains("SSD"));
        Assert.Contains(candidates, c => c.Suggested);
    }
}
