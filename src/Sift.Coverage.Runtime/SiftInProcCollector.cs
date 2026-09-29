using System.Runtime.Loader;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.DataCollection;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.DataCollector.InProcDataCollector;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.InProcDataCollector;

namespace Sift.Coverage.Runtime;

/// <summary>
/// VSTest in-process data collector, loaded into the test host through the run settings that
/// <c>sift record</c> writes. Forwards test boundaries to <see cref="SiftTracker"/>.
/// </summary>
/// <remarks>
/// Instrumented assemblies reference this assembly, but the test project's deps.json does not list
/// it, so the host could not find it. The constructor therefore resolves the reference to this very
/// assembly instance, which also guarantees that the collector and the instrumented code share the
/// static state of <see cref="SiftTracker"/>.
/// </remarks>
public sealed class SiftInProcCollector : InProcDataCollection
{
    /// <summary>Hooks assembly resolution as early as possible: the host creates collectors before running tests.</summary>
    public SiftInProcCollector()
    {
        var self = typeof(SiftInProcCollector).Assembly;
        var name = self.GetName().Name;
        AssemblyLoadContext.Default.Resolving += (_, requested) =>
            string.Equals(requested.Name, name, StringComparison.Ordinal) ? self : null;
    }

    /// <summary>Nothing to set up; the sink is not used because results go to a file.</summary>
    /// <param name="dataCollectionSink">Unused.</param>
    public void Initialize(IDataCollectionSink dataCollectionSink)
    {
    }

    /// <summary>Nothing to do at session start.</summary>
    /// <param name="testSessionStartArgs">Unused.</param>
    public void TestSessionStart(TestSessionStartArgs testSessionStartArgs)
    {
    }

    /// <summary>Opens a test in the tracker.</summary>
    /// <param name="testCaseStartArgs">The starting test.</param>
    public void TestCaseStart(TestCaseStartArgs testCaseStartArgs)
    {
        if (testCaseStartArgs.TestCase is { } testCase)
        {
            SiftTracker.BeginTest(testCase.FullyQualifiedName);
        }
    }

    /// <summary>Closes a test in the tracker.</summary>
    /// <param name="testCaseEndArgs">The finished test.</param>
    public void TestCaseEnd(TestCaseEndArgs testCaseEndArgs)
    {
        if (testCaseEndArgs.DataCollectionContext?.TestCase is { } testCase)
        {
            SiftTracker.EndTest(testCase.FullyQualifiedName);
        }
    }

    /// <summary>Writes the collected coverage.</summary>
    /// <param name="testSessionEndArgs">Unused.</param>
    public void TestSessionEnd(TestSessionEndArgs testSessionEndArgs) => SiftTracker.WriteResults();
}
