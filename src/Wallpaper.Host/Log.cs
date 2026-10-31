using System.Text;

namespace BitcoinWallpaper.Host;

/// <summary>
/// A small rolling file log in %LOCALAPPDATA%\BitcoinWallpaper\logs: wallpaper.log is the current file;
/// past 1 MB it becomes wallpaper.1.log (older ones shift up to wallpaper.4.log), so at most 5 files remain.
/// </summary>
internal static class Log
{
    public const long MaxFileBytes = 1024 * 1024;
    public const int KeepFiles = 5;
    private const string BaseName = "wallpaper";

    private static readonly Lock Gate = new();
    private static StreamWriter? _file;
    private static bool _canRoll;

    public static string Directory { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BitcoinWallpaper", "logs");

    public static string FilePath => Path.Combine(Directory, BaseName + ".log");

    /// <summary>
    /// Opens the current file for appending. Only the running instance rolls; a second instance that exits
    /// at once writes its line without rolling (canRoll false).
    /// </summary>
    public static void Open(bool canRoll, string? directory = null)
    {
        lock (Gate)
        {
            if (directory is not null)
            {
                Directory = directory;
            }

            System.IO.Directory.CreateDirectory(Directory);
            _canRoll = canRoll;
            if (_canRoll && File.Exists(FilePath) && new FileInfo(FilePath).Length >= MaxFileBytes)
            {
                Roll();
            }

            OpenCurrent();
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Close()
    {
        lock (Gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {level,-5} {message}";
        lock (Gate)
        {
            if (_file is null)
            {
                return;
            }

            try
            {
                // Roll before a line that would take the file past the limit, so no file exceeds it.
                var bytes = _file.Encoding.GetByteCount(line) + _file.NewLine.Length;
                if (_canRoll && _file.BaseStream.Length > 0 && _file.BaseStream.Length + bytes > MaxFileBytes)
                {
                    _file.Dispose();
                    _file = null;
                    Roll();
                    OpenCurrent();
                }

                _file!.WriteLine(line);
            }
            catch (IOException)
            {
                // A log that cannot be written must never take the wallpaper down.
            }
        }
    }

    private static void OpenCurrent()
    {
        var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _file = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
    }

    /// <summary>wallpaper.log -> wallpaper.1.log -> ... -> wallpaper.4.log; the oldest is deleted.</summary>
    private static void Roll()
    {
        try
        {
            var oldest = Numbered(KeepFiles - 1);
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (var i = KeepFiles - 2; i >= 1; i--)
            {
                if (File.Exists(Numbered(i)))
                {
                    File.Move(Numbered(i), Numbered(i + 1));
                }
            }

            if (File.Exists(FilePath))
            {
                File.Move(FilePath, Numbered(1));
            }
        }
        catch (IOException)
        {
            // Another process holds a file (an editor): keep appending to the current one.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Numbered(int index) => Path.Combine(Directory, $"{BaseName}.{index}.log");
}
