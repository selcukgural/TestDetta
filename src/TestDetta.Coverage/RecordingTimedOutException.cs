namespace TestDetta.Coverage;

/// <summary>
/// A test process did not finish within <see cref="RecordOptions.Timeout"/> while recording, so it
/// was killed and the project has no map. A hang usually repeats (a test waiting for a database or
/// a network service that is not there), so callers leave the project out rather than retry.
/// </summary>
/// <param name="message">What happened.</param>
public sealed class RecordingTimedOutException(string message) : InvalidOperationException(message);
