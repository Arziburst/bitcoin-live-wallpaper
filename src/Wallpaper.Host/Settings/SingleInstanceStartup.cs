using BitcoinWallpaper.Host.Install;

namespace BitcoinWallpaper.Host.Settings;

public enum LaunchKind
{
    /// <summary>A start by the user (a shortcut, the exe, the installer's first run).</summary>
    User,

    /// <summary>The launch at sign-in from the Run value.</summary>
    Autostart,
}

public static class Launch
{
    /// <summary>Autostart when the arguments carry <see cref="Autostart.AutostartArgument"/>; anything else is User.</summary>
    public static LaunchKind Parse(IReadOnlyList<string> args) =>
        args.Contains(Autostart.AutostartArgument, StringComparer.OrdinalIgnoreCase) ? LaunchKind.Autostart : LaunchKind.User;
}

/// <summary>One wallpaper per user session. Production: <see cref="WindowInstanceChannel"/>.</summary>
public interface IInstanceChannel
{
    /// <summary>True: this process is the only instance and becomes the primary.</summary>
    bool TryAcquirePrimary();

    /// <summary>From a second process to the primary.</summary>
    void SendTurnOn();

    /// <summary>In the primary, once per <see cref="SendTurnOn"/>, on the host's UI thread.</summary>
    event Action? TurnOnRequested;
}

/// <summary>
/// D-17: the primary starts the toggle (a user start turns the wallpaper on, the sign-in launch keeps the persisted
/// state) and answers later starts; a second process only signals the primary (a user start) and exits.
/// </summary>
public sealed class SingleInstanceStartup(IInstanceChannel channel, WallpaperToggle toggle)
{
    /// <summary>Null for the primary (the host runs next); the exit code for a second process.</summary>
    public int? Run(LaunchKind kind)
    {
        if (!channel.TryAcquirePrimary())
        {
            if (kind == LaunchKind.User)
            {
                channel.SendTurnOn();
            }

            return 0;
        }

        channel.TurnOnRequested += toggle.TurnOn;
        if (kind == LaunchKind.User)
        {
            toggle.StartOn();
        }
        else
        {
            toggle.Start();
        }

        return null;
    }
}
