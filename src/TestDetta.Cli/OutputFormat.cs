namespace TestDetta.Cli;

/// <summary>How <c>td affected</c> prints its result on stdout.</summary>
internal enum OutputFormat
{
    /// <summary>Human readable summary with the reason for every project.</summary>
    Text,

    /// <summary>A JSON document for CI scripts.</summary>
    Json,

    /// <summary>One .csproj path per line, relative to the repository root, for piping into <c>dotnet test</c>.</summary>
    Paths,

    /// <summary>One shell-quoted <c>dotnet test</c> command per test project that needs to run.</summary>
    Commands,
}
