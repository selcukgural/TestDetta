using Sift.Coverage.Runtime;

namespace Sift.Tests;

/// <summary>
/// An entry point of another class called from a running test leaves the bucket to the test; one the
/// framework calls (including a data member declared on the running class but used by another)
/// switches it.
/// </summary>
public sealed class SiftTrackerNestingTests
{
    private const string Running = "Sift.Tests.SiftTrackerNestingTests";

    [Fact]
    public void Entry_point_called_by_the_running_test_is_nested() =>
        Assert.True(Other.Helper(Running, "Sift.Tests.Other"));

    [Fact]
    public async Task Entry_point_called_after_an_await_is_nested()
    {
        await Task.Yield();
        Assert.True(Other.Helper(Running, "Sift.Tests.Other"));
    }

    [Fact]
    public void Entry_point_in_a_lambda_of_the_running_test_is_nested()
    {
        Func<bool> call = () => Other.Helper(Running, "Sift.Tests.Other");
        Assert.True(call());
    }

    [Fact]
    public void Data_member_declared_on_the_running_class_is_not_nested() =>
        // The data member's own class is skipped: called for another class, it is the framework's call.
        Assert.False(RowsOfRunning(Running, "Sift.Tests.Consumer"));

    [Fact]
    public void Entry_point_reached_through_the_framework_is_not_nested() =>
        Assert.False(Other.Helper("Sift.Tests.NotOnTheStack", "Sift.Tests.Other"));

    private static bool RowsOfRunning(string running, string entered) => SiftTracker.CalledFromRunningTest(running, entered);
}

/// <summary>A test declared on a base class runs for the derived class: its frames are the running class's.</summary>
public abstract class NestingBase
{
    [Fact]
    public void Entry_point_called_by_a_test_declared_on_a_base_class_is_nested() =>
        Assert.True(Other.HelperFor(GetType(), "Sift.Tests.Other"));
}

/// <summary>Runs the base class's test.</summary>
public sealed class DerivedNestingTests : NestingBase
{
    [Fact]
    public void Base_frames_do_not_make_an_unrelated_type_the_runner() =>
        Assert.False(Other.HelperFor(typeof(Other), "Sift.Tests.Other", running: "Sift.Tests.Unrelated"));
}

/// <summary>Stands for another test class whose entry point a test calls.</summary>
internal static class Other
{
    public static bool Helper(string running, string entered) => SiftTracker.CalledFromRunningTest(running, entered);

    public static bool HelperFor(Type runningType, string entered, string? running = null) =>
        SiftTracker.CalledFromRunningTest(running ?? runningType.FullName!, entered, runningType);
}
