using System.Text;
using Fileman.Cli;
using Fileman.Core;
using Fileman.Core.Archive;
using Fileman.Core.Automation;
using Fileman.Core.Backup;
using Fileman.Core.Catalog;
using Fileman.Core.Configuration;
using Fileman.Core.Protection;
using Fileman.Core.Restore;

Console.OutputEncoding = Encoding.UTF8;

var arguments = new Arguments(args);
if (arguments.Positional.Count == 0 || arguments.Flag("help"))
{
    PrintHelp();
    return arguments.Positional.Count == 0 && !arguments.Flag("help") ? 1 : 0;
}

var home = arguments.Option("home");
var workspace = new FilemanWorkspace(home is null ? AppPaths.Default() : new AppPaths(home));
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
    Console.Error.WriteLine("Abbruch angefordert …");
};

try
{
    var command = arguments.Positional[0].ToLowerInvariant();
    var rest = arguments.Shift();
    return command switch
    {
        "init-target" => InitTarget(rest),
        "roots" => Roots(rest),
        "backup" => await BackupAsync(rest),
        "status" => Status(),
        "find" => Find(rest),
        "show" => Show(rest),
        "changes" => Changes(rest),
        "restore" => Restore(rest),
        "verify" => Verify(),
        "watch" => await WatchAsync(),
        _ => throw new UsageException($"Unbekannter Befehl \"{command}\". Hilfe: fileman --help"),
    };
}
catch (UsageException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
catch (FilemanException ex)
{
    Console.Error.WriteLine("Fehler: " + ex.Message);
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Abgebrochen. Bereits Gesichertes bleibt erhalten.");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Unerwarteter Fehler: " + ex.Message);
    Console.Error.WriteLine(ex);
    return 1;
}

int InitTarget(Arguments a)
{
    var directory = FilemanWorkspace.ResolveTargetDirectory(a.Require(0, "der Ordner auf der Sicherungsplatte, z. B. E:\\"));
    var marker = workspace.InitializeTarget(directory, a.Option("name"));
    Console.WriteLine($"Sicherungsziel eingerichtet: {workspace.Settings.Target!.LastKnownPath}");
    Console.WriteLine($"Kennung: {marker.Id}");
    return 0;
}

int Roots(Arguments a)
{
    var sub = a.Positional.Count == 0 ? "list" : a.Positional[0].ToLowerInvariant();
    switch (sub)
    {
        case "list":
            if (workspace.Settings.Roots.Count == 0)
                Console.WriteLine("Keine Quellordner eingerichtet. Hinzufügen: fileman roots add <ordner> [--name <anzeigename>]");
            foreach (var root in workspace.Settings.Roots)
            {
                var state = Directory.Exists(root.Path) ? "" : "   (nicht erreichbar)";
                Console.WriteLine(root.Name is null ? $"{root.Path}{state}" : $"{root.Name,-20} {root.Path}{state}");
            }
            return 0;
        case "add":
            var added = SettingsStore.AddRoot(workspace.Settings, a.Require(1, "der Ordner"), a.Option("name"));
            workspace.SaveSettings();
            Console.WriteLine($"Quellordner hinzugefügt: {added.Path}");
            return 0;
        case "remove":
            if (!SettingsStore.RemoveRoot(workspace.Settings, a.Require(1, "Ordner oder Anzeigename")))
                throw new FilemanException("Dieser Quellordner ist nicht eingerichtet.");
            workspace.SaveSettings();
            Console.WriteLine("Quellordner entfernt. Bereits gesicherte Stände bleiben im Archiv.");
            return 0;
        default:
            throw new UsageException("Verwendung: fileman roots [list | add <ordner> [--name <name>] | remove <ordner|name>]");
    }
}

