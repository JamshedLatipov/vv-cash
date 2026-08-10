using System;
using Avalonia;

namespace VvCash.Services.Rendering;

/// <summary>Decides how Avalonia draws, before the first window exists.
///
/// The default <c>UsePlatformDetect()</c> chain asks for ANGLE over Direct3D and for a
/// modern compositor, and on Windows 7 both of those requests are answered badly rather
/// than refused: the window is created, the app runs, nothing throws — and the register
/// shows a black rectangle across the whole screen, because the app starts full-screen
/// and the frame is simply never painted. That failure mode is why this is decided from
/// the OS version up front instead of being left to fall back on its own.</summary>
public static class RenderingSelector
{
    /// <summary>Set to <c>software</c> or <c>gpu</c> to overrule <see cref="Select"/>.
    /// This exists for the register that fails in the field on hardware nobody here can
    /// reproduce — a cheap POS box whose GPU driver renders nothing on an OS new enough
    /// that the version check below passes it. Setting one environment variable beats
    /// cutting a release to find out.</summary>
    public const string OverrideVariable = "VVCASH_RENDER";

    /// <summary>Returns the options to apply, or <c>null</c> to leave Avalonia's own
    /// platform detection alone.
    ///
    /// An unrecognised override value falls through to automatic rather than throwing.
    /// This runs before any UI exists, so a typo in the variable would otherwise take
    /// the register down with no window in which to report why.</summary>
    public static Win32PlatformOptions? Select(string? overrideValue, OperatingSystem os)
    {
        switch (overrideValue?.Trim().ToLowerInvariant())
        {
            case "software": return SoftwareOptions();
            case "gpu": return null;
        }

        // Everything before Windows 10 — 7, 8 and 8.1. WinUIComposition needs 1803 and
        // DirectComposition needs 8, so on 7 there is no GPU path worth attempting. 8.x
        // is swept in with it deliberately: it is equally out of support, equally rare
        // here, and not somewhere to spend a debugging trip discovering that its GPU
        // path is only mostly fine.
        if (os.Platform == PlatformID.Win32NT && os.Version.Major < 10) return SoftwareOptions();

        return null;
    }

    private static Win32PlatformOptions SoftwareOptions() => new()
    {
        RenderingMode = new[] { Win32RenderingMode.Software },

        // RedirectionSurface is the only one of the four composition modes that predates
        // Windows 8. Leaving the default list in place would have Avalonia try the other
        // three first, which is where the black screen came from.
        CompositionMode = new[] { Win32CompositionMode.RedirectionSurface },
    };
}
