using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// A test class whose host runs a program's top-level statements but none of their lambdas is a user of them: the
/// recording named their method <c>Main</c> while the source index names it <c>&lt;Main&gt;$</c>, so a change named only
/// there (a response record behind <c>Produces&lt;T&gt;()</c>) missed a class that reads the OpenAPI document. Found by
/// experiment on 2026-10-12, modelled on AfterApply's integration tests. Slow: restores ASP.NET packages, builds, records.
/// </summary>
[Trait("Category", "Slow")]
public sealed class TopLevelStatementsTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_class_whose_host_ran_the_top_level_statements_is_selected_for_a_change_they_name()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        _repo.Write(".gitignore", "bin/\nobj/\n.testdetta/\n");
        _repo.Write("src/Contracts/Contracts.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        _repo.Write("src/Contracts/Responses.cs", "namespace Contracts; public sealed record DetailResponse(int Id, string Name);");
        _repo.Write("src/Api/Api.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="10.0.0" />
                <ProjectReference Include="../Contracts/Contracts.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("src/Api/Program.cs", """
            using Contracts;
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddOpenApi();
            var app = builder.Build();
            app.MapOpenApi();
            app.MapGet("/details/{id:int}", (int id) => new DetailResponse(id, "detail")).Produces<DetailResponse>();
            app.Run();
            public partial class Program;
            """);
        _repo.Write("tests/Api.Tests/Api.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0" />
                <ProjectReference Include="../../src/Api/Api.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Api.Tests/Tests.cs", """
            using System.Text.Json;
            using Microsoft.AspNetCore.Mvc.Testing;
            using Xunit;
            namespace Api.Tests;

            // Reads the OpenAPI document: the record's shape, through reflection, without running its code or a lambda.
            public class OpenApiSchemaTests(WebApplicationFactory<Program> host) : IClassFixture<WebApplicationFactory<Program>>
            {
                [Fact]
                public async Task The_detail_schema_has_exactly_id_and_name()
                {
                    using var document = JsonDocument.Parse(await host.CreateClient().GetStringAsync("/openapi/v1.json"));
                    var properties = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("DetailResponse").GetProperty("properties");
                    Assert.Equal(["id", "name"], properties.EnumerateObject().Select(static p => p.Name).Order());
                }
            }
            """);
        _repo.Commit("base");

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var testProject = graph.Projects.Single(static p => p.IsTestProject);
        var output = new StringWriter();
        var (map, passed) = new CoverageRecorder(NullLoggerFactory.Instance)
            .Record(new RecordRequest(_repo.Root, graph, testProject, new RecordOptions(Parallelism: 1)), output);

        Assert.True(passed, output.ToString());
        var program = map.Classes["Api.Tests.OpenApiSchemaTests"].Select(id => map.Methods[id]).Where(static m => m.TypeName == "Program").ToList();
        Assert.Contains(new CoveredMethod("Program", "<Main>$"), program);
        Assert.DoesNotContain(new CoveredMethod("Program", "Main"), program);

        CoverageMapStore.Write(CoverageMapStore.PathFor(_repo.Root, testProject.Path), map);
        _repo.Write("src/Contracts/Responses.cs", "namespace Contracts; public sealed record DetailResponse(int Id, string Name, string? Note = null);");
        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests.Single();

        Assert.True(selection.Mode == TestRunMode.All || selection.TestClasses.Contains("Api.Tests.OpenApiSchemaTests"), $"{selection.Mode}: {selection.Reason}");
    }
}
