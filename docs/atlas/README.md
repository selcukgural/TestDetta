# TestDetta Atlas

An interactive, code-level walkthrough of TestDetta, in Turkish: every step of recording, analysis and
selection, the coverage-map decisions, and the corpus evaluation, with the class and method, file and
line, inputs, outputs, purpose and code excerpts of each step. An IDE-style page: Explorer (chapters
and steps), four views (diagram, call tree, code, reading), an Inspector, a ⌘K palette and keyboard
shortcuts.

Open `testdetta-atlas.html` in a browser; it is one self-contained file. A published copy lives at
https://claude.ai/artifact/QMEzAMp7kTpSoZTgav35Ht (private to the owner).

## Keep it in sync

The atlas describes the code. **A change to a flow it documents updates the atlas in the same change**:
a renamed or moved class or method, a new step or rule, a changed signature, input or output, or a
design doc that changes one of these flows. Line numbers (`file.cs:123`) are refreshed when the
method moves.

| File | Chapters |
|---|---|
| `content/1-overview-record.js` | 1 Genel bakış, 2 td record |
| `content/2-test-eval-catalog.js` | 3 td test · seçim, 5 Değerlendirme, 6 Bileşen kataloğu |
| `content/3-coverage-map.js` | 4 Kapsam haritası |
| `shell/atlas.js` | The page: views, Inspector, palette; `CALLS` holds each chapter's call tree |
| `shell/head.html`, `shell/body.html` | Styles and layout |

A chapter is `{ id, short, eyebrow, title, lead, note?, width, height, zones, nodes, edges, steps }`. A
step is `{ title, nodes, edges, who, file, input, output, purpose, body }`: `nodes` and `edges` light up
in the diagram (the token runs along the edges in order); `who`, `file`, `input`, `output` and `purpose`
fill the Inspector; code blocks in `body` (`C(...)`) go to the Code view, the rest to the Inspector.
Markup in content is limited to `b, i, code, ul, li, p, br, span, kbd`; anything else is dropped.

Build and check:

```
node docs/atlas/build.mjs          # writes testdetta-atlas.html
node docs/atlas/build.mjs --check  # fails when the page is older than its sources
```
