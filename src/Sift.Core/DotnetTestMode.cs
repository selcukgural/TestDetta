using System.Text.Json;

namespace Sift.Core;

/// <summary>Which test runner <c>dotnet test</c> uses in a repository.</summary>
public enum DotnetTestMode
{
    /// <summary>The classic VSTest runner: <c>dotnet test &lt;project&gt;</c>, run settings, in-process data collectors.</summary>
    VSTest,

    /// <summary>
    /// Microsoft.Testing.Platform, opted into through <c>global.json</c> (required for xUnit v3 on the
    /// .NET 10 SDK): <c>dotnet test --project &lt;project&gt;</c>, and each test project is its own executable.
    /// </summary>
    TestingPlatform,
}

/// <summary>Reads the <c>dotnet test</c> runner from <c>global.json</c>.</summary>
public static class DotnetTestModeDetector
{
    /// <summary>
    /// The mode set by the nearest <c>global.json</c> at or above <paramref name="directory"/>, which is
    /// the file <c>dotnet test</c> itself uses when run there. VSTest when there is none.
    /// </summary>
    /// <param name="directory">Directory <c>dotnet test</c> runs in, normally the repository root.</param>
    /// <exception cref="InvalidDataException">The nearest <c>global.json</c> is not valid JSON.</exception>
    public static DotnetTestMode Detect(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var path = Path.Combine(current.FullName, "global.json");
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

                return document.RootElement.TryGetProperty("test", out var test)
                       && test.TryGetProperty("runner", out var runner)
                       && string.Equals(runner.GetString(), "Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase)
                    ? DotnetTestMode.TestingPlatform
                    : DotnetTestMode.VSTest;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{path} is not valid JSON: {ex.Message}", ex);
            }
        }

        return DotnetTestMode.VSTest;
    }
}
