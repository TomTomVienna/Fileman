namespace Fileman.Core.Identity;

/// <summary>
/// Liefert eine vom Dateisystem vergebene, über Umbenennen und Verschieben (auf demselben Laufwerk) stabile Kennung.
/// Windows: Volume-Seriennummer + NTFS-File-Index. Linux/macOS: Gerät + Inode (für Tests und Entwicklung).
/// </summary>
public interface IFileIdentityProvider
{
    /// <returns>Die Kennung oder null, wenn sie nicht ermittelt werden kann.</returns>
    string? TryGetFileId(string path);
}

public static class FileIdentityProvider
{
    public static IFileIdentityProvider ForCurrentPlatform() =>
        OperatingSystem.IsWindows() ? new WindowsFileIdentityProvider()
        : OperatingSystem.IsLinux() ? new LinuxFileIdentityProvider()
        : new NullFileIdentityProvider();
}

public sealed class NullFileIdentityProvider : IFileIdentityProvider
{
    public string? TryGetFileId(string path) => null;
}
