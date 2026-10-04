# Product site

Static site, no dependencies: `node build.mjs` writes `dist/`, `node build.mjs --serve` also serves it
on http://localhost:4321. Pages are HTML fragments in `src/` wrapped in `partials/`; the product
name, prices, contact and legal details come from `site.json` (`{{key}}` in any page).

## Languages

English (`src/`) is the source. Turkish, German, Japanese and Spanish live in `i18n/<lang>/` with
the same paths, and the interface strings (menus, footer, docs navigation) in
`i18n/strings/<lang>.json`. English is served at `/`, the others at `/tr/`, `/de/`, `/ja/`, `/es/`.

**Every change to a page updates all five languages in the same commit.** Each translation
records the English page it was made from (`source: <hash>` in its front matter; `node build.mjs
--hashes` prints the current ones). The build fails when a translation is missing or its English
page changed since, and lists them; `--allow-stale` builds anyway with English in their place, for
local previews only. Code, command output, option names and placeholders are never translated.

## Hosting

Cloudflare Pages: root directory `site`, build command `node build.mjs`, output directory `dist`.
`src/_headers` sets the security headers (no inline styles or scripts are allowed by the CSP).
