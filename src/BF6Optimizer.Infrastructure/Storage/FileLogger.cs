using System.Text;
using Microsoft.Extensions.Logging;

namespace BF6Optimizer.Infrastructure.Storage;

/// <summary>Readable daily log files. No handle is kept open, so nothing outlives the process.</summary>
public sealed class FileLoggerProvider(AppPaths paths) : ILoggerProvider
{
    private readonly object _gate = new();

    public ILogger CreateLogger(string categoryName) =>
        new FileLogger(this, categoryName[(categoryName.LastIndexOf('.') + 1)..]);

    public void Dispose() { }

    public string CurrentFile => Path.Combine(paths.Logs, $"app-{DateTime.Now:yyyyMMdd}.log");

    public IReadOnlyList<string> ReadRecent(int maxLines)
    {
        lock (_gate)
        {
            if (!File.Exists(CurrentFile)) return [];
            var lines = File.ReadAllLines(CurrentFile);
            return lines.Length <= maxLines ? lines : lines[^maxLines..];
        }
    }

    private void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(paths.Logs);
                File.AppendAllText(CurrentFile, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var tag = level switch
            {
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Critical => "CRT",
                _ => "DBG",
            };
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{tag}] {category}: {formatter(state, exception)}";
            if (exception is not null) line += $"{Environment.NewLine}    {exception.GetType().Name}: {exception.Message}";
            owner.Write(line);
        }
    }
}
