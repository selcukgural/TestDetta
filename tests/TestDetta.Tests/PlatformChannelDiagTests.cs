using System.Reflection;
using System.Reflection.Emit;
using TestDetta.Coverage.Runtime;

namespace TestDetta.Tests;

/// <summary>Diagnostic only (diag/platform-channels): the stack classification of hits the test platform causes.</summary>
public sealed class PlatformChannelDiagTests
{
    [Fact]
    public void Callers_after_the_hit_assembly_and_the_bcl_decide()
    {
        Assert.Equal(PlatformChannelDiag.Origin.PlatformOnly,
            PlatformChannelDiag.Classify("Newtonsoft.Json", ["Newtonsoft.Json", "System.Private.CoreLib", "Microsoft.TestPlatform.CommunicationUtilities", "testhost", "System.Private.CoreLib"]).Origin);
        Assert.Equal(PlatformChannelDiag.Origin.PlatformMixed,
            PlatformChannelDiag.Classify("Newtonsoft.Json", ["Microsoft.VisualStudio.TestPlatform.ObjectModel", "xunit.runner.visualstudio.testadapter", "testhost"]).Origin);
        Assert.Equal((PlatformChannelDiag.Origin.Other, "Newtonsoft.Json.Tests"),
            PlatformChannelDiag.Classify("Newtonsoft.Json", ["System.Linq", "Newtonsoft.Json.Tests", "Microsoft.TestPlatform.CrossPlatEngine"]));
    }

    [Fact]
    public void A_platform_thread_calling_the_code_is_platform_only()
    {
        var name = new AssemblyName("Microsoft.TestPlatform.Probe");
        var module = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run).DefineDynamicModule(name.Name!);
        var type = module.DefineType("Probe", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract);
        var run = type.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
        var il = run.GetILGenerator();
        il.Emit(OpCodes.Call, typeof(PlatformProbeTarget).GetMethod(nameof(PlatformProbeTarget.HitMethod))!);
        il.Emit(OpCodes.Ret);
        var start = type.CreateType().GetMethod("Run")!.CreateDelegate<ThreadStart>();

        var thread = new Thread(start);
        thread.Start();
        thread.Join();
        Assert.Equal((PlatformChannelDiag.Origin.PlatformOnly, "Microsoft.TestPlatform.Probe"), (PlatformProbeTarget.Walked.Origin, PlatformProbeTarget.Walked.FirstCaller));
        Assert.EndsWith(nameof(PlatformProbeTarget.HitMethod), PlatformProbeTarget.Walked.Method, StringComparison.Ordinal);

        // Called from this test, with xUnit below: not the platform.
        PlatformProbeTarget.HitMethod();
        Assert.Equal(PlatformChannelDiag.Origin.Other, PlatformProbeTarget.Walked.Origin);
    }
}

/// <summary>Stands for an instrumented method: its frame is the hit, the frames below it are its callers. Public, so the dynamic assembly may call it.</summary>
public static class PlatformProbeTarget
{
    internal static (string? Method, PlatformChannelDiag.Origin Origin, string? FirstCaller) Walked;

    public static void HitMethod() => Walked = PlatformChannelDiag.Walk();
}
