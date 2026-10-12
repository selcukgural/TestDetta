# The lab

`tools/TestDetta.Lab` answers the questions we ask while developing TestDetta, the same way every time. Each one used
to be a script written on the spot: a Python pass over a map's JSON, a scratch repository with a license variable set
so `td affected` would narrow, a comparison of two `synthetic.json` files. Owner's decision, 2026-10-12: a small lab,
command line first, for our own use; nothing of it ships.

```
dotnet run --project tools/TestDetta.Lab -- who <map.json | maps directory> <pattern>
dotnet run --project tools/TestDetta.Lab -- whatif <repository> [--base <ref>]
dotnet run --project tools/TestDetta.Lab -- ab <run A> <run B>
```

- **who**: every relation a map records to what the pattern names (`Type.Member`, `Type`, an output or a file path,
  `*` for any text): the classes and fixtures that ran it, outside every test or while initializing; the buckets that
  read a value it built (`builtValues`); the ones that loaded, loaded by name or scanned an output; the ones that read a
  file. It reads maps of the current format only (a recording cache folder or `.testdetta/coverage`).
- **whatif**: what the working tree's change against the base selects with the repository's maps, with every class's
  decision kind and evidence. No license check.
- **ab**: two evaluation runs mutant by mutant: common mutants and the ones only one side has (a comparison on one
  sample has none, docs/evaluation.md "Seeds and replays"), mean shares, narrower and wider changes with the largest
  ones listed, and misses on either side.

Next, when we need them: a `whatif --run` that runs the selected classes and the whole suite and names a miss, the
shape mutants (feat/shape-mutants) as changes `whatif` applies, and a page that shows the same answers.
