using System;
using System.Collections.Generic;
using Avalonia;

namespace VvCash.Services;

/// <summary>Where the customer-facing window goes, or that it should not exist.</summary>
/// <param name="Position">Top-left corner, in device pixels.</param>
/// <param name="ForcedOnSingleScreen">True only when the window was forced onto a machine
/// that has just one screen. The host reads this to make the window Topmost and modestly
/// sized: MainWindow is full-screen and Topmost, so a customer window merely placed beside
/// it on the same monitor would render behind it and be invisible. Always false in
/// production.</param>
public sealed record CustomerDisplayPlacement(PixelPoint Position, bool ForcedOnSingleScreen);

/// <summary>Decides whether this register gets a customer-facing window and where it lands,
/// before any such window exists.
///
/// Split out as a pure function for the same reason as
/// <see cref="VvCash.Services.Rendering.RenderingSelector"/>: the decision depends on the
/// machine (how many screens) and on an environment variable, neither of which a test can
/// arrange through a running Avalonia application. Without the override below, the whole
/// customer-display path is unreachable on a single-monitor development machine.</summary>
public static class CustomerDisplayPlacementSelector
{
    /// <summary>Set to <c>force</c> to get the window on a machine with one screen, or to
    /// <c>off</c> to silence a genuine customer display while debugging on the shop floor,
    /// on a register that really has two screens. Anything else — including a typo — falls
    /// through to the automatic decision rather than throwing: this runs before the first
    /// window exists, so there would be nowhere to report the error.</summary>
    public const string OverrideVariable = "VVCASH_CUSTOMER_DISPLAY";

    /// <summary>Returns the placement, or <c>null</c> when no window should be created.</summary>
    public static CustomerDisplayPlacement? Select(string? overrideValue, IReadOnlyList<PixelRect> screens)
    {
        var mode = overrideValue?.Trim().ToLowerInvariant();

        if (mode == "off") return null;
        if (screens.Count == 0) return null;

        // A real second screen always wins, override or not. "force" exists to make the
        // window EXIST on a one-screen machine, not to turn a genuine customer display into
        // a Topmost overlay on top of the POS.
        if (screens.Count > 1)
            return new CustomerDisplayPlacement(screens[1].Position, ForcedOnSingleScreen: false);

        return mode == "force"
            ? new CustomerDisplayPlacement(screens[0].Position, ForcedOnSingleScreen: true)
            : null;
    }
}
