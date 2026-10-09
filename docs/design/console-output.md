# Console output: tables, colour and themes

Status: implemented 2026-10-09. The owner picked the table layout (variant C) from four variants shown
side by side in three themes on dark and light terminals.

## What changed

The human-readable output of `td affected`, `td test` (the plan and the summary after the run) and
`td record` is laid out as:

- a header line of facts: `TestDetta 0.1.3 · 1 changed file · 2 affected test projects`;
- boxed tables: affected projects (project, code or test, why), tests to run (project, run mode,
  classes selected of the candidates, share of recorded time saved, why), the run summary (project,
  result, what ran, time) and the record summary (project, incremental step, what happened to the map,
  classes, methods);
- below a table, what does not fit in a cell: the selected classes of each filtered project, every one
  of them so a run can be reproduced, and the notes of the record summary;
- the saving as a bar: `Recorded test time 22.4 s instead of 5 m 59 s  ███████████████████░ 93% saved`.

Projects are shown relative to the repository, without the file name when the folder already names
the project (`tests/Shop.Tests` for `tests/Shop.Tests/Shop.Tests.csproj`). Tables size their columns
to their content; when a table would be wider than the console, the wrapping columns (the reasons)
wrap at spaces. Nothing is cut off.

Log lines on stderr keep their format; with colour, the time and category are muted and the level is
coloured (debug muted, warning yellow, error red).

`--format json`, `paths` and `commands` are unchanged and never carry colour.

## When there is colour

`--color auto|always|never` (default `auto`) on every command decides. In `auto`:

1. `NO_COLOR` set: no colour (https://no-color.org).
2. `FORCE_COLOR` set: colour, unless it is `0`.
3. `TERM=dumb`: no colour.
4. Otherwise colour on a terminal, and on the process's own stream redirected when
   `GITHUB_ACTIONS=true`, since the Actions log viewer shows ANSI colour. A file or a pipe anywhere
   else gets none, so stdout stays machine readable.

A writer that is not the process's own stream (tests, a caller capturing the output) never gets
colour in `auto`, whatever the environment says; `--color always` still colours it.

## Themes

`--theme` or `TESTDETTA_THEME` chooses; the default is `default`. All themes use the 16 basic ANSI
colours (SGR 30–37 and 90), which every terminal draws from its own palette, so they read on dark and
light backgrounds. 256-colour and truecolor themes were left out: a fixed RGB value that reads on one
background often does not on the other.

| Role | default | colorblind | mono |
|---|---|---|---|
| Heading, number | bold | bold | bold |
| Muted (reasons, borders) | bright black | bright black | faint |
| Good (filtered, saved, passed) | green | blue | plain |
| Notice (all, license) | yellow | yellow | bold |
| Bad (failed) | bold red | bold magenta | bold underline |
| Test project | blue | cyan | plain |

`colorblind` keeps good and bad apart as blue and yellow/magenta instead of green and red, the pair
most colour vision deficiencies confuse. `mono` keeps the layout without any colour.

## Characters

Box drawing (`┌─┬┐│`) and marks (`✓ ✗ ⚠ · →`, `█░` for the bar) when the stream's encoding is UTF-8;
ASCII (`+-|`, `v x ! - ->`, `#.`) otherwise, as on the legacy Windows console.

## Width

The console's width on a terminal; `COLUMNS` when set; 120 otherwise (CI logs are wide).

## Code

- `ConsoleStyle` (`src/TestDetta.Cli/ConsoleStyle.cs`): `ConsoleStyle.For(DisplaySettings, environment,
  StreamKind, Encoding, width)` makes the decisions above; `Paint(Role, text)` styles text. `Role`
  names what text means, `ConsoleTheme` maps roles to SGR codes, `Glyphs` holds the characters.
- `TextTable` (`TextTable.cs`): boxed tables with wrapping columns.
- `TextReport` (`TextReport.cs`): the reports of `td affected`, `td test` and `td record`.
- `CliApp.StyleFor` builds the style of stdout and stderr; `StderrLoggerFactory.Style` colours log lines.
- No library: the whole feature is a few hundred lines, against Spectre.Console's dependency tree
  (CONTRIBUTING: no large libraries for small jobs).

## Tests

`ConsoleOutputTests`: the colour rules (terminal, redirect, capture, GitHub Actions, `NO_COLOR`,
`FORCE_COLOR`, `TERM=dumb`, the option winning), theme choice, ASCII for a non-UTF-8 stream and the
width, `Paint` per theme, table sizing and wrapping (every line exactly as wide as the table, the
wrapped text whole again when joined), ASCII tables, project names, an end-to-end `td affected` that is
plain by default, coloured with `--color always`, and JSON with no escape code even then, and the
option parsing.

## Not changed

The shadow-mode and audit reports keep their own text; they can move to the same tables later.
