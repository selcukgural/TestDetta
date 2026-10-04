using System.Runtime.Loader;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.DataCollection;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.DataCollector.InProcDataCollector;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.InProcDataCollector;

namespace TestDetta.Coverage.Runtime;

/// <summary>
/// VSTest in-process data collector, loaded into the test host through the run settings that
/// <c>td record</c> writes. It makes the instrumented assemblies find <see cref="TestDettaTracker"/>
/// and writes the results when the session ends.
/// </summary>
/// <remarks>
/// Instrumented assemblies reference this assembly, but the test project's deps.json does not list
/// it, so the host could not find it. The constructor therefore resolves the reference to this very
/// assembly instance, which also guarantees that the collector and the instrumented code share the
/// static state of <see cref="TestDettaTracker"/>.
/// </remarks>
public sealed class TestDettaInProcCollector : InProcDataCollection
{
    /// <summary>Hooks assembly resolution as early as possible: the host creates collectors before running tests.</summary>
    public TestDettaInProcCollector()
    {
        var self = typeof(TestDettaInProcCollector).Assembly;
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

    /// <summary>Nothing to do: test classes mark themselves through their instrumented entry points.</summary>
    /// <param name="testCaseStartArgs">Unused.</param>
    public void TestCaseStart(TestCaseStartArgs testCaseStartArgs)
    {
    }

    /// <summary>Nothing to do: test classes mark themselves through their instrumented entry points.</summary>
    /// <param name="testCaseEndArgs">Unused.</param>
    public void TestCaseEnd(TestCaseEndArgs testCaseEndArgs)
    {
    }

    /// <summary>Writes the collected coverage.</summary>
    /// <param name="testSessionEndArgs">Unused.</param>
    public void TestSessionEnd(TestSessionEndArgs testSessionEndArgs) => TestDettaTracker.WriteResults();
}
