using System;
using System.Collections.Generic;
using Avalonia;
using VvCash.Services;
using Xunit;

namespace VvCash.Tests;

public class CustomerDisplayPlacementSelectorTest
{
    private static IReadOnlyList<PixelRect> Screens(params PixelRect[] screens) => screens;

    private static readonly PixelRect Primary = new(0, 0, 1920, 1080);
    private static readonly PixelRect Secondary = new(1920, 0, 1280, 1024);
    private static readonly PixelRect Tertiary = new(3200, 0, 1024, 768);

    [Fact]
    public void SingleScreenWithNoOverride_MeansNoWindow()
    {
        // Today's production behaviour, pinned: a register with one monitor has nowhere to
        // put a customer-facing window, so none is built.
        Assert.Null(CustomerDisplayPlacementSelector.Select(null, Screens(Primary)));
    }

    [Fact]
    public void TwoScreens_PlacesItOnTheSecond_AndIsNotForced()
    {
        var placement = CustomerDisplayPlacementSelector.Select(null, Screens(Primary, Secondary));

        Assert.NotNull(placement);
        Assert.Equal(Secondary.Position, placement!.Position);
        Assert.False(placement.ForcedOnSingleScreen);
    }

    [Fact]
    public void ThreeScreens_StillPlacesItOnTheSecond_AndIsNotForced()
    {
        // Pins "the second monitor" against a maintainer narrowing screens.Count > 1 to
        // == 2 — plausible-looking, but wrong for any register with more than two screens,
        // and a lone two-screen test would let it through green.
        var placement = CustomerDisplayPlacementSelector.Select(null, Screens(Primary, Secondary, Tertiary));

        Assert.NotNull(placement);
        Assert.Equal(Secondary.Position, placement!.Position);
        Assert.False(placement.ForcedOnSingleScreen);
    }

    [Fact]
    public void ForceOnASingleScreen_PlacesItOnTheOnlyScreen_AndMarksItForced()
    {
        // The development escape hatch. ForcedOnSingleScreen is what makes the host raise
        // the window above the full-screen Topmost MainWindow — without it the window would
        // be created, shown, and completely invisible.
        var placement = CustomerDisplayPlacementSelector.Select("force", Screens(Primary));

        Assert.NotNull(placement);
        Assert.Equal(Primary.Position, placement!.Position);
        Assert.True(placement.ForcedOnSingleScreen);
    }

    [Fact]
    public void ForceOnTwoScreens_BehavesExactlyLikeAutomatic()
    {
        // The variable forces the window to EXIST, not to be a debugging overlay. On a real
        // two-screen register it already lands on its own screen, and making it Topmost over
        // the POS would be a regression.
        var placement = CustomerDisplayPlacementSelector.Select("force", Screens(Primary, Secondary));

        Assert.NotNull(placement);
        Assert.Equal(Secondary.Position, placement!.Position);
        Assert.False(placement.ForcedOnSingleScreen);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("OFF")]
    [InlineData("  Off  ")]
    public void Off_SuppressesTheWindowEvenOnATwoScreenRegister(string value)
    {
        // For silencing a real customer display while debugging on the shop floor. Case and
        // surrounding whitespace are tolerated, same as RenderingSelector's override.
        Assert.Null(CustomerDisplayPlacementSelector.Select(value, Screens(Primary, Secondary)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("yes-please")]
    public void AnUnrecognisedValueFallsThroughToAutomatic(string value)
    {
        // This runs before any window exists, so a typo — or an empty/blank value — must
        // not throw; there would be no UI in which to report it. Mirrors RenderingSelector's
        // own UnusableOverrideFallsBackToAutomatic, which pins the same three shapes.
        Assert.Null(CustomerDisplayPlacementSelector.Select(value, Screens(Primary)));

        var placement = CustomerDisplayPlacementSelector.Select(value, Screens(Primary, Secondary));
        Assert.NotNull(placement);
        Assert.Equal(Secondary.Position, placement!.Position);
    }

    [Fact]
    public void NoScreensAtAll_MeansNoWindow()
    {
        // Not reachable on a live system, but Select must still answer rather than index
        // off the end of an empty list before any UI exists to report the crash.
        Assert.Null(CustomerDisplayPlacementSelector.Select(null, Screens()));
        Assert.Null(CustomerDisplayPlacementSelector.Select("force", Screens()));
    }
}
