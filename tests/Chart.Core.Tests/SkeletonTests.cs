using BitcoinWallpaper.Chart.Core;

namespace BitcoinWallpaper.Chart.Core.Tests;

public class SkeletonTests
{
    [Fact]
    public void Assembly_is_referenced()
    {
        Assert.Equal("Chart.Core", AssemblyMarker.Name);
    }
}
