using BitcoinWallpaper.Host.LockScreenChart;

namespace BitcoinWallpaper.Host.Tests;

// Contract 42, criterion 1 (D-34 step 2, issue #42): when the lock screen snapshot is refreshed. A pure policy with
// no clock of its own: every call passes `now`.
//
// API SKETCH (written before the code, #237). Namespace BitcoinWallpaper.Host.LockScreenChart,
// src/Wallpaper.Host/LockScreenChart/LockScreenRefreshPolicy.cs. Everything public.
//
//   public sealed class LockScreenRefreshPolicy
//   {
//       public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
//
//       // Both default true (the tray item defaults on; the wallpaper starts On).
//       public bool FeatureOn { get; set; }        // false -> true: forgets the last refresh and the last failure,
//                                                  // so the next ShouldRefresh with live data is true at once.
//       public bool WallpaperOn { get; set; }      // Off: never refresh. Setting it does not forget anything.
//
//       // Pure query (changes nothing). `hasLiveData`: the feed is live (not Connecting/Stale).
//       // `candleOpen`: open time of the current (newest) candle, null before the first data.
//       // False when !FeatureOn, !WallpaperOn or !hasLiveData. Otherwise true when
//       //   - nothing was refreshed since start / since the feature was turned on (the first live data), or
//       //   - candleOpen differs from the candle open the last successful refresh saw (a new candle opened), or
//       //   - now - lastSuccess >= Interval.
//       // After a failure it is false until now - lastFailure >= Interval, whatever else is due (at most one retry
//       // per 30 min, also on a new candle).
//       public bool ShouldRefresh(DateTimeOffset now, bool hasLiveData, DateTimeOffset? candleOpen);
//
//       public void Refreshed(DateTimeOffset now, DateTimeOffset? candleOpen);  // success; clears the failure
//       public void Failed(DateTimeOffset now);
//   }
public sealed class LockScreenRefreshPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Candle1 = T0.AddMinutes(-3);
    private static readonly DateTimeOffset Candle2 = T0.AddMinutes(2);
    private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);

    private static LockScreenRefreshPolicy RefreshedAtT0()
    {
        var policy = new LockScreenRefreshPolicy();
        Assert.True(policy.ShouldRefresh(T0, true, Candle1));
        policy.Refreshed(T0, Candle1);
        return policy;
    }

    [Fact]
    public void Defaults_are_feature_on_wallpaper_on_and_interval_30_minutes()
    {
        var policy = new LockScreenRefreshPolicy();
        Assert.True(policy.FeatureOn);
        Assert.True(policy.WallpaperOn);
        Assert.Equal(TimeSpan.FromMinutes(30), LockScreenRefreshPolicy.Interval);
    }

    [Fact]
    public void The_first_live_data_after_start_refreshes_but_not_before()
    {
        var policy = new LockScreenRefreshPolicy();

        Assert.False(policy.ShouldRefresh(T0, hasLiveData: false, candleOpen: null));
        Assert.False(policy.ShouldRefresh(T0 + Min, hasLiveData: false, candleOpen: Candle1));
        Assert.True(policy.ShouldRefresh(T0 + 2 * Min, hasLiveData: true, candleOpen: Candle1));
    }

    [Fact]
    public void Asking_changes_nothing_until_refreshed_is_reported()
    {
        var policy = new LockScreenRefreshPolicy();

        Assert.True(policy.ShouldRefresh(T0, true, Candle1));
        Assert.True(policy.ShouldRefresh(T0, true, Candle1));
    }

    [Fact]
    public void Nothing_to_do_right_after_a_refresh_on_the_same_candle()
    {
        var policy = RefreshedAtT0();

        Assert.False(policy.ShouldRefresh(T0, true, Candle1));
        Assert.False(policy.ShouldRefresh(T0 + 10 * Min, true, Candle1));
        Assert.False(policy.ShouldRefresh(T0 + 29 * Min + TimeSpan.FromSeconds(59), true, Candle1));
    }

    [Fact]
    public void A_new_candle_refreshes_at_once()
    {
        var policy = RefreshedAtT0();

        Assert.True(policy.ShouldRefresh(T0 + 2 * Min, true, Candle2));
        policy.Refreshed(T0 + 2 * Min, Candle2);
        Assert.False(policy.ShouldRefresh(T0 + 3 * Min, true, Candle2));
    }

    [Fact]
    public void Thirty_minutes_after_the_last_refresh_it_refreshes_again()
    {
        var policy = RefreshedAtT0();

        Assert.True(policy.ShouldRefresh(T0 + 30 * Min, true, Candle1));
        policy.Refreshed(T0 + 30 * Min, Candle1);
        Assert.False(policy.ShouldRefresh(T0 + 31 * Min, true, Candle1));
        Assert.False(policy.ShouldRefresh(T0 + 59 * Min, true, Candle1));
        Assert.True(policy.ShouldRefresh(T0 + 60 * Min, true, Candle1));
    }

    [Fact]
    public void The_30_minutes_count_from_the_last_refresh_not_from_the_start()
    {
        var policy = RefreshedAtT0();
        policy.Refreshed(T0 + 20 * Min, Candle1);   // e.g. refreshed again by a new candle meanwhile

        Assert.False(policy.ShouldRefresh(T0 + 30 * Min, true, Candle1));
        Assert.True(policy.ShouldRefresh(T0 + 50 * Min, true, Candle1));
    }

    [Fact]
    public void Offline_data_keeps_the_last_image_even_when_due()
    {
        var policy = RefreshedAtT0();

        Assert.False(policy.ShouldRefresh(T0 + 45 * Min, hasLiveData: false, candleOpen: Candle1));
        Assert.False(policy.ShouldRefresh(T0 + 45 * Min, hasLiveData: false, candleOpen: Candle2));
        Assert.True(policy.ShouldRefresh(T0 + 46 * Min, hasLiveData: true, candleOpen: Candle2));
    }

    [Fact]
    public void Never_while_the_wallpaper_is_off()
    {
        var policy = new LockScreenRefreshPolicy { WallpaperOn = false };

        Assert.False(policy.ShouldRefresh(T0, true, Candle1));   // the first data
        policy.WallpaperOn = true;
        Assert.True(policy.ShouldRefresh(T0, true, Candle1));
        policy.Refreshed(T0, Candle1);
        policy.WallpaperOn = false;
        Assert.False(policy.ShouldRefresh(T0 + 2 * Min, true, Candle2));    // a new candle
        Assert.False(policy.ShouldRefresh(T0 + 40 * Min, true, Candle2));   // 30 minutes passed
    }

    [Fact]
    public void Never_while_the_feature_is_off()
    {
        var policy = new LockScreenRefreshPolicy { FeatureOn = false };

        Assert.False(policy.ShouldRefresh(T0, true, Candle1));
        Assert.False(policy.ShouldRefresh(T0 + 2 * Min, true, Candle2));
        Assert.False(policy.ShouldRefresh(T0 + 90 * Min, true, Candle2));
    }

    [Fact]
    public void Turning_the_feature_on_refreshes_at_once_when_data_is_live()
    {
        var policy = RefreshedAtT0();
        policy.FeatureOn = false;
        Assert.False(policy.ShouldRefresh(T0 + 5 * Min, true, Candle1));

        policy.FeatureOn = true;

        Assert.False(policy.ShouldRefresh(T0 + 5 * Min, hasLiveData: false, candleOpen: Candle1));
        Assert.True(policy.ShouldRefresh(T0 + 5 * Min, hasLiveData: true, candleOpen: Candle1));
    }

    [Fact]
    public void Turning_the_feature_on_forgets_a_recent_failure()
    {
        var policy = new LockScreenRefreshPolicy();
        policy.Failed(T0);
        Assert.False(policy.ShouldRefresh(T0 + Min, true, Candle1));

        policy.FeatureOn = false;
        policy.FeatureOn = true;

        Assert.True(policy.ShouldRefresh(T0 + 2 * Min, true, Candle1));
    }

    [Fact]
    public void After_a_failure_at_most_one_retry_per_30_minutes()
    {
        var policy = new LockScreenRefreshPolicy();
        Assert.True(policy.ShouldRefresh(T0, true, Candle1));
        policy.Failed(T0);

        Assert.False(policy.ShouldRefresh(T0 + Min, true, Candle1));
        Assert.False(policy.ShouldRefresh(T0 + 29 * Min, true, Candle1));
        Assert.True(policy.ShouldRefresh(T0 + 30 * Min, true, Candle1));

        policy.Failed(T0 + 30 * Min);
        Assert.False(policy.ShouldRefresh(T0 + 31 * Min, true, Candle1));
        Assert.False(policy.ShouldRefresh(T0 + 59 * Min, true, Candle1));
        Assert.True(policy.ShouldRefresh(T0 + 60 * Min, true, Candle1));
    }

    [Fact]
    public void A_new_candle_does_not_beat_the_failure_back_off()
    {
        var policy = RefreshedAtT0();
        policy.Failed(T0 + 2 * Min);   // the refresh for Candle2 failed

        Assert.False(policy.ShouldRefresh(T0 + 3 * Min, true, Candle2));
        Assert.True(policy.ShouldRefresh(T0 + 32 * Min, true, Candle2));
    }

    [Fact]
    public void A_success_after_a_failure_clears_the_back_off()
    {
        var policy = new LockScreenRefreshPolicy();
        policy.Failed(T0);
        Assert.True(policy.ShouldRefresh(T0 + 30 * Min, true, Candle1));
        policy.Refreshed(T0 + 30 * Min, Candle1);

        Assert.True(policy.ShouldRefresh(T0 + 31 * Min, true, Candle2));   // new candle: no back-off any more
    }

    [Fact]
    public void A_failure_before_any_success_still_refreshes_for_data_after_the_back_off_even_without_a_new_candle()
    {
        var policy = new LockScreenRefreshPolicy();
        policy.Failed(T0);

        Assert.False(policy.ShouldRefresh(T0 + 10 * Min, true, null));
        Assert.True(policy.ShouldRefresh(T0 + 30 * Min, true, null));
    }
}