async Task<int> BackupAsync(Arguments a)
{
    var guard = new MassChangeGuard(workspace.Settings.MassChange) { BypassNextRun = a.Flag("force") };
    var lastLine = 0;
    var progress = new Progress<BackupProgress>(p =>
    {
        if (Console.IsOutputRedirected)
            return;
        var text = p.Phase switch
        {
            BackupPhase.Scanning => $"Durchsuche … {p.Processed} Dateien",
            BackupPhase.Saving => $"Sichere {p.Processed + 1}/{p.Total}: {Path.GetFileName(p.CurrentFile)}",
            _ => "Abschluss …",
        };
        text = text.Length > 79 ? text[..79] : text;
        Console.Write("\r" + text.PadRight(Math.Max(lastLine, text.Length)));
        lastLine = text.Length;
    });

    BackupResult result;
    try
    {
        result = await workspace.RunBackupAsync(BackupTrigger.Manual, progress: progress, cancellationToken: cancel.Token, guard: guard);
    }
    catch (MassChangeException ex)
    {
        if (!Console.IsOutputRedirected)
            Console.Write("\r" + new string(' ', lastLine) + "\r");
        PrintMassChange(ex.Report);
        Console.WriteLine("Wenn Sie diese Änderungen selbst vorgenommen haben: fileman backup --force");
        return 5;
    }
    if (!Console.IsOutputRedirected)
        Console.Write("\r" + new string(' ', lastLine) + "\r");

    Console.WriteLine($"Sicherung abgeschlossen ({(result.FinishedUtc - result.StartedUtc).TotalSeconds:0.0} s).");
    Console.WriteLine($"  Dateien geprüft:  {result.FilesScanned}");
    Console.WriteLine($"  neu:              {result.Created}");
    Console.WriteLine($"  geändert:         {result.Modified}");
    Console.WriteLine($"  verschoben:       {result.Moved}  (inkl. umbenannt)");
    Console.WriteLine($"  gelöscht:         {result.Deleted}  (gesicherte Stände bleiben erhalten)");
    Console.WriteLine($"  neu geschrieben:  {DisplayText.Size(result.BytesStored)}");
    foreach (var root in result.SkippedRoots)
        Console.WriteLine($"WARNUNG: Quellordner nicht erreichbar, übersprungen: {root}");
    foreach (var unreadable in result.UnreadableDirectories)
        Console.WriteLine($"WARNUNG: Ordner nicht lesbar, übersprungen: {unreadable.Path}: {unreadable.Message}");
    foreach (var error in result.Errors)
        Console.WriteLine($"FEHLER: {error.Path}: {error.Message}");
    return result.Errors.Count > 0 || result.SkippedRoots.Count > 0 || result.UnreadableDirectories.Count > 0 ? 3 : 0;
}

int Status()
{
    var settings = workspace.Settings;
    Console.WriteLine($"Einstellungen:  {workspace.Paths.SettingsFile}");
    if (File.Exists(workspace.Paths.MassChangeAlarmFile))
        Console.WriteLine("ACHTUNG:        Automatik wegen ungewöhnlich vieler Änderungen angehalten – bitte in der Tray-Anwendung prüfen.");
    Console.WriteLine($"Quellordner:    {settings.Roots.Count}");
    foreach (var root in settings.Roots)
        Console.WriteLine($"  {(Directory.Exists(root.Path) ? "ok   " : "FEHLT")} {root.DisplayName}{(root.Name is null ? "" : $" ({root.Path})")}");

    if (settings.Target is null)
    {
        Console.WriteLine("Sicherungsziel: noch nicht eingerichtet (fileman init-target <ordner>)");
        return 1;
    }
    var located = workspace.LocateArchive();
    Console.WriteLine(located is null
        ? $"Sicherungsziel: NICHT ANGESTECKT (zuletzt {settings.Target.LastKnownPath})"
        : $"Sicherungsziel: {located}");
    if (located is not null)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(located)!);
            if (drive.IsReady)
                Console.WriteLine($"  freier Platz: {DisplayText.Size(drive.AvailableFreeSpace)} von {DisplayText.Size(drive.TotalSize)}");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            // z. B. Netzwerkpfad: keine Platzanzeige
        }
    }

    try
    {
        using var view = workspace.OpenCatalogForReading();
        if (view.IsCache)
            Console.WriteLine("  (Angaben aus der lokalen Katalog-Kopie)");
        var stats = view.Catalog.GetStatistics();
        var last = view.Catalog.GetLastRun();
        Console.WriteLine($"Letzte Sicherung: {(last is null ? "noch keine" : $"{DisplayText.Time(last.FinishedUtc!.Value)} ({DisplayText.Trigger(last.Trigger)}, {last.Changes} Änderungen, {last.Errors} Fehler)")}");
        Console.WriteLine($"Dokumente:      {stats.LiveDocuments} vorhanden, {stats.DeletedDocuments} gelöscht (gesichert)");
        Console.WriteLine($"Versionen:      {stats.Versions}");
        Console.WriteLine($"Archivgröße:    {DisplayText.Size(stats.StoredBytes)} (alle Versionen zusammen {DisplayText.Size(stats.OriginalBytes)}, gleiche Inhalte nur einmal gespeichert)");
    }
    catch (TargetNotConnectedException)
    {
        Console.WriteLine("Noch kein Katalog vorhanden.");
    }
    return 0;
}

