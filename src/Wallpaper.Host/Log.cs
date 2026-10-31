using System.Text;

namespace BitcoinWallpaper.Host;

/// <summary>
/// A small rolling file log in %LOCALAPPDATA%\BitcoinWallpaper\logs: wallpaper.log is the current file;
/// past 1 MB it becomes wallpaper.1.log (older ones shift up to wallpaper.4.log), so at most 5 files remain.
/// The supervisor and the wallpaper process write to the same file: each line is appended under a per-session named
/// mutex (the file is opened for that line only, so no process writes at a stale position or into a rolled file).
/// </summary>
internal static class Log
{
    public const long MaxFileBytes = 1024 * 1024;
    public const int KeepFiles = 5;
    private const string BaseName = "wallpaper";
    private const string MutexName = @"Local\BitcoinWallpaper.Log";

    /// <summary>A process that holds the mutex longer than this is skipped: the line is written without it.</summary>
    private static readonly TimeSpan MutexWait = TimeSpan.FromSeconds(1);

    private static readonly Lock Gate = new();
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static Mutex? _mutex;
    private static bool _open;
    private static bool _canRoll;

    public static string Directory { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BitcoinWallpaper", "logs");

    public static string FilePath => Path.Combine(Directory, BaseName + ".log");

    /// <summary>
    /// Starts logging. Only the running wallpaper rolls; the supervisor and a second instance that exits at once
    /// write their lines without rolling (canRoll false).
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
            _mutex ??= new Mutex(initiallyOwned: false, MutexName);
            _canRoll = canRoll;
            _open = true;
            if (_canRoll)
            {
                Exclusive(() =>
                {
                    if (File.Exists(FilePath) && new FileInfo(FilePath).Length >= MaxFileBytes)
                    {
                        Roll();
                    }
                });
            }
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Close()
    {
        lock (Gate)
        {
            _open = false;
            _mutex?.Dispose();
            _mutex = null;
        }
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {level,-5} {message}";
        lock (Gate)
        {
            if (!_open)
            {
                return;
            }

            try
            {
                Exclusive(() =>
                {
                    using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete);

                    // Roll before a line that would take the file past the limit, so no file exceeds it.
                    var bytes = Utf8.GetBytes(line + Environment.NewLine);
                    if (_canRoll && stream.Length > 0 && stream.Length + bytes.Length > MaxFileBytes)
                    {
                        stream.Dispose();
                        Roll();
                        using var fresh = new FileStream(FilePath, FileMode.Append, FileAccess.Write,
                            FileShare.ReadWrite | FileShare.Delete);
                        fresh.Write(bytes);
                        return;
                    }

                    stream.Write(bytes);
                });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A log that cannot be written must never take the wallpaper down.
            }
        }
    }

    /// <summary>Runs <paramref name="step"/> holding the cross-process mutex (or without it after the wait).</summary>
    private static void Exclusive(Action step)
    {
        var held = false;
        try
        {
            held = _mutex!.WaitOne(MutexWait);
        }
        catch (AbandonedMutexException)
        {
            // A process died holding it (a crash mid-line): the mutex is ours now.
            held = true;
        }

        try
        {
            step();
        }
        finally
        {
            if (held)
            {
                _mutex!.ReleaseMutex();
            }
        }
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
