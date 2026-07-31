using System.Diagnostics;

namespace VvCash.Services.Update;

/// <summary>Starts the downloaded installer. Exists as an interface so no test ever
/// launches a real installer — every other part of the update flow is worth testing,
/// and this is the one call that cannot be.</summary>
public interface IInstallerLauncher
{
    /// <summary>Starts the installer unattended. Throws if the process cannot start.</summary>
    void Launch(string installerPath);
}

public sealed class ProcessInstallerLauncher : IInstallerLauncher
{
    public void Launch(string installerPath)
    {
        // /VERYSILENT   — no wizard at all; the cashier already agreed in our own dialog.
        // /SUPPRESSMSGBOXES — nothing may wait for a click once the app has exited.
        // /NORESTART    — never reboot the till, whatever the installer thinks it needs.
        //
        // The child process outlives this one on purpose: the caller shuts the app down
        // immediately afterwards, because Inno cannot overwrite a running VvCash.exe.
        var startInfo = new ProcessStartInfo(installerPath)
        {
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
            UseShellExecute = true
        };
        Process.Start(startInfo);
    }
}
