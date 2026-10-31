using System.Runtime.InteropServices;

namespace BitcoinWallpaper.Host.Supervisor;

/// <summary>
/// The wallpaper child's side of D-24: an unhandled exception is written to the log (type, message, stack) and the
/// process ends with <see cref="RestartPolicy.CrashExitCode"/>, so the supervisor restarts it.
/// </summary>
internal static partial class CrashHandler
{
    public static readonly TimeSpan CrashTestDelay = TimeSpan.FromSeconds(8);

    public static void Install() => AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        Die($"an unhandled exception on thread {Environment.CurrentManagedThreadId}", e.ExceptionObject);

    /// <summary>An exception that escaped the wallpaper's main code: logged like an unhandled one.</summary>
    public static int Escaped(Exception e)
    {
        Log.Error($"crash (an exception escaped Main): {e}");
        return RestartPolicy.CrashExitCode;
    }

    /// <summary>--crash-test: throws an unhandled exception on a background thread after <see cref="CrashTestDelay"/>.</summary>
    public static void StartCrashTest()
    {
        Log.Warn($"crash test: an unhandled exception on a background thread in {CrashTestDelay.TotalSeconds:0} s");
        var thread = new Thread(() =>
        {
            Thread.Sleep(CrashTestDelay);
            throw new InvalidOperationException("--crash-test: a deliberate unhandled exception on a background thread");
        })
        {
            IsBackground = true,
            Name = "crash test",
        };
        thread.Start();
    }

    private static void Die(string what, object error)
    {
        Log.Error($"crash ({what}): {error}");
        Log.Close();

        // Ends now with our code; the CLR would end with 0xE0434352 after its own report.
        TerminateProcess(GetCurrentProcess(), RestartPolicy.CrashExitCode);
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, int exitCode);
}
