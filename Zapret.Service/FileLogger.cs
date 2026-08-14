using System.Text;
using Zapret.Core;

namespace Zapret.Service;

/// <summary>
/// Minimal rolling file logging into <c>%ProgramData%\ZapretByGrubeer\logs</c>, with the display product
/// name in the header (SPEC.md §13). Deliberately dependency-free: a logging package is not worth a
/// supply-chain surface for a product whose whole point is running privileged network code.
/// </summary>
public sealed class FileLoggerProvider(string source, LogLevel minimum = LogLevel.Information) : ILoggerProvider
{
    private readonly object _gate = new();
    private string? _currentPath;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName, minimum);

    internal void Append(string line)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Logs);
                var path = Path.Combine(AppPaths.Logs, $"{source}-{DateTime.UtcNow:yyyyMMdd}.log");

                if (path != _currentPath)
                {
                    _currentPath = path;
                    if (!File.Exists(path))
                    {
                        File.AppendAllText(path,
                            $"{AppPaths.DisplayName} — {source} log{Environment.NewLine}",
                            new UTF8Encoding(false));
                    }
                }

                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the service down.
            }
        }
    }

    public void Dispose() { }

    private sealed class FileLogger(FileLoggerProvider provider, string category, LogLevel minimum) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var shortCategory = category.Split('.').LastOrDefault() ?? category;
            var line = $"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z {Level(logLevel)} {shortCategory}: {formatter(state, exception)}";

            if (exception is not null) line += Environment.NewLine + exception;

            provider.Append(line);
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "---",
        };
    }
}
