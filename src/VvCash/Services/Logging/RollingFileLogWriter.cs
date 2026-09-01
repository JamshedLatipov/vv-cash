using System;
using System.IO;
using System.Text;

namespace VvCash.Services.Logging;

/// <summary>A <see cref="TextWriter"/> that appends timestamped lines to a size-capped,
/// self-rolling file, and never throws.
///
/// This is the file half of the logging seam (see <see cref="AppLogging"/>): the app
/// already carries 115 <c>Console.WriteLine</c> calls across its services, and rather
/// than rewrite every one of them, <see cref="AppLogging.Start"/> points
/// <see cref="Console.Out"/> at an instance of this class so every existing call lands
/// on disk for free.
///
/// Every write opens the file, appends, flushes and closes again — nothing is left
/// buffered in this process waiting for a <see cref="Dispose"/> that a crash might skip.
/// That also means the file is never held open between calls, so a shop's antivirus or a
/// support person tailing the file cannot deadlock against it.
///
/// Nothing here ever throws out of a public member. A full disk, a file another process
/// is holding open, a permissions problem — every one of those is caught inside
/// <see cref="WriteCore"/> and simply drops that one line; logging degrades to "no
/// logging" rather than taking a till down over a diagnostic. Deliberately not "disabled
/// forever after the first failure" either: a transient lock (antivirus scanning the
/// file, a support person with it open) clears on its own, and the next write just tries
/// again.</summary>
public sealed class RollingFileLogWriter : TextWriter
{
    /// <summary>Cap per file. 2 MiB of short, single-line diagnostic text is on the
    /// order of tens of thousands of lines — enough to cover a busy shift — while
    /// staying small enough for a shop to attach to a support email without thinking
    /// twice about it.</summary>
    public const long DefaultMaxFileSizeBytes = 2 * 1024 * 1024;

    /// <summary>Rolled backups kept alongside the live file (vvcash.log.1 .. .4).
    /// Combined with <see cref="DefaultMaxFileSizeBytes"/>, the worst case on disk is
    /// (DefaultMaxRolledFiles + 1) * DefaultMaxFileSizeBytes = 10 MiB, predictable
    /// regardless of how many months the register has been running.</summary>
    public const int DefaultMaxRolledFiles = 4;

    private readonly string _filePath;
    private readonly long _maxFileSizeBytes;
    private readonly int _maxRolledFiles;

    /// <summary>Guards the check-then-roll-then-append sequence in <see cref="WriteCore"/>
    /// against the concurrent writers this app actually has: the UI thread, the queue
    /// flush loop, Kestrel request handlers and the background sync loop can all log at
    /// once (see AppLogging's own remarks).</summary>
    private readonly object _lock = new();

    /// <summary>Creates the writer against the standard per-register log file. Pass
    /// <paramref name="logFilePath"/> to point at a different one (e.g. a temp file in
    /// tests); left null/empty, production gets the file that lives beside
    /// settings.json and offline_data.db — same %LOCALAPPDATA%\VvCash folder, same
    /// optional-constructor-argument arrangement, as SettingsService and
    /// OfflineStorageService already use for exactly this reason: one folder support can
    /// ask a shop for, not three.</summary>
    public RollingFileLogWriter(
        string? logFilePath = null,
        long maxFileSizeBytes = DefaultMaxFileSizeBytes,
        int maxRolledFiles = DefaultMaxRolledFiles)
    {
        if (string.IsNullOrEmpty(logFilePath))
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appDir = Path.Combine(appDataPath, "VvCash");
            logFilePath = Path.Combine(appDir, "vvcash.log");
        }

        _filePath = logFilePath;
        _maxFileSizeBytes = maxFileSizeBytes;
        _maxRolledFiles = maxRolledFiles;
    }

    /// <summary>The file this writer appends to. Exposed so callers (and tests) can find
    /// it without recomputing the %LOCALAPPDATA% path themselves.</summary>
    public string LogFilePath => _filePath;

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value) => WriteCore(value.ToString());

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        WriteCore(value);
    }

    public override void WriteLine(string? value)
    {
        // Timestamped so "Print failed" means something when the question is what
        // happened at 14:22 — local time, because that is the clock the shop reads,
        // not a UTC offset they would have to do math against over the phone.
        WriteCore($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {value}{Environment.NewLine}");
    }

    public override void WriteLine() => WriteCore(Environment.NewLine);

    private void WriteCore(string text)
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var bytes = Encoding.UTF8.GetBytes(text);

                if (File.Exists(_filePath))
                {
                    var currentLength = new FileInfo(_filePath).Length;
                    if (currentLength + bytes.Length > _maxFileSizeBytes) Roll();
                }

                using var stream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            catch
            {
                // Best effort only, on purpose (see class remarks): whatever went wrong —
                // locked file, full disk, no permission — this line is lost, not the
                // caller's transaction.
            }
        }
    }

    /// <summary>Shifts vvcash.log.1 -> .2 -> ... -> <see cref="_maxRolledFiles"/> (the
    /// oldest falls off the end and is deleted), then moves the live file to
    /// vvcash.log.1, clearing the way for a fresh one. Called from inside
    /// <see cref="WriteCore"/>'s try/catch, but wrapped in its own regardless: a rotation
    /// that fails half-way (e.g. one rolled file locked by whatever a shop uses to tail
    /// logs) must still let the write that triggered it proceed against whatever
    /// vvcash.log is left, rather than losing that line too.</summary>
    private void Roll()
    {
        try
        {
            if (_maxRolledFiles <= 0)
            {
                File.Delete(_filePath);
                return;
            }

            var oldest = RolledPath(_maxRolledFiles);
            if (File.Exists(oldest)) File.Delete(oldest);

            for (var i = _maxRolledFiles - 1; i >= 1; i--)
            {
                var src = RolledPath(i);
                if (File.Exists(src)) File.Move(src, RolledPath(i + 1));
            }

            File.Move(_filePath, RolledPath(1));
        }
        catch
        {
            // See WriteCore: the write this rotation was clearing space for still has to
            // be attempted against whatever is left at _filePath.
        }
    }

    private string RolledPath(int index) => $"{_filePath}.{index}";
}
