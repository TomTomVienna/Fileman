namespace Fileman.Core.Configuration;

/// <summary>Lokale Ablageorte der Anwendung (Einstellungen, Katalog-Kopie, Zwischenspeicher).</summary>
public sealed class AppPaths
{
    public const string HomeEnvironmentVariable = "FILEMAN_HOME";

    public AppPaths(string homeDirectory)
    {
        Home = PathUtil.Normalize(homeDirectory);
    }

    public string Home { get; }

    public string SettingsFile => Path.Combine(Home, "settings.json");

    /// <summary>Lokale Kopie des Katalogs, damit man auch ohne angesteckte Platte suchen kann.</summary>
    public string CatalogCacheFile => Path.Combine(Home, "catalog-cache.db");

    /// <summary>Zwischenspeicher für Änderungen, solange die Sicherungsplatte nicht angesteckt ist.</summary>
    public string SpoolDirectory => Path.Combine(Home, "spool");

    public string LogFile => Path.Combine(Home, "fileman.log");

    public string ViewDirectory => Path.Combine(Path.GetTempPath(), "Fileman", "Ansicht");

    /// <summary>Standard: %LOCALAPPDATA%\Fileman, überschreibbar mit der Umgebungsvariable FILEMAN_HOME.</summary>
    public static AppPaths Default()
    {
        var overridden = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
            return new AppPaths(overridden);
        return new AppPaths(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "Fileman"));
    }
}
