namespace LucidCartographer.Services.Diagnostics;

/// <summary>
/// Tees Console.Out / Console.Error to a log file so scrape diagnostics
/// remain readable without attaching to the live console.
/// </summary>
public static class DiagnosticLogging
{
    public static void TeeConsoleToFile(string logPath)
    {
        try
        {
            var sink = new StreamWriter(logPath, append: false) { AutoFlush = true };
            Console.SetOut(new MultiTextWriter(Console.Out, sink));
            Console.SetError(new MultiTextWriter(Console.Error, sink));
        }
        catch
        {
            // Best-effort: if the diag log path is unwritable (read-only FS,
            // permission denied), just keep the default Console — never
            // prevent app startup over this.
        }
    }
}

internal sealed class MultiTextWriter(params TextWriter[] writers) : TextWriter
{
    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
    public override void Write(char value) { foreach (var w in writers) w.Write(value); }
    public override void Write(string? value) { foreach (var w in writers) w.Write(value); }
    public override void WriteLine(string? value) { foreach (var w in writers) w.WriteLine(value); }
    public override void Flush() { foreach (var w in writers) w.Flush(); }
}
