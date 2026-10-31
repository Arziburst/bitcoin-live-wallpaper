using DesktopSpike;

namespace DesktopSpike.Tests;

public class DesktopLayoutDetectionTests
{
    // Real ex-styles: Progman on 23H2 (WS_EX_TOOLWINDOW) and on 24H2+ (plus WS_EX_NOREDIRECTIONBITMAP);
    // DefView on 24H2+ is WS_EX_LAYERED.
    private const long LegacyProgmanExStyle = 0x0000_0080;
    private const long RaisedProgmanExStyle = 0x0020_0080;
    private const long LayeredDefViewExStyle = 0x0008_0000;

    [Fact]
    public void Progman_without_noredirectionbitmap_is_legacy()
    {
        var result = DesktopLayoutDetector.Detect(new DesktopSnapshot(true, LegacyProgmanExStyle, DefViewHost.TopLevelWorkerW, 0));

        Assert.Equal(DesktopLayout.Legacy, result.Layout);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Legacy_with_defview_still_in_progman_has_no_warning()
    {
        var result = DesktopLayoutDetector.Detect(new DesktopSnapshot(true, LegacyProgmanExStyle, DefViewHost.Progman, 0));

        Assert.Equal(DesktopLayout.Legacy, result.Layout);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Progman_with_noredirectionbitmap_and_layered_defview_child_is_raised()
    {
        var result = DesktopLayoutDetector.Detect(
            new DesktopSnapshot(true, RaisedProgmanExStyle, DefViewHost.Progman, LayeredDefViewExStyle));

        Assert.Equal(DesktopLayout.Raised, result.Layout);
        Assert.Empty(result.Warnings);
        Assert.Contains("WS_EX_NOREDIRECTIONBITMAP", result.Reason);
    }

    [Theory]
    [InlineData(0x0020_0000L)]
    [InlineData(0x0820_0088L)]
    [InlineData(0x7FFF_FFFFL)]
    public void Noredirectionbitmap_bit_alone_decides_raised(long progmanExStyle)
    {
        var result = DesktopLayoutDetector.Detect(
            new DesktopSnapshot(true, progmanExStyle, DefViewHost.Progman, LayeredDefViewExStyle));

        Assert.Equal(DesktopLayout.Raised, result.Layout);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(0x0008_0080L)]
    [InlineData(0x7FDF_FFFFL)]
    public void Any_other_style_bits_without_noredirectionbitmap_are_legacy(long progmanExStyle)
    {
        var result = DesktopLayoutDetector.Detect(
            new DesktopSnapshot(true, progmanExStyle, DefViewHost.Progman, LayeredDefViewExStyle));

        Assert.Equal(DesktopLayout.Legacy, result.Layout);
    }

    [Fact]
    public void No_progman_is_unknown()
    {
        var result = DesktopLayoutDetector.Detect(new DesktopSnapshot(false, RaisedProgmanExStyle, DefViewHost.NotFound, 0));

        Assert.Equal(DesktopLayout.Unknown, result.Layout);
    }

    [Fact]
    public void Raised_with_defview_in_a_top_level_workerw_warns()
    {
        var result = DesktopLayoutDetector.Detect(
            new DesktopSnapshot(true, RaisedProgmanExStyle, DefViewHost.TopLevelWorkerW, LayeredDefViewExStyle));

        Assert.Equal(DesktopLayout.Raised, result.Layout);
        Assert.Contains(result.Warnings, w => w.Contains("top-level WorkerW"));
    }

    [Fact]
    public void Raised_with_unlayered_defview_warns()
    {
        var result = DesktopLayoutDetector.Detect(new DesktopSnapshot(true, RaisedProgmanExStyle, DefViewHost.Progman, 0));

        Assert.Equal(DesktopLayout.Raised, result.Layout);
        Assert.Contains(result.Warnings, w => w.Contains("not WS_EX_LAYERED"));
    }

    [Theory]
    [InlineData(LegacyProgmanExStyle)]
    [InlineData(RaisedProgmanExStyle)]
    public void Missing_defview_warns(long progmanExStyle)
    {
        var result = DesktopLayoutDetector.Detect(new DesktopSnapshot(true, progmanExStyle, DefViewHost.NotFound, 0));

        Assert.Contains(result.Warnings, w => w.Contains("SHELLDLL_DefView not found"));
    }

    [Fact]
    public void Legacy_workerw_is_the_first_workerw_after_the_defview_host()
    {
        WindowEntry[] topLevel =
        [
            new(0x10, "Shell_TrayWnd"),
            new(0x20, "WorkerW"),
            new(0x30, "WorkerW", HasDefViewChild: true),
            new(0x40, "Notepad"),
            new(0x50, "WorkerW"),
            new(0x60, "WorkerW"),
            new(0x70, "Progman"),
        ];

        Assert.Equal((nint)0x50, DesktopLayoutDetector.FindLegacyWorkerW(topLevel));
    }

    [Fact]
    public void Legacy_workerw_after_progman_hosting_defview()
    {
        WindowEntry[] topLevel =
        [
            new(0x20, "WorkerW"),
            new(0x70, "Progman", HasDefViewChild: true),
            new(0x80, "WorkerW"),
        ];

        Assert.Equal((nint)0x80, DesktopLayoutDetector.FindLegacyWorkerW(topLevel));
    }

    [Fact]
    public void Legacy_workerw_is_zero_without_a_workerw_after_the_host()
    {
        WindowEntry[] topLevel =
        [
            new(0x20, "WorkerW"),
            new(0x30, "WorkerW", HasDefViewChild: true),
            new(0x70, "Progman"),
        ];

        Assert.Equal((nint)0, DesktopLayoutDetector.FindLegacyWorkerW(topLevel));
    }

    [Fact]
    public void Legacy_workerw_is_zero_without_a_defview_host()
    {
        WindowEntry[] topLevel = [new(0x20, "WorkerW"), new(0x70, "Progman")];

        Assert.Equal((nint)0, DesktopLayoutDetector.FindLegacyWorkerW(topLevel));
    }

    [Fact]
    public void Raised_targets_are_defview_and_workerw_children_of_progman()
    {
        WindowEntry[] children = [new(0x100, "SHELLDLL_DefView"), new(0x200, "WorkerW")];

        var targets = DesktopLayoutDetector.FindRaisedTargets(children);

        Assert.Equal(new RaisedAttachTargets(0x100, 0x200), targets);
    }

    [Fact]
    public void Raised_targets_without_workerw_keep_defview()
    {
        WindowEntry[] children = [new(0x100, "SHELLDLL_DefView")];

        Assert.Equal(new RaisedAttachTargets(0x100, 0), DesktopLayoutDetector.FindRaisedTargets(children));
    }

    [Fact]
    public void Raised_targets_are_null_without_defview()
    {
        WindowEntry[] children = [new(0x200, "WorkerW")];

        Assert.Null(DesktopLayoutDetector.FindRaisedTargets(children));
    }
}
