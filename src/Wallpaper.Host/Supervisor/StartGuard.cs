using System.Runtime.InteropServices;

namespace BitcoinWallpaper.Host.Supervisor;

/// <summary>
/// The supervisor's <c>canStart</c>: no child while Windows is ending the session (SM_SHUTTINGDOWN), nor once the exe
/// is gone (an uninstall) or is no longer the file the supervisor started from (a Velopack update swapped it; Velopack
/// starts the new version itself).
/// </summary>
internal sealed partial class StartGuard(string exePath)
{
    private const int SM_SHUTTINGDOWN = 0x2000;

    private readonly (long Length, DateTime Written)? _started = Identity(exePath);

    public bool CanStart()
    {
        if (GetSystemMetrics(SM_SHUTTINGDOWN) != 0)
        {
            Log.Info("Windows is ending the session");
            return false;
        }

        var now = Identity(exePath);
        if (now is null)
        {
            Log.Info($"the exe is gone: {exePath}");
            return false;
        }

        if (now != _started)
        {
            Log.Info($"the exe was replaced (an update): {exePath}");
            return false;
        }

        return true;
    }

    private static (long Length, DateTime Written)? Identity(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? (file.Length, file.LastWriteTimeUtc) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);
}
