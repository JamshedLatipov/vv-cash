using System;

namespace VvCash.Services.Update;

/// <summary>One published release, after the manifest has passed validation. Every
/// field here has already been checked: Version is normalised to three parts, Url is
/// absolute and https, Sha256 is 64 lowercase hex characters.</summary>
public sealed record UpdateInfo(
    Version Version,
    string Url,
    string Sha256,
    long SizeBytes,
    string? Notes);
