using System;
using System.Threading;
using System.Threading.Tasks;

namespace VvCash.Services.Update;

public interface IUpdateService
{
    /// <summary>Fetches and validates the manifest. Returns null both when there is no
    /// newer release and when anything at all went wrong — a caller has nothing useful
    /// to do with the difference, and the register must not nag the cashier about the
    /// update server.</summary>
    Task<UpdateInfo?> CheckAsync(CancellationToken ct);

    /// <summary>Downloads the installer and verifies its SHA-256. Returns the path to
    /// the verified file, or null if the download failed, was cancelled, or the hash
    /// did not match. Never returns a path to an unverified file.</summary>
    Task<string?> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct);
}
