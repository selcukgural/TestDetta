using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace TestDetta.LicenseService;

/// <summary>
/// Writes each log entry as one line of JSON that Cloud Logging reads as a structured entry: <c>severity</c>
/// carries the level and <c>message</c> the text, with an exception appended to it. The default console
/// formatter writes the category and the message as separate lines without a severity, so Cloud Logging
/// sees every line as a plain default entry and an alert on warnings or errors (<c>alerts.sh</c>) has
/// nothing to match.
/// </summary>
public sealed class CloudLogFormatter() : ConsoleFormatter(FormatterName)
{
    /// <summary>Name the console logger selects this formatter by.</summary>
    public const string FormatterName = "cloud";

    /// <summary>Writes one entry; an entry with neither a message nor an exception writes nothing.</summary>
    /// <param name="logEntry">The entry.</param>
    /// <param name="scopeProvider">Scopes, not written: the service logs carry their context in the message.</param>
    /// <param name="textWriter">The console.</param>
    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (logEntry.Exception is null && string.IsNullOrEmpty(message))
        {
            return;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("severity"u8, Severity(logEntry.LogLevel));
            json.WriteString("message"u8, logEntry.Exception is null ? message : $"{message}\n{logEntry.Exception}");
            json.WriteString("logger"u8, logEntry.Category);
            json.WriteEndObject();
        }

        textWriter.Write(Encoding.UTF8.GetString(buffer.WrittenSpan));
        textWriter.Write('\n');
    }

    /// <summary>The Cloud Logging severity of a .NET log level.</summary>
    /// <param name="level">The level.</param>
    public static string Severity(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARNING",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRITICAL",
        _ => "DEFAULT",
    };

    /// <summary>
    /// Logs to the console as one JSON line per entry on Cloud Run (a service sets <c>K_SERVICE</c>, a job
    /// <c>CLOUD_RUN_JOB</c>) and as single plain lines elsewhere, where a person reads them.
    /// </summary>
    /// <param name="logging">The logging builder.</param>
    public static ILoggingBuilder AddServiceConsole(ILoggingBuilder logging)
    {
        if (Environment.GetEnvironmentVariable("K_SERVICE") is null && Environment.GetEnvironmentVariable("CLOUD_RUN_JOB") is null)
        {
            return logging.AddSimpleConsole(static console => console.SingleLine = true);
        }

        return logging
            .AddConsole(static console => console.FormatterName = FormatterName)
            .AddConsoleFormatter<CloudLogFormatter, ConsoleFormatterOptions>();
    }
}
