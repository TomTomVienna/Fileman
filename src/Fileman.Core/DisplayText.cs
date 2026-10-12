using System.Globalization;
using Fileman.Core.Automation;
using Fileman.Core.Catalog;

namespace Fileman.Core;

/// <summary>Deutsche Anzeigetexte für Oberflächen (CLI und Tray-Anwendung).</summary>
public static class DisplayText
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-AT");

    public static string Time(DateTime utc) => utc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", German);

    public static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : value.ToString("0.0", German) + " " + units[unit];
    }

    public static string Kind(ChangeKind kind) => kind switch
    {
        ChangeKind.Created => "neu",
        ChangeKind.Modified => "geändert",
        ChangeKind.Renamed => "umbenannt",
        ChangeKind.Moved => "verschoben",
        ChangeKind.Deleted => "gelöscht",
        ChangeKind.Recreated => "wieder da",
        _ => kind.ToString(),
    };

    public static string Status(ServiceStatus status) => status switch
    {
        ServiceStatus.NotConfigured => "Nicht eingerichtet",
        ServiceStatus.Idle => "Alles gesichert",
        ServiceStatus.Pending => "Änderungen erkannt",
        ServiceStatus.Running => "Sicherung läuft",
        ServiceStatus.TargetMissing => "Sicherungsplatte nicht angesteckt",
        ServiceStatus.Paused => "Pausiert",
        ServiceStatus.Error => "Fehler",
        _ => status.ToString(),
    };

    public static string Trigger(BackupTrigger trigger) => trigger switch
    {
        BackupTrigger.Manual => "manuell",
        BackupTrigger.Watcher => "automatisch",
        BackupTrigger.Reconcile => "Abgleich",
        _ => trigger.ToString(),
    };
}
