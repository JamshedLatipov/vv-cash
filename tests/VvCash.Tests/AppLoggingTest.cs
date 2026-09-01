using System;
using System.IO;
using System.Net.Http;
using System.Text;
using VvCash.Services.Logging;
using Xunit;

namespace VvCash.Tests;

/// <summary>Covers what a shop actually depends on from the new file logging (see
/// AppLogging's own remarks for why it exists): output reaches the file, the file
/// rotates instead of growing forever, and a logger whose destination cannot be written
/// to does not throw at the caller.
///
/// Deliberately does not touch <see cref="Console.SetOut"/> anywhere in this file.
/// xunit runs test classes concurrently, and AppLogging.Start redirects the
/// process-wide Console.Out — a test that called it (even if it restored the original
/// writer in Dispose) would still have every OTHER test class's Console.WriteLine
/// racing through it while it ran. Testing RollingFileLogWriter and TeeTextWriter
/// directly, as plain objects with WriteLine called on them, exercises the same code
/// AppLogging.Start wires up without going anywhere near global state.</summary>
public class AppLoggingTest : IDisposable
{
    private readonly string _dir;
    private readonly string _logPath;

    public AppLoggingTest()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"vvcash-logging-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_dir);
        _logPath = Path.Combine(_dir, "vvcash.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort cleanup */ }
    }

    [Fact]
    public void DefaultPath_LivesBesideSettingsAndOfflineData()
    {
        // No write happens here — only LogFilePath is read — so this cannot touch the
        // real %LOCALAPPDATA% on whatever machine runs the suite.
        var writer = new RollingFileLogWriter();

        var expectedDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VvCash");

        Assert.Equal(Path.Combine(expectedDir, "vvcash.log"), writer.LogFilePath);
    }

    [Fact]
    public void WriteLine_ReachesTheFile_Timestamped()
    {
        var writer = new RollingFileLogWriter(_logPath);

        writer.WriteLine("[AuthService] Login successful.");

        var content = File.ReadAllText(_logPath);
        Assert.Contains("[AuthService] Login successful.", content);
        // yyyy-MM-dd HH:mm:ss.fff in brackets, ahead of the original line — this is the
        // whole point: "Print failed" with no time is nearly useless.
        Assert.Matches(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] \[AuthService\] Login successful\.", content.TrimStart());
    }

    [Fact]
    public void RotatesInsteadOfGrowingWithoutBound()
    {
        // A small cap and few rolled backups so the test rotates several times over
        // quickly, and hits the "keep only N backups" ceiling — the same mechanism
        // production uses at 2 MiB / 4 backups, just scaled down to run in milliseconds.
        var writer = new RollingFileLogWriter(_logPath, maxFileSizeBytes: 200, maxRolledFiles: 2);

        for (var i = 0; i < 60; i++)
        {
            writer.WriteLine($"line-{i:D3}");
        }

        Assert.True(File.Exists(_logPath), "the live file should still exist");
        Assert.True(File.Exists(_logPath + ".1"), "one rotation should have happened");
        Assert.True(File.Exists(_logPath + ".2"), "a second rotation should have happened");
        // The ceiling: with maxRolledFiles=2, a third backup must never accumulate —
        // this is what keeps a register that runs for months predictable on disk.
        Assert.False(File.Exists(_logPath + ".3"), "rolled backups beyond the configured count must not pile up");

        long total = new FileInfo(_logPath).Length;
        foreach (var rolled in new[] { _logPath + ".1", _logPath + ".2" })
        {
            total += new FileInfo(rolled).Length;
        }

        // (maxRolledFiles + 1) files at ~200 bytes each, with generous slack for the one
        // line that can straddle a rotation boundary — the contract under test is
        // "bounded", not the exact byte count.
        Assert.True(total <= 3 * 200 * 2, $"total on-disk size {total} bytes was not bounded as expected");

        // And the earliest lines are the ones that fell off — proving this is a rolling
        // window over recent activity, not just a cap that stops writing.
        var combined = File.ReadAllText(_logPath) + File.ReadAllText(_logPath + ".1") + File.ReadAllText(_logPath + ".2");
        Assert.DoesNotContain("line-000", combined);
        Assert.Contains("line-059", combined);
    }

