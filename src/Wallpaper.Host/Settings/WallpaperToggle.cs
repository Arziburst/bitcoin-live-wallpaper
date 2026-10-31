using System.Text.Json;

namespace BitcoinWallpaper.Host.Settings;

public enum WallpaperState
{
    On,
    Off,
}

/// <summary>What the toggle asks of the host. Production: the host window, the market data feed.</summary>
public interface IWallpaperControl
{
    /// <summary>Puts our layer behind the icons again.</summary>
    void AttachLayer();

    /// <summary>Removes our layer: the normal Windows wallpaper shows.</summary>
    void DetachLayer();

    /// <summary>(Re)connects the market data client; it reloads the history.</summary>
    void StartMarketData();

    /// <summary>Stops the client: socket closed, no REST polling.</summary>
    void StopMarketData();

    /// <summary>Draws one frame.</summary>
    void RequestFrame();
}

/// <summary>
/// D-16: the wallpaper on/off switch of the tray. Off detaches our layer and stops the data client, the process stays
/// in the tray; On attaches, reloads and draws one frame. The state persists in the settings store; a missing,
/// unreadable or corrupt store means On.
/// </summary>
public sealed class WallpaperToggle(ISettingsStore store, IWallpaperControl control)
{
    private const string Key = "wallpaper";

    public WallpaperState State { get; private set; } = WallpaperState.On;

    /// <summary>App start (and the launch at sign-in): a persisted Off stays off and touches nothing.</summary>
    public void Start()
    {
        if (ReadPersisted() == WallpaperState.Off)
        {
            State = WallpaperState.Off;
            return;
        }

        State = WallpaperState.On;
        On();
    }

    /// <summary>D-17: a start by the user turns the wallpaper on whatever was persisted.</summary>
    public void StartOn()
    {
        State = WallpaperState.On;
        On();
        Save();
    }

    public void TurnOff()
    {
        if (State == WallpaperState.Off)
        {
            return;
        }

        State = WallpaperState.Off;
        control.DetachLayer();
        control.StopMarketData();
        Save();
    }

    public void TurnOn()
    {
        if (State == WallpaperState.On)
        {
            return;
        }

        State = WallpaperState.On;
        On();
        Save();
    }

    public void Toggle()
    {
        if (State == WallpaperState.On)
        {
            TurnOff();
        }
        else
        {
            TurnOn();
        }
    }

    private void On()
    {
        control.AttachLayer();
        control.StartMarketData();
        control.RequestFrame();
    }

    private WallpaperState ReadPersisted()
    {
        try
        {
            var text = store.Read();
            if (string.IsNullOrWhiteSpace(text))
            {
                return WallpaperState.On;
            }

            using var json = JsonDocument.Parse(text);
            return json.RootElement.ValueKind == JsonValueKind.Object
                   && json.RootElement.TryGetProperty(Key, out var value)
                   && value.ValueKind == JsonValueKind.String
                   && value.GetString() == "off"
                ? WallpaperState.Off
                : WallpaperState.On;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"settings: unreadable ({e.Message}); the wallpaper starts on");
            return WallpaperState.On;
        }
    }

    private void Save()
    {
        var text = $"{{\"{Key}\": \"{(State == WallpaperState.Off ? "off" : "on")}\"}}";
        try
        {
            store.Write(text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"settings: saving the wallpaper {State} failed: {e.Message}");
        }
    }
}
