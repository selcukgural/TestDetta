using System.Reflection;
using TestDetta.Coverage;
using TestDetta.Coverage.Runtime;

namespace TestDetta.Tests;

/// <summary>
/// The tracker's identity, which instrumented assemblies reference. .NET Framework loads a strong-named assembly only
/// when everything it references has a strong name, and recording adds a reference to the tracker into third-party
/// assemblies such as Newtonsoft.Json; macOS and Linux never enforce that, so these checks stand in for it there.
/// </summary>
public sealed class TrackerIdentityTests
{
    [Fact]
    public void The_tracker_has_a_strong_name_so_signed_assemblies_can_reference_it_on_net_framework()
    {
        Assert.NotEmpty(typeof(TestDettaTracker).Assembly.GetName().GetPublicKeyToken()!);
    }

    [Fact]
    public void Both_builds_of_the_tracker_have_one_identity_so_instrumented_code_binds_to_either()
    {
        var standard = typeof(TestDettaTracker).Assembly.GetName();
        var framework = AssemblyName.GetAssemblyName(Path.Combine(
            Path.GetDirectoryName(typeof(RuntimeDeployment).Assembly.Location)!, "runtime", "net462", Path.GetFileName(typeof(TestDettaTracker).Assembly.Location)));

        Assert.Equal(standard.FullName, framework.FullName);
    }

    [Fact]
    public void The_collector_is_loaded_from_the_copy_next_to_the_tests()
    {
        var tests = Path.Combine(Path.GetTempPath(), "out", "App.Tests.dll");

        Assert.Equal(Path.Combine(Path.GetTempPath(), "out", Path.GetFileName(typeof(TestDettaTracker).Assembly.Location)), RuntimeDeployment.DeployedPath(tests));
    }
}
