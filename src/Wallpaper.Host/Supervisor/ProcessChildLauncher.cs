using System.ComponentModel;
using System.Diagnostics;
using BitcoinWallpaper.Host.Install;
using BitcoinWallpaper.Host.Settings;

namespace BitcoinWallpaper.Host.Supervisor;

/// <summary>
/// The production <see cref="IChildLauncher"/>: the same exe with the hidden <see cref="ChildArgument"/> (the
/// wallpaper itself), "--autostart" for the sign-in launch and <see cref="CrashTestArgument"/> when asked for.
/// </summary>
internal sealed class ProcessChildLauncher(string exePath) : IChildLauncher
{
    /// <summary>Hidden: the process is the wallpaper child of a supervisor. Without it the exe is the supervisor.</summary>
    public const string ChildArgument = "--wallpaper-child";

    /// <summary>Hidden (no menu): the wallpaper child throws an unhandled exception a few seconds after start.</summary>
    public const string CrashTestArgument = "--crash-test";

    public static bool IsChild(IReadOnlyList<string> args) => args.Contains(ChildArgument, StringComparer.OrdinalIgnoreCase);

    public static bool IsCrashTest(IReadOnlyList<string> args) =>
        args.Contains(CrashTestArgument, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Arguments(ChildStart start)
    {
        var args = new List<string> { ChildArgument };
        if (start.Launch == LaunchKind.Autostart)
        {
            args.Add(Autostart.AutostartArgument);
        }

        if (start.CrashTest)
        {
            args.Add(CrashTestArgument);
        }

        return args;
    }

    public int Run(ChildStart start)
    {
        var args = Arguments(start);
        var info = new ProcessStartInfo(exePath) { UseShellExecute = false };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        Process? child;
        try
        {
            child = Process.Start(info);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Log.Error($"starting the wallpaper process failed: {e.Message}");
            return 1;
        }

        if (child is null)
        {
            Log.Error("starting the wallpaper process failed: no process");
            return 1;
        }

        using (child)
        {
            Log.Info($"wallpaper process {child.Id} started ({string.Join(' ', args)})");
            child.WaitForExit();
            return child.ExitCode;
        }
    }
}

internal sealed class SystemSupervisorClock : ISupervisorClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public void Wait(TimeSpan delay) => Thread.Sleep(delay);
}
