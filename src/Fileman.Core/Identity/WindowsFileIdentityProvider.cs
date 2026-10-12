using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Fileman.Core.Identity;

[SupportedOSPlatform("windows")]
public sealed partial class WindowsFileIdentityProvider : IFileIdentityProvider
{
    private const uint FileShareAll = 0x1 | 0x2 | 0x4; // Read | Write | Delete
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    public string? TryGetFileId(string path)
    {
        // Zugriffsrecht 0 = nur Attribute abfragen; funktioniert auch bei Dateien, die gerade exklusiv geöffnet sind.
        using SafeFileHandle handle = CreateFileW(ToLongPath(path), 0, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
            return null;
        if (!GetFileInformationByHandle(handle, out var info))
            return null;
        ulong index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        return $"{info.VolumeSerialNumber:X8}:{index:X16}";
    }

    /// <summary>Präfix für Pfade über 260 Zeichen.</summary>
    private static string ToLongPath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..]
        : @"\\?\" + path;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow, CreationTimeHigh;
        public uint LastAccessTimeLow, LastAccessTimeHigh;
        public uint LastWriteTimeLow, LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);
}
