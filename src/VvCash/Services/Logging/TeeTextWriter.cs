using System;
using System.Text;
using System.IO;

namespace VvCash.Services.Logging;

/// <summary>Forwards every write to two inner <see cref="TextWriter"/>s independently,
/// so <c>dotnet run</c> during development keeps printing to the console exactly as it
/// does today (the first writer, whatever <see cref="Console.Out"/> was before
/// <see cref="AppLogging.Start"/> ran) while the same call also lands in the register's
/// log file (the second).
///
/// Each inner writer is called inside its own try/catch. That is the single most
/// important property of this class: <see cref="RollingFileLogWriter"/> already never
/// throws on its own, but this is the object actually installed as
/// <see cref="Console.Out"/> for the rest of the process, so it is the last line of
/// defence between a logging failure — in either sink, including whatever the console
/// writer itself does when there is no console attached — and every one of the app's 115
/// existing <c>Console.WriteLine</c> call sites. None of them expect logging to be able
/// to throw, and after this change none of them have to start.</summary>
public sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter _first;
    private readonly TextWriter _second;

    public TeeTextWriter(TextWriter first, TextWriter second)
    {
        _first = first;
        _second = second;
    }

    public override Encoding Encoding => _second.Encoding;

    public override void Write(char value) => SafeWrite(w => w.Write(value));

    public override void Write(string? value) => SafeWrite(w => w.Write(value));

    public override void WriteLine(string? value) => SafeWrite(w => w.WriteLine(value));

    public override void WriteLine() => SafeWrite(w => w.WriteLine());

    private void SafeWrite(Action<TextWriter> action)
    {
        try { action(_first); } catch { /* one dead sink must not silence the other */ }
        try { action(_second); } catch { /* ditto, the other way round */ }
    }
}
