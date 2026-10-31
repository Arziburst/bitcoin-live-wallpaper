namespace BitcoinWallpaper.Host.Install;

/// <summary>"First run done" marker; survives restarts. Production: <see cref="FileFirstRunMarker"/>.</summary>
public interface IFirstRunMarker
{
    bool IsSet { get; }

    void Set();

    /// <summary>No-op when not set.</summary>
    void Clear();
}
