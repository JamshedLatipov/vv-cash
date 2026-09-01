using System;
using System.Threading;
using System.Threading.Tasks;

namespace VvCash.Services.Update;

public interface IUpdateService
{
    /// <summary>Fetches and validates the manifest.
    ///
    /// Reports "nothing newer" and "the check itself failed" as different outcomes — see
    /// <see cref="UpdateCheckResult"/> for why that distinction, which the hourly timer
    /// genuinely did not need, became necessary. The timer still ignores the failure and
    /// stays quiet; only a check the cashier asked for reports it.</summary>
    Task<UpdateCheckResult> CheckAsync(CancellationToken ct);

    /// <summary>Downloads the installer and verifies its SHA-256. Returns the path to
    /// the verified file, or null if the download failed, was cancelled, or the hash
    /// did not match. Never returns a path to an unverified file.</summary>
    Task<string?> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct);
}