int Find(Arguments a)
{
    var text = string.Join(' ', a.Positional);
    using var view = workspace.OpenCatalogForReading();
    var hits = view.Catalog.Find(new FindQuery { Text = text, UnderPath = a.Option("under"), Limit = 200 });
    if (a.Flag("deleted"))
        hits = hits.Where(d => d.IsDeleted).ToList();
    if (hits.Count == 0)
    {
        Console.WriteLine("Nichts gefunden.");
        return 0;
    }
    Console.WriteLine($"{"Nr",6}  {"Vers.",5}  {"Stand",-16}  Pfad");
    foreach (var doc in hits)
    {
        var state = doc.IsDeleted ? $"  [gelöscht {DisplayText.Time(doc.DeletedUtc!.Value)}]" : "";
        Console.WriteLine($"{doc.Id,6}  {doc.VersionCount,5}  {DisplayText.Time(doc.LastWriteUtc),-16}  {doc.Path}{state}");
    }
    if (hits.Count == 200)
        Console.WriteLine("… weitere Treffer; bitte Suche verfeinern.");
    Console.WriteLine("Details und Versionen: fileman show <Nr>");
    return 0;
}

int Show(Arguments a)
{
    var id = a.RequireId(0, "die Dokument-Nummer aus \"fileman find\"");
    using var view = workspace.OpenCatalogForReading();
    var doc = view.Catalog.GetDocument(id) ?? throw new FilemanException($"Dokument {id} gibt es nicht.");
    Console.WriteLine($"Dokument {doc.Id}: {doc.FileName}");
    Console.WriteLine($"  Pfad:          {doc.Path}");
    Console.WriteLine($"  Status:        {(doc.IsDeleted ? $"gelöscht am {DisplayText.Time(doc.DeletedUtc!.Value)}" : "vorhanden")}");
    Console.WriteLine($"  erstmals gesehen: {DisplayText.Time(doc.FirstSeenUtc)}");

    var history = view.Catalog.GetPathHistory(id);
    if (history.Count > 0)
    {
        Console.WriteLine("  Frühere Pfade:");
        foreach (var change in history)
            Console.WriteLine($"    bis {DisplayText.Time(change.ChangedUtc)}  {change.OldPath}");
    }

    Console.WriteLine("  Versionen (neueste zuerst):");
    Console.WriteLine($"    {"Nr",6}  {"Dateistand",-16}  {"gesichert",-16}  {"Größe",10}  {"Art",-11}  Prüfsumme");
    foreach (var v in view.Catalog.GetVersions(id))
        Console.WriteLine($"    {v.Id,6}  {DisplayText.Time(v.LastWriteUtc),-16}  {DisplayText.Time(v.CapturedUtc),-16}  {DisplayText.Size(v.Size),10}  {DisplayText.Trigger(v.Trigger),-11}  {v.Hash[..12]}");
    Console.WriteLine("Wiederherstellen: fileman restore <Versions-Nr> [--to <ordner>]");
    return 0;
}

int Changes(Arguments a)
{
    var count = int.TryParse(a.Option("count"), out var n) ? n : 20;
    using var view = workspace.OpenCatalogForReading();
    foreach (var e in view.Catalog.GetRecentEvents(count))
    {
        var detail = e.OldPath is null ? e.Path : $"{e.OldPath}  →  {e.Path}";
        Console.WriteLine($"{DisplayText.Time(e.Utc)}  {DisplayText.Kind(e.Kind),-10}  {detail}");
    }
    return 0;
}

int Restore(Arguments a)
{
    var versionId = a.RequireId(0, "die Versions-Nummer aus \"fileman show\"");
    using var archive = workspace.OpenArchive(readOnly: true);
    var target = new RestoreService(archive).Restore(versionId, a.Option("to"), cancel.Token);
    Console.WriteLine($"Wiederhergestellt: {target}");
    return 0;
}

int Verify()
{
    using var archive = workspace.OpenArchive(readOnly: true);
    var progress = new Progress<(int Done, int Total)>(p =>
    {
        if (!Console.IsOutputRedirected && (p.Done % 100 == 0 || p.Done == p.Total))
            Console.Write($"\rPrüfe {p.Done}/{p.Total} …");
    });
    var result = ArchiveVerifier.Verify(archive, progress, cancel.Token, workspace.Settings.FileThreads);
    if (!Console.IsOutputRedirected)
        Console.WriteLine();
    if (result.DamagedOrMissing.Count == 0)
    {
        Console.WriteLine($"Alle {result.Checked} gesicherten Inhalte sind in Ordnung.");
        return 0;
    }
    Console.WriteLine($"{result.DamagedOrMissing.Count} von {result.Checked} Inhalten sind beschädigt oder fehlen (Pfade im Ordner \"Dateien\"):");
    foreach (var path in result.DamagedOrMissing)
        Console.WriteLine($"  {path}");
    return 4;
}

