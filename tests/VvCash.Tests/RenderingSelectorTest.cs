using System;
using Avalonia;
using VvCash.Services.Rendering;
using Xunit;

namespace VvCash.Tests;

public class RenderingSelectorTest
{
    private static OperatingSystem Windows(int major, int minor, int build)
        => new(PlatformID.Win32NT, new Version(major, minor, build));

    private static void AssertSoftware(Win32PlatformOptions? options)
    {
        Assert.NotNull(options);
        Assert.Equal(new[] { Win32RenderingMode.Software }, options!.RenderingMode);

        // The composition mode matters as much as the rendering mode. Software drawing
        // paired with Avalonia's default composition list still tries WinUIComposition
        // and DirectComposition first, neither of which exists before Windows 8 - which
        // is the black screen this whole selector exists to prevent.
        Assert.Equal(new[] { Win32CompositionMode.RedirectionSurface }, options.CompositionMode);
    }

    [Theory]
    [InlineData(6, 1, 7601)]   // Windows 7 SP1
    [InlineData(6, 2, 9200)]   // Windows 8
    [InlineData(6, 3, 9600)]   // Windows 8.1
    public void PreWindows10FallsBackToSoftware(int major, int minor, int build)
        => AssertSoftware(RenderingSelector.Select(null, Windows(major, minor, build)));

    [Theory]
    [InlineData(10, 0, 19045)] // Windows 10 22H2
    [InlineData(10, 0, 26100)] // Windows 11 24H2
    public void Windows10AndLaterKeepsThePlatformDefault(int major, int minor, int build)
        => Assert.Null(RenderingSelector.Select(null, Windows(major, minor, build)));

    [Fact]
    public void NonWindowsKeepsThePlatformDefault()
    {
        // Win32PlatformOptions means nothing off Windows, and the desktop build also
        // targets Linux and macOS. Their kernel versions are small numbers that would
        // sail straight through a bare "major < 10" test.
        Assert.Null(RenderingSelector.Select(null, new OperatingSystem(PlatformID.Unix, new Version(6, 8))));
    }

    [Fact]
    public void OverrideForcesSoftwareOnAModernWindows()
    {
        // The reason the override exists: a register on a current Windows whose GPU
        // driver draws nothing, which the version check above has no way to detect.
        AssertSoftware(RenderingSelector.Select("software", Windows(10, 0, 19045)));
    }

    [Fact]
    public void OverrideCanForceGpuOnAnOldWindows()
    {
        // The escape hatch has to work in both directions. If the software path ever
        // turns out to be the wrong call on some Windows 7 machine, that is not a
        // situation to need a new build for.
        Assert.Null(RenderingSelector.Select("gpu", Windows(6, 1, 7601)));
    }

    [Theory]
    [InlineData("  SOFTWARE  ")]
    [InlineData("Software")]
    public void OverrideIgnoresCaseAndSurroundingSpace(string value)
    {
        // This arrives from an environment variable typed at a register by someone
        // reading it off a support call.
        AssertSoftware(RenderingSelector.Select(value, Windows(10, 0, 19045)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sofware")]
    public void UnusableOverrideFallsBackToAutomatic(string value)
    {
        // A typo must not take the register down. This runs before any window exists,
        // so there would be nowhere to report the mistake - automatic is the safe
        // reading, and on Windows 7 automatic is what was wanted anyway.
        AssertSoftware(RenderingSelector.Select(value, Windows(6, 1, 7601)));
    }
}
