using Microsoft.Extensions.Logging;

namespace BARS_Client_V2.Infrastructure.Diagnostics;

internal sealed class ClientLogProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName);
    public void Dispose() { }

    private sealed class FileLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level != LogLevel.None &&
            level >= (category.StartsWith("BARS_Client_V2", StringComparison.Ordinal) ? LogLevel.Debug : LogLevel.Warning);

        public void Log<TState>(LogLevel level, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level) || (level == LogLevel.Debug && exception == null)) return;
            ClientLog.Write($"[{level}] {category}: {formatter(state, exception)}" +
                (exception == null ? string.Empty : Environment.NewLine + exception));
        }
    }
}