async Task<int> WatchAsync()
{
    FilemanLog.Configure(workspace.Paths.LogFile);
    await using var service = new BackupService(workspace);
    ServiceStatus? last = null;
    service.StateChanged += (_, state) =>
    {
        if (state.Status == last || state.Status == ServiceStatus.Running)
            return;
        last = state.Status;
        Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {DisplayText.Status(state.Status)}{(state.Message is null ? "" : " – " + state.Message)}");
    };
    service.BackupCompleted += (_, result) =>
    {
        foreach (var change in result.Changes)
            Console.WriteLine($"{DateTime.Now:HH:mm:ss}    {DisplayText.Kind(change.Kind),-10} {(change.OldPath is null ? change.Path : $"{change.OldPath}  →  {change.Path}")}");
        if (result.ImportedFromSpool > 0)
            Console.WriteLine($"{DateTime.Now:HH:mm:ss}    {result.ImportedFromSpool} Zwischenstände übernommen");
        foreach (var error in result.Errors)
            Console.WriteLine($"{DateTime.Now:HH:mm:ss}    FEHLER {error.Path}: {error.Message}");
    };
    service.MassChangeDetected += (_, report) => PrintMassChange(report);
    if (service.Alarm is { } alarm)
        PrintMassChange(alarm);
    Console.WriteLine("Automatik-Modus läuft. Beenden mit Strg+C.");
    service.Start();
    try
    {
        await Task.Delay(Timeout.Infinite, cancel.Token);
    }
    catch (OperationCanceledException)
    {
    }
    await service.StopAsync();
    return 0;
}

static void PrintMassChange(MassChangeReport report)
{
    Console.WriteLine();
    Console.WriteLine("!!! SICHERUNG ANGEHALTEN – UNGEWÖHNLICH VIELE ÄNDERUNGEN !!!");
    Console.WriteLine(report.Reason);
    Console.WriteLine("Das kann auf einen Verschlüsselungstrojaner hindeuten. Bisherige Versionen im Archiv sind unverändert.");
    if (report.Folders.Count > 0)
    {
        Console.WriteLine("Betroffene Ordner:");
        foreach (var folder in report.Folders)
            Console.WriteLine("  " + folder);
    }
    Console.WriteLine("Beispiele:");
    foreach (var example in report.Examples)
        Console.WriteLine("  " + example);
    Console.WriteLine("Wenn Sie diese Änderungen NICHT selbst vorgenommen haben: Sicherungsplatte sofort abziehen und IT-Betreuung verständigen.");
    Console.WriteLine();
}

static void PrintHelp()
{
    Console.WriteLine("""
        Fileman – Versionssicherung auf eine USB-Platte

        Einrichtung:
          fileman init-target <ordner> [--name <name>]   Sicherungsziel einrichten (z. B. E:\)
          fileman roots list                             Quellordner anzeigen
          fileman roots add <ordner> [--name <name>]     Quellordner hinzufügen
          fileman roots remove <ordner|name>             Quellordner entfernen

        Sichern und prüfen:
          fileman backup [--force]                       Änderungen seit der letzten Sicherung sichern
                                                         (--force: auch bei ungewöhnlich vielen Änderungen)
          fileman status                                 Zustand von Quellordnern, Platte und Archiv
          fileman verify                                 alle gesicherten Inhalte auf Beschädigung prüfen
          fileman watch                                  Automatik-Modus im Vordergrund (wie die Tray-Anwendung)

        Suchen und wiederherstellen:
          fileman find <begriffe> [--under <ordner>] [--deleted]
                                                         sucht auch in früheren Pfaden und Namen
          fileman show <dokument-nr>                     Pfadverlauf und Versionen eines Dokuments
          fileman changes [--count <n>]                  letzte Änderungen
          fileman restore <versions-nr> [--to <ordner>]  Version wiederherstellen (überschreibt nie)

        Allgemein:
          --home <ordner>   anderer Ordner für Einstellungen (Standard: %LOCALAPPDATA%\Fileman)

        Rückgabewerte: 0 = ok, 1 = Fehler, 2 = falscher Aufruf, 3 = Sicherung mit Fehlern, 4 = Archiv beschädigt,
                       5 = angehalten wegen ungewöhnlich vieler Änderungen
        """);
}
