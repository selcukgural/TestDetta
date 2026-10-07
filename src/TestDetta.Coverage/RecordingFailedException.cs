namespace TestDetta.Coverage;

/// <summary>
/// The recording of a test project failed on TestDetta's side, so it gets no map: its test assembly was not
/// instrumented, or the tracker wrote no coverage (or no test class) in its test processes although the tests ran.
/// A map from such a recording would be empty or list every class as failed, and say nothing true; the project is
/// reported as not recorded with the reason, and selection decides it by name until it has a map.
/// </summary>
/// <param name="message">What went wrong, for the summary.</param>
public sealed class RecordingFailedException(string message) : InvalidOperationException(message)
{
    /// <summary>True when the tests ran and passed, false when they ran and some failed, null when they were not run.</summary>
    public bool? TestsPassed { get; init; }
}