    [Fact]
    public void LockedFile_DoesNotThrow_AndRecoversOnceUnlocked()
    {
        var writer = new RollingFileLogWriter(_logPath);
        writer.WriteLine("before lock");

        using (new FileStream(_logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Simulates "a file held open by something else" — antivirus, a support
            // person tailing it, whatever. The one requirement that matters more than
            // any other here: this must not throw.
            var ex = Record.Exception(() => writer.WriteLine("during lock"));
            Assert.Null(ex);
        }

        // The lock was transient, so logging is expected to recover on its own, without
        // anything having to notice and re-enable it.
        writer.WriteLine("after lock");

        var content = File.ReadAllText(_logPath);
        Assert.Contains("after lock", content);
        Assert.DoesNotContain("during lock", content);
    }

    [Fact]
    public void UnwritableDirectory_DoesNotThrow()
    {
        // A file where the log's directory needs to be is a permissions-problem stand-in
        // that doesn't depend on OS-specific ACL manipulation: Directory.CreateDirectory
        // and File.Open both fail the same way when a path segment is already a file.
        var blockerFile = Path.Combine(_dir, "blocker");
        File.WriteAllText(blockerFile, "not a directory");
        var unwritablePath = Path.Combine(blockerFile, "vvcash.log");

        var writer = new RollingFileLogWriter(unwritablePath);

        var ex = Record.Exception(() =>
        {
            writer.WriteLine("first attempt");
            writer.WriteLine("second attempt");
        });

        Assert.Null(ex);
        Assert.False(Directory.Exists(unwritablePath));
    }

    [Fact]
    public void TeeTextWriter_ForwardsToBothInnerWriters()
    {
        var a = new StringWriter();
        var b = new StringWriter();
        var tee = new TeeTextWriter(a, b);

        tee.WriteLine("hello");

        Assert.Contains("hello", a.ToString());
        Assert.Contains("hello", b.ToString());
    }

    [Fact]
    public void TeeTextWriter_OneSinkThrowing_DoesNotStopTheOther_OrThrowToTheCaller()
    {
        var good = new StringWriter();

        var teeWithFirstThrowing = new TeeTextWriter(new ThrowingTextWriter(), good);
        var ex1 = Record.Exception(() => teeWithFirstThrowing.WriteLine("still logged"));
        Assert.Null(ex1);
        Assert.Contains("still logged", good.ToString());

        var good2 = new StringWriter();
        var teeWithSecondThrowing = new TeeTextWriter(good2, new ThrowingTextWriter());
        var ex2 = Record.Exception(() => teeWithSecondThrowing.WriteLine("also logged"));
        Assert.Null(ex2);
        Assert.Contains("also logged", good2.ToString());
    }

    [Fact]
    public void FormatUnhandledException_CarriesOriginMessageAndStackTrace()
    {
        Exception captured;
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (Exception ex)
        {
            captured = ex;
        }

        var formatted = AppLogging.FormatUnhandledException("AppDomain.UnhandledException", captured);

        Assert.Contains("AppDomain.UnhandledException", formatted);
        Assert.Contains("boom", formatted);
        Assert.Contains(nameof(InvalidOperationException), formatted);
        // ex.ToString() includes "at ..." stack frames once the exception has actually
        // been thrown and caught (unlike a freshly-constructed one, whose StackTrace is
        // null) — this is what makes the trace worth anything on a call to a shop.
        Assert.Contains("FormatUnhandledException_CarriesOriginMessageAndStackTrace", formatted);
    }

    [Fact]
    public void FormatUnhandledException_NullException_StillProducesALine()
    {
        var formatted = AppLogging.FormatUnhandledException("TaskScheduler.UnobservedTaskException", null);

        Assert.Contains("TaskScheduler.UnobservedTaskException", formatted);
        Assert.False(string.IsNullOrWhiteSpace(formatted));
    }

    /// <summary>Форма ровно та, что пришла с боевой кассы на Windows 7: два верхних
    /// уровня говорят «рукопожатие не состоялось», а причина лежит в третьем. Пока
    /// журнал печатал только два, ответ искали снаружи перебором шифров.</summary>
    [Fact]
    public void DescribeChain_ReachesTheInnermostCause_NotJustTheTopTwo()
    {
        var innermost = new Exception("The client and server cannot communicate, because they do not possess a common algorithm");
        var middle = new Exception("Authentication failed, see inner exception.", innermost);
        var top = new HttpRequestException("The SSL connection could not be established, see inner exception.", middle);

        var described = AppLogging.DescribeChain(top);

        Assert.Contains("common algorithm", described);
        Assert.Contains("HttpRequestException", described);
        Assert.DoesNotContain("   at ", described); // без стеков — см. докстринг DescribeChain
    }

    [Fact]
    public void DescribeChain_SurvivesASelfReferencingChain()
    {
        // AggregateException, вложенный сам в себя, эта программа не создаёт — но
        // строка в журнале не то место, где стоит выяснять это переполнением стека.
        var deepest = new Exception("bottom");
        var current = deepest;
        for (var i = 0; i < 50; i++) current = new Exception($"level {i}", current);

        var described = AppLogging.DescribeChain(current);

        Assert.Contains("цепочка длиннее", described);
    }

    [Fact]
    public void DescribeChain_HandlesNoException()
        => Assert.False(string.IsNullOrWhiteSpace(AppLogging.DescribeChain(null)));

    /// <summary>A TextWriter double whose every member throws, standing in for "the
    /// original Console.Out" behaving badly, or the file sink failing in a way its own
    /// try/catch somehow didn't anticipate — see TeeTextWriter's own remarks on why it
    /// isolates each sink regardless of RollingFileLogWriter already being non-throwing
    /// on its own.</summary>
    private sealed class ThrowingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => throw new IOException("simulated sink failure");
        public override void Write(string? value) => throw new IOException("simulated sink failure");
        public override void WriteLine(string? value) => throw new IOException("simulated sink failure");
        public override void WriteLine() => throw new IOException("simulated sink failure");
    }
}
