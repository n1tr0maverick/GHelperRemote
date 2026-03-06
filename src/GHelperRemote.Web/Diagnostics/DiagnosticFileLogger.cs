using Microsoft.Extensions.Logging;

namespace GHelperRemote.Web.Diagnostics;

/// <summary>
/// Static log writer that captures all output to a timestamped file.
/// Used for diagnostic dumps that users can share for debugging.
/// </summary>
public static class DiagnosticLog
{
    private static StreamWriter? _writer;
    private static readonly object Lock = new();

    public static string? FilePath { get; private set; }

    public static void Initialize(string path)
    {
        FilePath = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _writer = new StreamWriter(path, append: false) { AutoFlush = true };
    }

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        Console.WriteLine(line);
        lock (Lock)
        {
            try { _writer?.WriteLine(line); }
            catch { /* never throw from logger */ }
        }
    }

    public static void WriteLogEntry(LogLevel level, string category, string message, Exception? ex)
    {
        var levelStr = level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRIT ",
            _ => level.ToString()
        };

        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{levelStr}] [{category}] {message}";
        if (ex != null)
            line += Environment.NewLine + ex;

        lock (Lock)
        {
            try { _writer?.WriteLine(line); }
            catch { /* never throw from logger */ }
        }
    }

    public static void Dispose()
    {
        lock (Lock)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }
}

/// <summary>
/// ILoggerProvider that routes all log entries to <see cref="DiagnosticLog"/>.
/// </summary>
public sealed class DiagnosticFileLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new DiagnosticFileLogger(categoryName);
    public void Dispose() { }
}

/// <summary>
/// ILogger implementation that writes to the shared diagnostic log file.
/// </summary>
public sealed class DiagnosticFileLogger : ILogger
{
    private readonly string _category;

    public DiagnosticFileLogger(string category)
    {
        // Shorten "GHelperRemote.Core.Services.AcpiSensorService" to "AcpiSensorService"
        var lastDot = category.LastIndexOf('.');
        _category = lastDot >= 0 ? category[(lastDot + 1)..] : category;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        DiagnosticLog.WriteLogEntry(logLevel, _category, formatter(state, exception), exception);
    }
}
