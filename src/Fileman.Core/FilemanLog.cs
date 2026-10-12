using System.Globalization;

namespace Fileman.Core;

/// <summary>Einfaches Protokoll in eine Textdatei (für den Support). Wird bei 5 MB einmal rotiert.</summary>
public static class FilemanLog
{
    private const long MaxSize = 5 * 1024 * 1024;
    private static readonly object Sync = new();
    private static string? _file;

    public static void Configure(string file)
    {
        lock (Sync)
            _file = file;
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        lock (Sync)
        {
            if (_file is null)
                return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                if (File.Exists(_file) && new FileInfo(_file).Length > MaxSize)
                    File.Move(_file, _file + ".1", overwrite: true);
                File.AppendAllText(_file, $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {level} {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Protokoll darf nie den Betrieb stören.
            }
        }
    }
}
