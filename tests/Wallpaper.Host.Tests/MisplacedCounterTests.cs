namespace BitcoinWallpaper.Host.Tests;

// Contract 22.2 (issue #23), criterion 1: the count of re-placement tries of a misplaced wallpaper window is a
// pure public type the host uses in WallpaperHost.Check instead of the _misplacedTries field. Within one
// episode (consecutive misplaced observations) at most 3 tries; an observation of the window back in place
// ends the episode, so a later misplacement gets its 3 tries again.
//
// API SKETCH (written before the code, #237). In src/Wallpaper.Host/ (new file, e.g. MisplacedCounter.cs),
// namespace BitcoinWallpaper.Host. No Win32, no clock, no logging:
//
//   public sealed class MisplacedCounter
//   {
//       public const int MaxTries = 3;                   // WallpaperHost.MisplacedRetries becomes this
//
//       public int Tries { get; }                        // tries made in the current episode, 0..MaxTries
//
//       // The window was observed misplaced: true and Tries goes up by one while Tries < MaxTries (the host
//       // re-places the window and logs "try {Tries} of {MaxTries}"); false and nothing changes otherwise.
//       public bool TryNext();
//
//       // The window was observed in place: the episode is over, Tries = 0.
//       public void ObservedInPlace();
//
//       // The host's other resets (attach, re-layout, turned off): Tries = 0.
//       public void Reset();
//   }
//
// In Check, the "placement == _placement && !IsAt" branch calls TryNext(); the "placement == _placement && IsAt"
// outcome calls ObservedInPlace(); the attach, re-layout and detach places call Reset().
public class MisplacedCounterTests
{
    [Fact]
    public void A_new_counter_has_made_no_tries_and_the_limit_is_three()
    {
        var counter = new MisplacedCounter();

        Assert.Equal(0, counter.Tries);
        Assert.Equal(3, MisplacedCounter.MaxTries);
    }

    [Fact]
    public void Within_one_episode_three_tries_are_allowed_and_the_fourth_is_refused()
    {
        var counter = new MisplacedCounter();

        Assert.True(counter.TryNext());
        Assert.Equal(1, counter.Tries);
        Assert.True(counter.TryNext());
        Assert.Equal(2, counter.Tries);
        Assert.True(counter.TryNext());
        Assert.Equal(3, counter.Tries);

        Assert.False(counter.TryNext());
        Assert.Equal(3, counter.Tries);
    }

    [Fact]
    public void A_refused_try_stays_refused_for_the_rest_of_the_episode()
    {
        var counter = new MisplacedCounter();
        for (var i = 0; i < MisplacedCounter.MaxTries; i++)
        {
            Assert.True(counter.TryNext());
        }

        for (var i = 0; i < 100; i++)
        {
            Assert.False(counter.TryNext());
        }

        Assert.Equal(MisplacedCounter.MaxTries, counter.Tries);
    }

    [Fact]
    public void Observed_in_place_ends_the_episode_and_the_next_one_gets_three_tries_again()
    {
        var counter = new MisplacedCounter();
        while (counter.TryNext())
        {
        }

        Assert.False(counter.TryNext());

        counter.ObservedInPlace();

        Assert.Equal(0, counter.Tries);
        Assert.True(counter.TryNext());
        Assert.True(counter.TryNext());
        Assert.True(counter.TryNext());
        Assert.False(counter.TryNext());
    }

    [Fact]
    public void Observed_in_place_in_the_middle_of_an_episode_gives_a_full_budget_again()
    {
        var counter = new MisplacedCounter();
        Assert.True(counter.TryNext());
        Assert.True(counter.TryNext());

        counter.ObservedInPlace();

        Assert.Equal(0, counter.Tries);
        for (var i = 0; i < MisplacedCounter.MaxTries; i++)
        {
            Assert.True(counter.TryNext(), $"try {i + 1} of the new episode was refused");
        }

        Assert.False(counter.TryNext());
    }

    [Fact]
    public void Observed_in_place_without_tries_changes_nothing()
    {
        var counter = new MisplacedCounter();

        counter.ObservedInPlace();
        counter.ObservedInPlace();

        Assert.Equal(0, counter.Tries);
        Assert.True(counter.TryNext());
        Assert.Equal(1, counter.Tries);
    }

    [Fact]
    public void Reset_gives_three_tries_again_after_the_budget_is_used_up()
    {
        var counter = new MisplacedCounter();
        while (counter.TryNext())
        {
        }

        counter.Reset();

        Assert.Equal(0, counter.Tries);
        for (var i = 0; i < MisplacedCounter.MaxTries; i++)
        {
            Assert.True(counter.TryNext());
        }

        Assert.False(counter.TryNext());
    }

    [Fact]
    public void Many_episodes_in_a_row_each_get_exactly_three_tries()
    {
        var counter = new MisplacedCounter();

        for (var episode = 0; episode < 20; episode++)
        {
            var granted = 0;
            for (var observation = 0; observation < 10; observation++)
            {
                if (counter.TryNext())
                {
                    granted++;
                }
            }

            Assert.Equal(3, granted);
            counter.ObservedInPlace();
        }
    }

    [Fact]
    public void Two_counters_do_not_share_their_tries()
    {
        var a = new MisplacedCounter();
        var b = new MisplacedCounter();
        while (a.TryNext())
        {
        }

        Assert.Equal(0, b.Tries);
        Assert.True(b.TryNext());
    }
}
