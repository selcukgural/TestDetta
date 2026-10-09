# Coding guidelines

These rules apply to all code in this repository. Rules marked **(enforced)** fail the build
through `.editorconfig`, `Directory.Build.props` and the .NET analyzers; the rest are checked in
review.

## Build and dependencies

- **One place for build settings.** Shared properties live in `Directory.Build.props`, package
  versions in `Directory.Packages.props` (central package management, transitive pinning on).
  A `.csproj` only declares what is specific to that project: output type, project references,
  a package no other project needs. `PackageReference` items never carry a `Version`. **(enforced)**
- **Lock files are committed.** `packages.lock.json` is generated on restore; CI restores in
  locked mode (`CI=true`).
- **No large libraries for small jobs.** Add a package only when it replaces a meaningful amount
  of code that would be hard to get right. Twenty lines of our own code beat a dependency tree.
  Example: the CLI has its own stderr logger instead of `Microsoft.Extensions.Logging.Console`.

## Language

- **Latest C# syntax.** `LangVersion` is `latest`. Use file-scoped namespaces, primary
  constructors, collection expressions (`[]`, `[.. items]`), pattern matching, switch
  expressions, raw string literals, `required`/`init`, `field`, and `u8` literals where they fit.
  Most of these are analyzer rules. **(enforced)**
- **Follow Rider/ReSharper conventions.** `var` everywhere, `_camelCase` private fields,
  `PascalCase` constants and static readonly fields, `I` prefix for interfaces, explicit
  accessibility modifiers, one top-level type per file (small related records may share a file).
  **(enforced where an analyzer exists)**
- **Braces always**, even around a one-line `if`, `else`, `for`, `foreach` or `while` body.
  **(enforced, IDE0011)**

## Control flow

- **Guard clauses and early returns.** Validate and exit at the top of a method; keep the main
  path unindented. Prefer `continue` over wrapping a loop body in an `if`.

  ```csharp
  if (owner is null)
  {
      unmapped.Add(relative);
      continue;
  }

  reasons.TryAdd(owner.Path, ImpactReason.DirectChange);
  ```

- **At most five parameters.** A constructor or method that needs more takes a parameter object,
  usually a `record` named after what it describes (see `ResultReport`). Optional flags go into
  that object too, not into boolean parameters.

## Comments

- **Every type and member has an XML comment**: classes, records, interfaces, enums and their
  values, methods, properties, constructors, including private and internal ones. Document every
  parameter with `<param>`. The compiler enforces it for public members; review checks the rest.
  Test projects are exempt; test names describe the behaviour. **(enforced for public members)**
- **Write for the next developer.** Explain *what* the member does and *why* it is done this way:
  intent, invariants, edge cases, performance reasons. Do not restate the signature ("Gets the
  path"), narrate the code line by line, or leave notes addressed to an AI or about how the code
  was written.

## Logging

- **Detailed debug logs.** Every decision a user might question ("why was this project
  selected?") produces a `Debug` line; raw external output (git lines) goes to `Trace`.
  `Warning` is for input we ignore, `Error` for failures.
- **Source-generated logging only.** Declare messages with `[LoggerMessage]` on partial methods,
  never `logger.LogDebug($"...")`, so disabled levels cost nothing. **(enforced, CA1848)**
- **Console text goes through `ConsoleStyle`.** Human-readable reports are built with `TextReport` and
  `TextTable` and styled with `ConsoleStyle.Paint(Role, ...)`; never write ANSI codes or box characters
  by hand. Colour follows `--color` and the environment (docs/design/console-output.md) and never
  reaches JSON or other machine formats.
- **Log to stderr.** stdout carries the command's output and must stay machine readable.
  `-v`/`--verbose` turns on debug logs, `--log-level` selects any level.
- Classes take `ILogger<T>` through their primary constructor; tests pass `NullLogger<T>.Instance`.

## Performance

- **Avoid needless allocations** on paths that run per file or per project: no string
  concatenation for comparisons (see `Paths.IsUnder`), no LINQ in hot loops when a plain loop is
  as clear, no `ToList()`/`ToArray()` just to iterate once.
- Use `FrozenSet`/`FrozenDictionary` for static lookup tables, `ReadOnlySpan<T>` and
  `params ReadOnlySpan<T>` for transient inputs, `u8` literals and `Utf8JsonWriter` for JSON, and
  `static` lambdas when nothing is captured.
- Do not compute expensive log arguments unless the level is enabled
  (`logger.IsEnabled(...)`). **(enforced, CA1873)**
- Measure before optimizing anything more than this; readability wins without numbers.

## Behaviour

- **Select more, never less.** When the analysis is unsure whether a change affects a project,
  it treats the project as affected. A missed test is a bug; an extra test only costs CI time.
