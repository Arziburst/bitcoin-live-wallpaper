namespace DesktopSpike;

/// <summary>Writes every line to the console and to one log file per run in %TEMP%\DesktopSpike.</summary>
internal static class Log
{
    private static readonly Lock Gate = new();
    private static StreamWriter? _file;

    public static string Directory { get; } = Path.Combine(Path.GetTempPath(), "DesktopSpike");

    public static string FilePath { get; } = Path.Combine(Directory, $"run-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    public static void Open()
    {
        System.IO.Directory.CreateDirectory(Directory);
        _file = new StreamWriter(FilePath, append: true) { AutoFlush = true };
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
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level,-5} {message}";
        lock (Gate)
        {
            Console.WriteLine(line);
            _file?.WriteLine(line);
        }
    }
}
