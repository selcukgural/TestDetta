# Evaluation corpus

`corpus.json` lists the repositories `sift-eval` measures Sift on, with what each one needs to
build and test. Run all of them (hours) or some:

```sh
dotnet run --project tools/Sift.Eval -c Release -- --corpus eval/corpus.json --out ~/sift-corpus
dotnet run --project tools/Sift.Eval -c Release -- --corpus eval/corpus.json --only polly,nodatime --out ~/sift-corpus
dotnet run --project tools/Sift.Eval -c Release -- --summarize ~/sift-corpus --corpus eval/corpus.json
```

Each repository gets `<out>/<name>/` with its clone, cached coverage maps, logs and reports
(`synthetic.md`, `results.md` for history commits); `<out>/summary.md` adds them up, per tag and in
total, with a 95% upper bound of the miss rate. The summary is rewritten after every repository,
and an existing clone and its maps are reused, so a stopped run can be continued.

## What a run measures

- **Synthetic changes** (`synthetic`, default 100 per repository): coverage is recorded once at the
  branch head; for each sampled member Sift sees a neutral source edit and selects tests, while the
  compiled member throws. A test class that fails but was not selected is a miss. 80% of the sample
  comes from covered members, 20% from members no recorded test ran.
- **History** (`merges`): the latest commits, each recorded at its parent and compared with the full
  suite; this is where csproj, package and props changes are measured.

## Environment

Set these before a run when the corpus needs them:

- `DOTNET_ROOT` and `PATH` to an SDK that satisfies every repository's `global.json`
  (`DOTNET_MULTILEVEL_LOOKUP=0` keeps a machine-wide SDK out).
- `EnableWindowsTargeting=true` lets projects that reference Windows-targeting projects build on
  macOS and Linux.

## Adding a repository

Add an entry with `name`, `url` and `tags` naming what it exercises (`reflection`, `pinvoke`,
`source-generator`, `di-scanning`, `serialization`, `static-state`, `native`, `large`, ...), plus
`setup` (e.g. `dotnet tool restore`), `framework` or `projects` when the defaults do not build it.
