using BitcoinWallpaper.Chart.Core.Crosshair;

namespace BitcoinWallpaper.Host.Crosshair;

/// <summary>
/// The frame-request rule of the crosshair (D-35; D-10: nothing is drawn while nothing moves). Fed with the state the
/// host computed each time it looked at the cursor, it answers whether the picture changed and a frame is needed.
/// </summary>
public sealed class CrosshairFrameGate
{
    private Key? _last;

    /// <summary>True when what would be drawn differs from the last state: hidden / shown, another candle slot, another
    /// pixel column (floor X), another pixel row (floor Y), or a price present / absent (the volume pane). The first
    /// call compares with "hidden".</summary>
    public bool Update(CrosshairState? state)
    {
        Key? key = state is null
            ? null
            : new Key(state.CandleIndex, Math.Floor(state.X), Math.Floor(state.Y), state.Price is not null);
        if (key == _last)
        {
            return false;
        }

        _last = key;
        return true;
    }

    /// <summary>What a frame shows of the crosshair: the candle (its values and its highlight), the lines' pixel and
    /// whether the price tag is there.</summary>
    private readonly record struct Key(int CandleIndex, double Column, double Row, bool HasPrice);
}
