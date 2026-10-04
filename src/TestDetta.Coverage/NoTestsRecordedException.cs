namespace TestDetta.Coverage;

/// <summary>
/// A test project ran no test while recording, so there is no coverage to store: typically a
/// project named like a test project that is not one (an AOT smoke test, a sample). The caller
/// skips it; selection then decides for it by name.
/// </summary>
/// <param name="message">What happened.</param>
public sealed class NoTestsRecordedException(string message) : InvalidOperationException(message);
