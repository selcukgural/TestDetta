namespace TestDetta.Core;

/// <summary>
/// A file a source generator added to a compilation, as the compiler embeds it in the PDB. Generated
/// code is in no source file, so the source index reads it from the coverage maps and indexes it like
/// the project's own sources: name tracing sees generated callers (the registration code the MVVM
/// Toolkit generates for a test's message recipients), and generated types are types like any other,
/// answered member by member (Meziantou.Polyfill's extensions, PolySharp's attributes).
/// </summary>
/// <param name="Project">Project that compiled it, relative to the repository root.</param>
/// <param name="Name">Its path in the PDB, which tells the generated files of a project apart.</param>
/// <param name="Text">Its source.</param>
public sealed record GeneratedSource(string Project, string Name, string Text);
