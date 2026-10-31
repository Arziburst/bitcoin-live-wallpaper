using System.Text;

namespace BitcoinWallpaper.Host.Screensaver;

/// <summary>
/// The screensaver's own log, %LOCALAPPDATA%\BitcoinWallpaper\logs\screensaver.log (the wallpaper's log is never
/// opened by this process). Past 1 MB at start it becomes screensaver.1.log. A failing write is dropped.
/// </summary>
internal static class ScreensaverLog
{
    private const long MaxFileBytes = 1024 * 1024;
    private static readonly Lock Gate = new();
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static string? _path;

    public static string FilePath => Path.Combine(Log.Directory, "screensaver.log");

    public static void Open()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Log.Directory);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length >= MaxFileBytes)
                {
                    File.Move(FilePath, Path.Combine(Log.Directory, "screensaver.1.log"), overwrite: true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            _path = FilePath;
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Close()
    {
        lock (Gate)
        {
            _path = null;
        }
    }

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            if (_path is null)
            {
                return;
            }

            try
            {
                File.AppendAllText(_path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {level,-5} {message}{Environment.NewLine}",
                    Utf8);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
