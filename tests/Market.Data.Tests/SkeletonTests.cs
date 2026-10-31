using BitcoinWallpaper.Market.Data;

namespace BitcoinWallpaper.Market.Data.Tests;

public class SkeletonTests
{
    [Fact]
    public void Assembly_is_referenced()
    {
        Assert.Equal("Market.Data", AssemblyMarker.Name);
    }
}
