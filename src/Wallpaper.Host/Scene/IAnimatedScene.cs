using BitcoinWallpaper.Host.Settings;

namespace BitcoinWallpaper.Host.Scene;

/// <summary>
/// A scene that changes by itself (D-10): the host starts it once its window exists, reads its sources on the
/// host thread every <see cref="PollInterval"/>, and presents one frame per display refresh while
/// <see cref="NeedsFrame"/> is true and none otherwise. Every member is called on the host thread.
/// </summary>
internal interface IAnimatedScene : IWallpaperScene, IDisposable
{
    /// <summary>How often <see cref="Poll"/> runs.</summary>
    TimeSpan PollInterval { get; }

    /// <summary>A tween runs: the host presents at the next display refresh.</summary>
    bool NeedsFrame { get; }

    /// <summary>The host window exists; frame and pause requests go to <paramref name="host"/> from now on.</summary>
    void Start(WallpaperHost host);

    /// <summary>Reads the sources (the market data client); a change asks the host for a frame.</summary>
    void Poll();

    /// <summary>Windows reported the display on (dimmed counts as on) or off (D-15); off pauses like the pause monitor.</summary>
    void SetDisplayOn(bool displayOn);

    /// <summary>Turned off from the tray (D-16): the scene's background work (the pause monitor) stops (#23).</summary>
    void Suspend();

    /// <summary>Turned on again after <see cref="Suspend"/>: the background work runs again.</summary>
    void Resume();

    /// <summary>What the tray tooltip shows while the wallpaper is on (D-26), as of the last <see cref="Poll"/>.</summary>
    TrayStatus TrayStatus { get; }
}
