using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// Code can only name members of its own project and the projects it references, so a name is not
/// looked up in projects that cannot see its declaration (docs/design/run-once-cascades.md, A).
/// </summary>
public sealed class ProjectVisibilityTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public ProjectVisibilityTests()
    {
        _repo.Write("src/Core/Core.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Core/Rates.cs", "namespace Core; public static class Rates { public static decimal Vat() => 0.2m; }");
        // A tool next to the library: it uses Core too, and has a Run of its own.
        _repo.Write("src/Tool/Tool.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../Core/Core.csproj" /></ItemGroup></Project>
            """);
        _repo.Write("src/Tool/Exporter.cs", "namespace Tool; public class Exporter { public decimal Run() => Core.Rates.Vat(); }");
        _repo.Write("src/Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../Core/Core.csproj" /></ItemGroup></Project>
            """);
        _repo.Write("src/Lib/Invoice.cs", "namespace Lib; public class Invoice { public decimal Tax(decimal net) => net * Core.Rates.Vat(); }");
        _repo.Write("src/Lib/Job.cs", "namespace Lib; public class Job { public int Run() => 1; }");
        _repo.Write("tests/Lib.Tests/Lib.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/Lib/Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Lib.Tests/InvoiceTests.cs", "namespace Lib.Tests; public class InvoiceTests { [Fact] public void Taxes() => Assert.Equal(2m, new Lib.Invoice().Tax(10m)); }");
        _repo.Write("tests/Lib.Tests/JobTests.cs", "namespace Lib.Tests; public class JobTests { [Fact] public void Runs() => Assert.Equal(1, new Lib.Job().Run()); }");
        _repo.Write("tests/Lib.Tests/PlainTests.cs", "namespace Lib.Tests; public class PlainTests { [Fact] public void Holds() => Assert.True(true); }");
        _repo.Commit("base");
    }

    [Fact]
    public void Name_of_a_project_the_tests_cannot_see_does_not_reach_them()
    {
        // Exporter.Run reaches the change; JobTests calls Lib's own Run, which Lib.Tests cannot
        // confuse with the tool's: Lib does not reference Tool.
        _repo.Replace("src/Core/Rates.cs", "0.2m", "0.25m");

        Assert.Equal(["Lib.Tests.InvoiceTests"], Select().TestClasses);
    }

    [Fact]
    public void Code_using_dynamic_can_name_anything()
    {
        // A dynamic call binds by name at run time, to an object from any project.
        _repo.Write("tests/Lib.Tests/JobTests.cs", "namespace Lib.Tests; public class JobTests { [Fact] public void Runs() { dynamic job = new Lib.Job(); Assert.Equal(1, job.Run()); } }");
        _repo.Commit("dynamic");
        _repo.Replace("src/Core/Rates.cs", "0.2m", "0.25m");

        Assert.Equal(["Lib.Tests.InvoiceTests", "Lib.Tests.JobTests"], Select().TestClasses);
    }

    private TestProjectSelection Select() =>
        new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();

    public void Dispose() => _repo.Dispose();
}
