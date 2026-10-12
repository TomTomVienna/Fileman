using System.Runtime.InteropServices;

namespace Fileman.Core.Storage;

/// <summary>
/// Hardlinks: derselbe Dateiinhalt unter mehreren Namen, belegt aber nur einmal Platz (NTFS, ext4 …).
/// Auf Dateisystemen ohne Hardlinks (exFAT, FAT32, manche Netzlaufwerke) wird stattdessen kopiert.
/// </summary>
internal static partial class HardLink
{
    /// <returns>True, wenn ein Hardlink angelegt wurde; false, wenn kopiert werden musste.</returns>
    public static bool CreateOrCopy(string existingFile, string newFile)
    {
        if (TryCreate(existingFile, newFile))
            return true;
        // Manche Windows-Versionen verweigern Hardlinks auf schreibgeschützte Dateien: kurz aufheben und erneut versuchen.
        var attributes = File.GetAttributes(existingFile);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(existingFile, attributes & ~FileAttributes.ReadOnly);
            try
            {
                if (TryCreate(existingFile, newFile))
                    return true;
            }
            finally
            {
                File.SetAttributes(existingFile, attributes);
            }
        }
        File.Copy(existingFile, newFile);
        return false;
    }

    private static bool TryCreate(string existingFile, string newFile)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return CreateHardLinkW(newFile, existingFile, IntPtr.Zero);
            return Link(existingFile, newFile) == 0;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Link(string existingFile, string newFile);
}
