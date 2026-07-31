using System;
using System.Reflection;

namespace VvCash.Services.Update;

/// <summary>The running build's version. An interface with one property looks like
/// ceremony, but the update check is entirely a comparison against this value, so
/// without a seam here no test can describe "the register is older than what the
/// server publishes" without rebuilding the assembly.</summary>
public interface IAppVersionProvider
{
    Version Current { get; }
}

public static class AppVersion
{
    /// <summary>Trims a version to Major.Minor.Build.
    ///
    /// Both sides of the update comparison need this. An assembly version always has
    /// four components (1.0.0 in the csproj builds as 1.0.0.0), while a hand-written
    /// manifest says "1.0.0" and parses with Revision = -1. System.Version compares
    /// missing components as -1, so the unnormalised pair 1.0.0.0 and 1.0.0 are *not*
    /// equal — the running build would read as newer than the release it came from,
    /// and an update would never be offered. Build is clamped rather than passed
    /// through because "1.1" parses with Build = -1, and the Version constructor
    /// rejects a negative component outright.</summary>
    public static Version Normalize(Version version)
        => new Version(version.Major, version.Minor, version.Build < 0 ? 0 : version.Build);
}

public sealed class AssemblyAppVersionProvider : IAppVersionProvider
{
    public Version Current { get; } = AppVersion.Normalize(
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));
}
