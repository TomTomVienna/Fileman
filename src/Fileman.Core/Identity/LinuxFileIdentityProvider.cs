using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Fileman.Core.Identity;

/// <summary>Gerät + Inode über stat(2). Dient vor allem dazu, die Logik unter Linux testen zu können.</summary>
[SupportedOSPlatform("linux")]
public sealed partial class LinuxFileIdentityProvider : IFileIdentityProvider
{
    public string? TryGetFileId(string path)
    {
        // struct stat beginnt auf x86_64 und arm64 mit st_dev (8 Byte) und st_ino (8 Byte).
        var buffer = new byte[256];
        try
        {
            if (Stat(path, buffer) != 0)
                return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        var device = BitConverter.ToUInt64(buffer, 0);
        var inode = BitConverter.ToUInt64(buffer, 8);
        return $"{device:X}:{inode:X16}";
    }

    [LibraryImport("libc", EntryPoint = "stat", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Stat(string path, byte[] buffer);
}
