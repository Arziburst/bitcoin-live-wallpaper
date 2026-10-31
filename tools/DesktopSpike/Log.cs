namespace DesktopSpike;

/// <summary>Writes every line to the console and to a log file in %TEMP%.</summary>
internal static class Log
{
    private static readonly Lock Gate = new();
    private static StreamWriter? _file;

    public static string FilePath { get; } = Path.Combine(Path.GetTempPath(), "DesktopSpike.log");

    public static void Open()
    {
        _file = new StreamWriter(FilePath, append: false) { AutoFlush = true };
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
