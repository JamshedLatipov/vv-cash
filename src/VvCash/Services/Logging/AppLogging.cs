using System;
using System.Threading.Tasks;

namespace VvCash.Services.Logging;

/// <summary>Gives the app's existing <c>Console.WriteLine</c> logging somewhere to go.
///
/// The app already logs — 115 calls across the services, each prefixed like
/// <c>[AuthService]</c> or <c>[SyncService]</c> — but it is a WinExe with no console
/// attached in production, so every one of those lines currently goes nowhere. Rather
/// than rewrite 115 call sites to go through a new logging type, this redirects
/// <see cref="Console.Out"/> once, here, to a <see cref="TeeTextWriter"/> that keeps
/// printing to whatever console <c>dotnet run</c> already gives it in development and
/// also appends every line, timestamped, to a size-capped rolling file (see
/// <see cref="RollingFileLogWriter"/>) under the same %LOCALAPPDATA%\VvCash folder as
/// settings.json and offline_data.db.
///
/// Also subscribes the two hooks that catch what currently has no trace at all:
/// <see cref="AppDomain.UnhandledException"/> (an exception that would otherwise take
/// the process down with nothing on disk to show for it — the Windows 7 case a queue
/// feature that has never run there motivated this whole change for) and
/// <see cref="TaskScheduler.UnobservedTaskException"/> (a faulted task nobody awaited,
/// e.g. the async-void payment continuation in PosViewModel.ProceedToPayAsync). Neither
/// hook changes what already happens to these exceptions — see the remarks on each
/// subscription below — this only makes sure something is written before whatever
/// happens next happens.</summary>
public static class AppLogging
{
    /// <summary>Call once, as early as possible — see Program.Main, which calls this
    /// before <c>BuildAvaloniaApp()</c> even runs. That is deliberately earlier than
    /// <c>App.OnFrameworkInitializationCompleted</c>: Avalonia's own platform detection
    /// and rendering setup happen in between, and on Windows 7 that is exactly where a
    /// register can go down before a single window exists (see RenderingSelector's own
    /// remarks) — this call has to be in place before that, not after it, to have any
    /// chance of catching it.
    ///
    /// Returns the created <see cref="RollingFileLogWriter"/> so a caller who needs the
    /// resolved log path (e.g. to show it in a diagnostics screen one day) doesn't have
    /// to recompute it.</summary>
    public static RollingFileLogWriter Start(string? logFilePath = null)
    {
        var fileWriter = new RollingFileLogWriter(logFilePath);

        // The original Console.Out, not discarded: it is whatever dotnet run attached
        // (or, in production, the harmless no-op writer a WinExe gets with no console),
        // and TeeTextWriter keeps every call going there too.
        Console.SetOut(new TeeTextWriter(Console.Out, fileWriter));

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // IsTerminating is always true for this event on .NET (Core) — there is no
            // way to stop the process from going down once this fires, only a chance to
            // leave a trace before it does. Console.WriteLine now reaches
            // RollingFileLogWriter, whose every write is already flushed to disk before
            // it returns (see its own remarks) — nothing further to flush here.
            Console.WriteLine(FormatUnhandledException("AppDomain.UnhandledException", e.ExceptionObject as Exception));
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Console.WriteLine(FormatUnhandledException("TaskScheduler.UnobservedTaskException", e.Exception));

            // Deliberately NOT calling e.SetObserved(): since .NET Core 2.0 an
            // unobserved task exception no longer crashes the process by default, it is
            // just silently dropped. Calling SetObserved() here wouldn't change that —
            // it exists to stop a *different*, opt-in behaviour (UnobservedTaskException
            // escalating to a crash under ThrowUnobservedTaskExceptions) that this app
            // does not enable. Leaving it uncalled keeps this hook doing exactly one
            // thing: logging what already happens, not altering it.
        };

        return fileWriter;
    }

    /// <summary>Pure formatting, kept separate from the two subscriptions above so it can
    /// be unit tested without touching <see cref="AppDomain"/>, <see cref="TaskScheduler"/>
    /// or <see cref="Console"/> — none of which a test can safely fire or redirect without
    /// either crashing the test host or racing every other test class xunit runs
    /// concurrently.</summary>
    internal static string FormatUnhandledException(string origin, Exception? ex)
    {
        // ex.ToString() carries the type, message and full stack trace (and any inner
        // exception's) in one call — everything needed to tell a shop what happened,
        // nothing that was ever excluded from the existing Console.WriteLine lines this
        // sits alongside (see the review note this feature started from: no token, PIN,
        // password or raw response body is logged anywhere in this app).
        var body = ex?.ToString() ?? "(no exception object)";
        return $"[{origin}] {body}";
    }
}
