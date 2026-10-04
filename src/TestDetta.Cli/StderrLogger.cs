using Microsoft.Extensions.Logging;

namespace TestDetta.Cli;

/// <summary>
/// Minimal <see cref="ILoggerFactory"/> that writes to stderr, so stdout stays clean for the
/// machine-readable output. Replaces Microsoft.Extensions.Logging.Console, which would pull in
/// the options and configuration stack for what is a single line of formatting.
/// </summary>
/// <param name="writer">Destination, normally <see cref="Console.Error"/>.</param>
/// <param name="minimumLevel">Messages below this level are discarded before they are formatted.</param>
internal sealed class StderrLoggerFactory(TextWriter writer, LogLevel minimumLevel) : ILoggerFactory
{
    /// <summary>Serializes writes so lines from different threads do not interleave.</summary>
    private readonly Lock _gate = new();

    /// <summary>Minimum level, read by <see cref="StderrLogger.IsEnabled"/>.</summary>
    private LogLevel MinimumLevel => minimumLevel;

    /// <summary>Creates a logger whose lines are prefixed with the last segment of <paramref name="categoryName"/>.</summary>
    /// <param name="categoryName">Usually the full type name, such as <c>TestDetta.Core.GitClient</c>.</param>
    public ILogger CreateLogger(string categoryName)
    {
        var dot = categoryName.LastIndexOf('.');
        var shortName = dot < 0 ? categoryName : categoryName[(dot + 1)..];
        return new StderrLogger(this, shortName);
    }

    /// <summary>Not supported: this factory has exactly one fixed output.</summary>
    /// <param name="provider">Ignored.</param>
    public void AddProvider(ILoggerProvider provider) =>
        throw new NotSupportedException("StderrLoggerFactory does not accept providers.");

    /// <summary>Nothing to release; the writer belongs to the caller.</summary>
    public void Dispose()
    {
    }

    /// <summary>Writes one formatted line: time, level, category, message and, if any, the exception.</summary>
    /// <param name="level">Level of the message.</param>
    /// <param name="category">Short category name.</param>
    /// <param name="message">Formatted message.</param>
    /// <param name="exception">Exception attached to the message, or null.</param>
    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var label = level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };

        lock (_gate)
        {
            writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff} {label}] {category}: {message}");
            if (exception is not null)
            {
                writer.WriteLine(exception);
            }
        }
    }

    /// <summary>Logger for one category; all output goes through the owning factory.</summary>
    /// <param name="factory">Factory that owns the writer and the minimum level.</param>
    /// <param name="category">Short category name printed on every line.</param>
    private sealed class StderrLogger(StderrLoggerFactory factory, string category) : ILogger
    {
        /// <summary>Scopes are not printed, so no scope object is created.</summary>
        /// <typeparam name="TState">Scope state type.</typeparam>
        /// <param name="state">Ignored.</param>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <summary>True when <paramref name="logLevel"/> reaches the factory's minimum level.</summary>
        /// <param name="logLevel">Level to check.</param>
        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= factory.MinimumLevel;

        /// <summary>Formats and writes the message when its level is enabled.</summary>
        /// <typeparam name="TState">State type produced by the logging call.</typeparam>
        /// <param name="logLevel">Level of the message.</param>
        /// <param name="eventId">Not printed.</param>
        /// <param name="state">State passed to <paramref name="formatter"/>.</param>
        /// <param name="exception">Exception attached to the message, or null.</param>
        /// <param name="formatter">Turns the state into the message text.</param>
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            factory.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
