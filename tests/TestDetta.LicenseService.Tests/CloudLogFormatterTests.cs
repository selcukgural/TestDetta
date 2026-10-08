using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TestDetta.LicenseService.Tests;

public sealed class CloudLogFormatterTests
{
    [Fact]
    public void An_entry_is_one_json_line_with_the_cloud_logging_severity()
    {
        var text = Write(LogLevel.Warning, "Trials paused: the daily ceiling of 10 is reached\nsecond line", null);

        Assert.EndsWith("\n", text);
        Assert.Single(text.TrimEnd('\n').Split('\n'));
        using var json = JsonDocument.Parse(text);
        Assert.Equal("WARNING", json.RootElement.GetProperty("severity").GetString());
        Assert.Equal("Trials paused: the daily ceiling of 10 is reached\nsecond line", json.RootElement.GetProperty("message").GetString());
        Assert.Equal("TestDetta.LicenseService.TrialEndpoint", json.RootElement.GetProperty("logger").GetString());
    }

    [Fact]
    public void An_exception_goes_into_the_message()
    {
        using var json = JsonDocument.Parse(Write(LogLevel.Error, "Webhook failed", new InvalidOperationException("boom")));

        Assert.Equal("ERROR", json.RootElement.GetProperty("severity").GetString());
        Assert.Contains("Webhook failed\nSystem.InvalidOperationException: boom", json.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void An_empty_entry_writes_nothing() =>
        Assert.Equal("", Write(LogLevel.Information, "", null));

    [Theory]
    [InlineData(LogLevel.Trace, "DEBUG")]
    [InlineData(LogLevel.Debug, "DEBUG")]
    [InlineData(LogLevel.Information, "INFO")]
    [InlineData(LogLevel.Critical, "CRITICAL")]
    public void Levels_map_to_cloud_logging_severities(LogLevel level, string severity) =>
        Assert.Equal(severity, CloudLogFormatter.Severity(level));

    private static string Write(LogLevel level, string message, Exception? exception)
    {
        using var writer = new StringWriter();
        var entry = new LogEntry<string>(level, "TestDetta.LicenseService.TrialEndpoint", default, message, exception, static (state, _) => state);
        new CloudLogFormatter().Write(entry, null, writer);
        return writer.ToString();
    }
}
