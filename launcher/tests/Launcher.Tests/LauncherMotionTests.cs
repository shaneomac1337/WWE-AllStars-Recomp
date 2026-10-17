using WweAllStars.Launcher.Core;

namespace WweAllStars.Launcher.Tests;

public sealed class LauncherMotionTests
{
    [Fact]
    public void Motion_runs_only_when_focused_and_no_game_runs()
    {
        Assert.True(LauncherMotion.Allowed(focused: true, minimized: false, gameRunning: false, reduceEffects: false));
    }

    [Theory]
    [InlineData(true, false, true)]   // the game runs
    [InlineData(false, false, false)] // another window has focus
    [InlineData(true, true, false)]   // minimized
    [InlineData(false, true, true)]   // all at once
    public void Motion_stops_while_the_game_runs_or_the_window_is_out_of_sight(
        bool focused, bool minimized, bool gameRunning)
    {
        Assert.False(LauncherMotion.Allowed(focused, minimized, gameRunning, reduceEffects: false));
    }

    [Fact]
    public void Reduced_effects_stop_the_motion_even_in_full_view()
    {
        Assert.False(LauncherMotion.Allowed(focused: true, minimized: false, gameRunning: false, reduceEffects: true));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]   // the saved preference
    [InlineData(false, true, true)]   // --reduce-effects, for a launcher that freezes before its settings open
    [InlineData(true, true, true)]
    public void Effects_are_reduced_by_the_preference_or_the_switch(bool preference, bool commandLine, bool reduced)
    {
        Assert.Equal(reduced, LauncherMotion.EffectsReduced(preference, commandLine));
    }
}
