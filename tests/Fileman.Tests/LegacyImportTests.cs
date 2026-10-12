using Fileman.Core;
using Fileman.Core.Archive;
using Fileman.Core.Catalog;
using Fileman.Core.Configuration;
using Fileman.Core.Legacy;
using Fileman.Core.Storage;

namespace Fileman.Tests;

public class LegacyImportTests : IDisposable
{
    private static readonly DateTime T2021 = new(2021, 12, 10, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2022 = new(2022, 12, 10, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2025 = new(2025, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly TestEnvironment _env = new();
    private readonly FilemanWorkspace _workspace;
    private readonly string _external;

    public LegacyImportTests()
    {
        _external = Path.Combine(_env.Base, "Externe Platte", "Sicherungen");
        _workspace = new FilemanWorkspace(new AppPaths(Path.Combine(_env.Base, "home")), _env.Identity, _env.Time, () => []);
        _workspace.Settings.Target = new TargetSettings { Id = TargetMarker.TryRead(_env.ArchiveDir)!.Id, LastKnownPath = _env.ArchiveDir };
        SettingsStore.AddRoot(_workspace.Settings, _env.RootA, "Mandanten");
    }

    public void Dispose() => _env.Dispose();

    private void Master(string relative, string content, DateTime utc) => _env.Write(Path.Combine(_env.RootA, relative), content, utc);

    private void Old(string snapshot, string relative, string content, DateTime utc) =>
        _env.Write(Path.Combine(_external, snapshot, relative), content, utc);

    /// <summary>Master (heute) und drei alte Sicherungen desselben Teilbaums.</summary>
    private async Task ArrangeAsync()
    {
        Master("Huber GmbH/2023/Bilanz.xlsx", "Huber Stand 3", T2025);
        Master("Huber GmbH/Vollmacht.docx", "Vollmacht", T2021);
        Master("Maier KG/2023/Bilanz.xlsx", "Maier Stand 2", T2022);
        Master("Maier KG/2023/UVA.xlsx", "Maier UVA", T2021);
        Master("Dr. Steiner & Partner/2023/Bilanz.xlsx", "Steiner Bilanz", T2021);
        Master("Dr. Steiner & Partner/2023/UVA.xlsx", "Steiner UVA", T2021);
        Master("Dr. Steiner & Partner/Vollmacht.docx", "Steiner Vollmacht", T2021);
        await _workspace.RunBackupAsync(BackupTrigger.Manual);

        foreach (var snapshot in new[] { "Sicherung 2022-01-31", "Sicherung 2023-01-31", "Sicherung 2024-01-31" })
        {
            var year = int.Parse(snapshot[^10..^6]);
            Old(snapshot, "Mandanten/Huber GmbH/2023/Bilanz.xlsx", year == 2022 ? "Huber Stand 1" : "Huber Stand 2", year == 2022 ? T2021 : T2022);
            Old(snapshot, "Mandanten/Huber GmbH/Vollmacht.docx", "Vollmacht", T2021);
            Old(snapshot, "Mandanten/Maier KG/2023/Bilanz.xlsx", year == 2024 ? "Maier Stand 2" : "Maier Stand 1", year == 2024 ? T2022 : T2021);
            Old(snapshot, "Mandanten/Maier KG/2023/UVA.xlsx", "Maier UVA", T2021);
            Old(snapshot, "Mandanten/Dr. Steiner/2023/Bilanz.xlsx", "Steiner Bilanz", T2021);
            Old(snapshot, "Mandanten/Dr. Steiner/2023/UVA.xlsx", "Steiner UVA", T2021);
            Old(snapshot, "Mandanten/Dr. Steiner/Vollmacht.docx", "Steiner Vollmacht", T2021);
            if (year < 2024)
                Old(snapshot, "Mandanten/Huber GmbH/Alt/Brief.docx", "alter Brief", T2021); // im Master gelöscht
            if (year == 2022)
                Old(snapshot, "Vorlagen/Briefkopf.dotx", "Vorlage", T2021);                 // kein Gegenstück im Master
        }
        SettingsStore.AddLegacyFolders(_workspace.Settings, Directory.GetDirectories(_external));
    }

    [Fact]
    public async Task Import_StoresEverythingAsIs_ReadingEachStateOnlyOnce()
    {
        await ArrangeAsync();

        var result = await _workspace.ImportLegacyAsync();

        Assert.Equal(3, result.Imported.Count);
        Assert.Equal(9 + 8 + 7, result.Files);
        Assert.Equal(11, result.FilesRead); // 9 Stände aus 2022 + Huber Stand 2 (2023) + Maier Stand 2 (2024)
        var asIs = Path.Combine(_env.ArchiveDir, ArchiveStore.FilesFolderName,
            LegacyImporter.ArchivePathFor(Path.Combine(_external, "Sicherung 2022-01-31"), Path.Combine("Mandanten", "Huber GmbH", "2023", "Bilanz.xlsx")));
        Assert.Equal("Huber Stand 1", File.ReadAllText(asIs));
        Assert.Contains(LegacyImporter.AreaName, asIs);

        var again = await _workspace.ImportLegacyAsync();
        Assert.Equal(3, again.AlreadyImported.Count);
        Assert.Equal(0, again.Files);
    }

    [Fact]
    public async Task Proposal_FindsMainMappingAndRenamedFolder_WithoutConfiguration()
    {
        await ArrangeAsync();
        await _workspace.ImportLegacyAsync();

        var proposal = _workspace.ProposeLegacyMapping();

        var groups = proposal.Groups;
        Assert.Equal(2, groups.Count);
        var main = Assert.Single(groups, g => g.LegacyPrefix == "Mandanten");
        Assert.Equal(_env.RootA, main.TargetPrefix);
        Assert.Equal(3, main.Mappings.Count);
        var renamed = Assert.Single(groups, g => g.LegacyPrefix == Path.Combine("Mandanten", "Dr. Steiner"));
        Assert.Equal(Path.Combine(_env.RootA, "Dr. Steiner & Partner"), renamed.TargetPrefix);
        Assert.Equal(3, renamed.Mappings.Count); // in allen drei Sicherungen erkannt
        Assert.Equal(1, proposal.UnmappedFiles.Values.Sum()); // Vorlagen/Briefkopf.dotx
        Assert.Equal(0, main.NewerThanMaster);
    }

    [Fact]
    public async Task Consolidation_BuildsVersionHistory_AndIsRepeatable()
    {
        await ArrangeAsync();
        await _workspace.ImportLegacyAsync();
        var proposal = _workspace.ProposeLegacyMapping();

        var result = await _workspace.ConsolidateLegacyAsync(proposal.Mappings);

        Assert.Equal(1, result.Unmapped);
        Assert.Equal(1, result.DocumentsCreated); // Brief.docx gibt es nur noch im Altbestand
        Assert.Equal(4, result.VersionsAdded);    // Huber Stand 1 + 2, Maier Stand 1, Brief
        _env.WithCatalog(catalog =>
        {
            var huber = catalog.FindDocumentByAnyPath(Path.Combine(_env.RootA, "Huber GmbH", "2023", "Bilanz.xlsx"))!;
            var versions = catalog.GetVersions(huber.Id);
            Assert.Equal(3, versions.Count);
            Assert.Equal(BackupTrigger.Legacy, versions[^1].Trigger);
            Assert.Equal(T2021, versions[^1].LastWriteUtc); // älteste zuletzt
            var steiner = catalog.FindDocumentByAnyPath(Path.Combine(_env.RootA, "Dr. Steiner & Partner", "2023", "Bilanz.xlsx"))!;
            Assert.Single(catalog.GetVersions(steiner.Id)); // identische Kopien zusammengeführt
            var brief = catalog.FindDocumentByAnyPath(Path.Combine(_env.RootA, "Huber GmbH", "Alt", "Brief.docx"))!;
            Assert.True(brief.IsDeleted);
            return 0;
        });

        // Versionen liegen mit Datum im Namen am Master-Ort, der nur im Altbestand vorhandene Brief auch unter exaktem Namen.
        var folder = Path.Combine(_env.ArchiveDir, ArchiveStore.FilesFolderName, ArchiveStore.MirrorPath(Path.Combine(_env.RootA, "Huber GmbH")));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(folder, "2023"), "Bilanz (*).xlsx").Length);
        Assert.Equal("alter Brief", File.ReadAllText(Path.Combine(folder, "Alt", "Brief.docx")));

        var again = await _workspace.ConsolidateLegacyAsync(proposal.Mappings);
        Assert.Equal(0, again.VersionsAdded);
        Assert.Equal(0, again.DocumentsCreated);
    }

    [Fact]
    public async Task UnreachableLegacyFolder_IsReportedAndImportedLater()
    {
        await ArrangeAsync();
        Directory.Move(Path.Combine(_env.Base, "Externe Platte"), Path.Combine(_env.Base, "abgesteckt"));

        var result = await _workspace.ImportLegacyAsync();

        Assert.Equal(3, result.Unreachable.Count);
        Directory.Move(Path.Combine(_env.Base, "abgesteckt"), Path.Combine(_env.Base, "Externe Platte"));
        Assert.Equal(3, (await _workspace.ImportLegacyAsync()).Imported.Count);
    }

    [Fact]
    public void LegacyFolderMustNotOverlapWithRoots()
    {
        Assert.Throws<FilemanException>(() => SettingsStore.AddLegacyFolders(_workspace.Settings, [Path.Combine(_env.RootA, "Alt")]));
    }
}

public class LegacyMapperSharedEvidenceTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task RenamedFolder_IsRecognizedInOlderBackups_EvenWhenContentChangedSince()
    {
        var workspace = new FilemanWorkspace(new AppPaths(Path.Combine(_env.Base, "home")), _env.Identity, _env.Time, () => []);
        workspace.Settings.Target = new TargetSettings { Id = TargetMarker.TryRead(_env.ArchiveDir)!.Id, LastKnownPath = _env.ArchiveDir };
        SettingsStore.AddRoot(workspace.Settings, _env.RootA, "Mandanten");
        var old = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        string[] clients = ["Huber", "Maier", "Gruber", "Steiner"];
        string[] files = ["2021/Bilanz.xlsx", "2021/UVA.xlsx", "2022/Bilanz.xlsx"];
        foreach (var client in clients)
        {
            var master = client == "Steiner" ? "Steiner & Partner" : client;
            foreach (var file in files)
                _env.Write(Path.Combine(_env.RootA, master, file), $"{client} {file} neu", newer);
        }
        await workspace.RunBackupAsync(BackupTrigger.Manual);
        var external = Path.Combine(_env.Base, "Extern");
        foreach (var client in clients)
        {
            foreach (var file in files)
            {
                // 2022: alle Inhalte noch alt (kein Inhaltsbeweis); 2024: identisch mit dem Master
                _env.Write(Path.Combine(external, "Sicherung 2022-01-01", "Mandanten", client, file), $"{client} {file} alt", old);
                _env.Write(Path.Combine(external, "Sicherung 2024-06-01", "Mandanten", client, file), $"{client} {file} neu", newer);
            }
        }
        SettingsStore.AddLegacyFolders(workspace.Settings, Directory.GetDirectories(external));
        await workspace.ImportLegacyAsync();

        var proposal = workspace.ProposeLegacyMapping();

        var renamed = Assert.Single(proposal.Groups, g => g.TargetPrefix.EndsWith("Steiner & Partner", StringComparison.Ordinal));
        Assert.Equal(2, renamed.Mappings.Count);
        Assert.Equal(0, proposal.UnmappedFiles.Values.Sum());
    }
}
