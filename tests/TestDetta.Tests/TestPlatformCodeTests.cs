using System.Reflection;
using System.Reflection.Emit;
using TestDetta.Coverage.Runtime;

namespace TestDetta.Tests;

/// <summary>
/// A start-up hit whose callers are the test platform alone is kept apart from code outside every test
/// (docs/design/test-platform-code.md): the stack past the method's own assembly and the base class library decides.
/// </summary>
public sealed class TestPlatformCodeTests
{
    [Fact]
    public void A_method_the_test_platform_calls_on_its_own_thread_runs_for_it_alone()
    {
        var name = new AssemblyName("Microsoft.TestPlatform.Probe");
        var module = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run).DefineDynamicModule(name.Name!);
        var type = module.DefineType("Probe", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract);
        var run = type.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
        var il = run.GetILGenerator();
        il.Emit(OpCodes.Call, typeof(PlatformCallee).GetMethod(nameof(PlatformCallee.Instrumented))!);
        il.Emit(OpCodes.Ret);
        var start = type.CreateType().GetMethod("Run")!.CreateDelegate<ThreadStart>();

        var thread = new Thread(start);
        thread.Start();
        thread.Join();

        Assert.True(PlatformCallee.ForPlatformOnly);
    }

    [Fact]
    public void A_method_a_test_calls_does_not()
    {
        // Called from this test, with xUnit and the test assembly below: not the platform alone.
        PlatformCallee.Instrumented();

        Assert.False(PlatformCallee.ForPlatformOnly);
    }

    [Fact]
    public void A_method_with_no_caller_but_the_base_library_does_not()
    {
        var thread = new Thread(PlatformCallee.Instrumented);
        thread.Start();
        thread.Join();

        Assert.False(PlatformCallee.ForPlatformOnly);
    }
}

/// <summary>Stands for an instrumented method: its frame is the hit, the frames below it its callers. Public, so the dynamic assembly may call it.</summary>
public static class PlatformCallee
{
    /// <summary>The last classification.</summary>
    internal static bool ForPlatformOnly;

    /// <summary>Classifies its own callers, as <c>TestDettaTracker.Hit</c> does for an instrumented method.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static void Instrumented() => ForPlatformOnly = TestDettaTracker.RunsForTestPlatformOnlyForTests();
}
