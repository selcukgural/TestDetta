# Product site

Static site, no dependencies: `node build.mjs` writes `dist/`, `node build.mjs --serve` also serves it
on http://localhost:4321. Pages are HTML fragments in `src/` wrapped in `partials/`; the product
name, prices, contact and legal details come from `site.json` (`{{key}}` in any page).

Cloudflare Pages: root directory `site`, build command `node build.mjs`, output directory `dist`.
`src/_headers` sets the security headers (no inline styles or scripts are allowed by the CSP).
