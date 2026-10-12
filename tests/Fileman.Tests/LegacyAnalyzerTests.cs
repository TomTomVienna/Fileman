using Fileman.Core.Legacy;

namespace Fileman.Tests;

public class LegacyAnalyzerTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fileman-legacy", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string snapshot, string relative, string content, DateTime utc)
    {
        var path = Path.Combine(_root, snapshot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, utc);
    }

    [Theory]
    [InlineData("Sicherung 2024-06-30", 2024, 6, 30)]
    [InlineData("20240630", 2024, 6, 30)]
    [InlineData("Backup_2023_01_15", 2023, 1, 15)]
    [InlineData("Stand 30.06.2024", 2024, 6, 30)]
    [InlineData("Kopie 5.3.23", 2023, 3, 5)]
    [InlineData("2022-11", 2022, 11, 1)]
    public void RecognizesDatesInFolderNames(string name, int year, int month, int day)
    {
        Assert.Equal(new DateTime(year, month, day), LegacyAnalyzer.DateFromName(name)!.Value.ToLocalTime().Date);
    }

    [Fact]
    public void FolderWithoutDate_HasNoDate() => Assert.Null(LegacyAnalyzer.DateFromName("Alte Sachen"));

    [Fact]
    public void FindsCopiesChangedStatesDeletedAndMovedFiles()
    {
        var t1 = new DateTime(2022, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2023, 1, 10, 9, 0, 0, DateTimeKind.Utc);
        // Drei Sicherungen desselben Teilbaums, absichtlich nicht in alphabetischer = zeitlicher Reihenfolge benannt
        foreach (var snapshot in new[] { "Sicherung 01.03.2022", "Sicherung 2023-01-15", "Sicherung 2024-06-30" })
        {
            Write(snapshot, @"Mandanten/Huber/Vollmacht.docx", "unverändert", t1);
            Write(snapshot, @"Mandanten/Maier/Notizen.txt", "gleich", t1);
        }
        Write("Sicherung 01.03.2022", "Mandanten/Huber/Bilanz.xlsx", "Stand 1", t1);
        Write("Sicherung 2023-01-15", "Mandanten/Huber/Bilanz.xlsx", "Stand 2 länger", t2);
        Write("Sicherung 2024-06-30", "Mandanten/Huber/Bilanz.xlsx", "Stand 2 länger", t2);
        Write("Sicherung 01.03.2022", "Mandanten/Alt/Brief.docx", "gelöscht später", t1);
        Write("Sicherung 01.03.2022", "Mandanten/Ablage/Vertrag.pdf", "vertrag", t1);
        Write("Sicherung 2024-06-30", "Mandanten/Archiv/Vertrag.pdf", "vertrag", t1);

        var a = LegacyAnalyzer.Analyze(_root);

        Assert.Equal(["Sicherung 01.03.2022", "Sicherung 2023-01-15", "Sicherung 2024-06-30"], a.Snapshots.Select(s => s.Name));
        Assert.Equal(12, a.TotalFiles);
        Assert.Equal(6, a.DistinctPaths);
        Assert.Equal(7, a.DistinctStates); // Bilanz hat 2 Stände
        Assert.Equal(5, a.DuplicateFiles);
        Assert.Equal("Bilanz.xlsx", Path.GetFileName(Assert.Single(a.FilesWithSeveralStates).Key));
        Assert.Equal(2, a.MissingInNewest.Count); // Brief.docx und Ablage/Vertrag.pdf
        var move = Assert.Single(a.PossibleMoves);
        Assert.Equal("Vertrag.pdf", move.FileName);
        Assert.Equal(1, a.NewStatesIn(1)); // 2023: nur die geänderte Bilanz ist neu
        Assert.Equal(1, a.NewStatesIn(2)); // 2024: nur der verschobene Vertrag
    }

    [Fact]
    public void ReportAndCsvAreWritten()
    {
        Write("2024-01-01", "a.txt", "x", DateTime.UtcNow.AddDays(-10));
        Write("2024-02-01", "a.txt", "xy", DateTime.UtcNow.AddDays(-5));
        var analysis = LegacyAnalyzer.Analyze(_root);
        var html = Path.Combine(_root, "bericht", "Analyse.html");

        var csv = LegacyReport.Write(analysis, html);

        Assert.Contains("Analyse alter Sicherungen", File.ReadAllText(html));
        Assert.Equal(3, File.ReadAllLines(csv).Length); // Kopfzeile + 2 Stände
    }

    [Fact]
    public void RootWithoutSubfolders_GivesClearMessage()
    {
        File.WriteAllText(Path.Combine(_root, "lose.txt"), "x");
        var ex = Assert.Throws<Fileman.Core.FilemanException>(() => LegacyAnalyzer.Analyze(_root));
        Assert.Contains("Datumsordner", ex.Message);
    }
}
