// Builds the static product site into dist/, in every language. No dependencies.
//
// English pages in src/ are the source: HTML fragments with a front-matter comment, wrapped in a
// layout from partials/. Each other language has the same pages under i18n/<lang>/, and its
// interface strings in i18n/strings/<lang>.json. {{key}} is replaced from site.json (product name,
// links, contact) and the language's strings, so the name and links change in one place.
//
// A translation records the English page it was made from (`source: <hash>` in its front matter).
// The build fails when a translation is missing or its English page changed since, so no language
// can fall behind unnoticed. `--allow-stale` builds anyway, using English for those pages.
//
//   node build.mjs                 build into dist/
//   node build.mjs --serve         build, then serve dist/ on http://localhost:4321
//   node build.mjs --hashes        print the source hash of every English page
import { readFileSync, writeFileSync, mkdirSync, rmSync, readdirSync, statSync, copyFileSync, existsSync } from "node:fs";
import { createHash } from "node:crypto";
import { createServer } from "node:http";
import { join, dirname, relative, extname, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { highlight } from "./highlight.mjs";

const root = dirname(fileURLToPath(import.meta.url));
const src = join(root, "src");
const dist = join(root, "dist");
const site = JSON.parse(readFileSync(join(root, "site.json"), "utf8"));

// English first: it is the source and is served at the root; the others under /<code>/.
const languages = ["en", "tr"];

// Docs navigation, in reading order: [path, string key of its title, string key of its group]. The
// sidebar shows a heading wherever the group changes; the pager walks the list in this order.
const docsNav = [
  ["/docs/", "docGettingStarted", "navGroupStart"],
  ["/docs/examples/", "docExamples", "navGroupStart"],
  ["/docs/validate/", "docValidate", "navGroupStart"],
  ["/docs/how-it-works/", "docHowItWorks", "navGroupConcepts"],
  ["/docs/what-runs-everything/", "docWhatRunsEverything", "navGroupConcepts"],
  ["/docs/test-configuration/", "docTestConfiguration", "navGroupConcepts"],
  ["/docs/time-saved/", "docTimeSaved", "navGroupConcepts"],
  ["/docs/github-actions/", "docGitHubActions", "navGroupSetup"],
  ["/docs/other-ci/", "docOtherCi", "navGroupSetup"],
  ["/docs/compatibility/", "docCompatibility", "navGroupSetup"],
  ["/docs/cli/", "docCli", "navGroupReference"],
  ["/docs/troubleshooting/", "docTroubleshooting", "navGroupReference"],
  ["/docs/security/", "docSecurity", "navGroupReference"],
  ["/docs/license/", "docLicense", "navGroupReference"],
  ["/docs/limitations/", "docLimitations", "navGroupReference"],
];

const escapeHtml = (text) =>
  String(text).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);

// Replaces {{key}}; an unknown key fails the build rather than ship "{{key}}". `escape` is false for
// front-matter values, which the shell escapes once more on the way in.
const fill = (text, values, file, escape = true) =>
  text.replace(/\{\{(\w+)\}\}/g, (_, key) => {
    if (!(key in values)) {
      throw new Error(`${file}: unknown placeholder {{${key}}}`);
    }
    return escape ? escapeHtml(values[key]) : String(values[key]);
  });

const hash = (text) => createHash("sha256").update(text).digest("hex").slice(0, 12);

// Stylesheets and scripts are linked with a hash of their content (site.css?v=…), so a deploy never
// pairs new pages with a stylesheet or script a browser or CDN still holds from the previous one.
const versioned = new Map();
const versionAssets = (html) =>
  html.replace(/(href|src)="\/assets\/([\w.-]+\.(?:css|js))"/g, (_, attr, file) => {
    if (!versioned.has(file)) {
      versioned.set(file, hash(readFileSync(join(src, "assets", file))).slice(0, 8));
    }
    return `${attr}="/assets/${file}?v=${versioned.get(file)}"`;
  });

// Front matter: a first-line comment such as <!-- title: License; layout: docs; description: ... -->.
function parse(text) {
  const match = text.match(/^<!--([\s\S]*?)-->\s*/);
  const meta = { layout: "page" };
  if (match) {
    for (const part of match[1].split(";")) {
      const at = part.indexOf(":");
      if (at > 0) {
        meta[part.slice(0, at).trim()] = part.slice(at + 1).trim();
      }
    }
  }
  return { meta, body: match ? text.slice(match[0].length) : text };
}

// The URL a source page is served at: index.html at its folder, any other page as a folder of its own.
function urlFor(relativePath) {
  const path = "/" + relativePath.split(sep).join("/");
  return path.endsWith("/index.html") ? path.slice(0, -"index.html".length) : path.replace(/\.html$/, "/");
}

const prefix = (lang) => (lang === "en" ? "" : `/${lang}`);

// Site-absolute links of a page in another language point into that language; assets stay shared.
const localizeLinks = (html, lang) =>
  lang === "en" ? html : html.replace(/href="\/(?!assets\/|\/)/g, `href="${prefix(lang)}/`);

function docsSidebar(url, strings) {
  return docsNav
    .map(([href, key, group], index) => {
      const heading = index === 0 || docsNav[index - 1][2] !== group
        ? `<li class="nav-group">${escapeHtml(strings[group])}</li>`
        : "";
      return `${heading}<li><a href="${href}"${href === url ? ' aria-current="page"' : ""}>${escapeHtml(strings[key])}</a></li>`;
    })
    .join("\n          ");
}

function docsPager(url, strings) {
  const at = docsNav.findIndex(([href]) => href === url);
  if (at < 0) {
    return "";
  }
  const link = (index, cls, label) =>
    `<a class="${cls}" href="${docsNav[index][0]}"><span>${escapeHtml(label)}</span>${escapeHtml(strings[docsNav[index][1]])}</a>`;
  const previous = at > 0 ? link(at - 1, "pager-prev", strings.previous) : "<span></span>";
  const next = at < docsNav.length - 1 ? link(at + 1, "pager-next", strings.next) : "<span></span>";
  return `<nav class="pager" aria-label="${escapeHtml(strings.docsPagesLabel)}">${previous}${next}</nav>`;
}

// The language menu: the same page in every language, in a native popover, which closes on a click
// outside it or on Escape without any script.
function languageMenu(url, lang, allStrings) {
  const items = languages
    .map((code) => `<li><a href="${prefix(code)}${url}" hreflang="${code}" lang="${code}"${code === lang ? ' aria-current="true"' : ""}>${escapeHtml(allStrings[code].langName)}</a></li>`)
    .join("");
  const label = escapeHtml(allStrings[lang].language);
  return `<button type="button" class="header-control" popovertarget="languages" aria-label="${label}" title="${label}">${code(lang)}</button>`
    + `<div id="languages" class="lang-popover" popover><ul>${items}</ul></div>`;
}

const code = (lang) => lang.toUpperCase();

// The page's own address as canonical, so search engines index the trailing-slash URL and not
// /index.html, then the same page in every language for hreflang. A page with its own url (the 404
// page) is not an address anyone should land on from a search, so it is marked noindex instead.
function alternates(url, lang, indexable) {
  const base = `https://${site.domain}`;
  if (!indexable) {
    return `  <meta name="robots" content="noindex">`;
  }
  return [
    `  <link rel="canonical" href="${base}${prefix(lang)}${url}">`,
    ...languages.map((lang) => `  <link rel="alternate" hreflang="${lang}" href="${base}${prefix(lang)}${url}">`),
    `  <link rel="alternate" hreflang="x-default" href="${base}${url}">`,
  ].join("\n");
}

function walk(directory) {
  return readdirSync(directory).flatMap((name) => {
    const path = join(directory, name);
    return statSync(path).isDirectory() ? walk(path) : [path];
  });
}

function loadStrings() {
  const all = {};
  for (const lang of languages) {
    const raw = JSON.parse(readFileSync(join(root, "i18n", "strings", `${lang}.json`), "utf8"));
    const missing = Object.keys(JSON.parse(readFileSync(join(root, "i18n", "strings", "en.json"), "utf8"))).filter((k) => !(k in raw));
    if (missing.length > 0) {
      throw new Error(`i18n/strings/${lang}.json lacks ${missing.join(", ")}`);
    }
    // Strings may use site.json values ({{name}}), filled before the page sees them.
    all[lang] = Object.fromEntries(Object.entries(raw).map(([k, v]) => [k, fill(v, site, `${lang}.json`, false)]));
  }
  return all;
}

function build({ allowStale }) {
  rmSync(dist, { recursive: true, force: true });
  const partial = (name) => readFileSync(join(root, "partials", name), "utf8");
  const shell = partial("shell.html");
  const layouts = { page: partial("page.html"), docs: partial("docs.html") };
  const allStrings = loadStrings();
  const problems = [];
  const sitemapUrls = [];

  for (const file of walk(src)) {
    const relativePath = relative(src, file);
    if (extname(file) !== ".html") {
      const target = join(dist, relativePath);
      mkdirSync(dirname(target), { recursive: true });
      copyFileSync(file, target);
      continue;
    }

    const english = readFileSync(file, "utf8");
    const sourceHash = hash(english);
    // Pages with their own url (the 404 page) are not real addresses and stay out of the sitemap.
    const englishMeta = parse(english).meta;
    if (englishMeta.url === undefined) {
      sitemapUrls.push(urlFor(relativePath));
    }
    for (const lang of languages) {
      let text = english;
      if (lang !== "en") {
        const translated = join(root, "i18n", lang, relativePath);
        const translation = existsSync(translated) ? readFileSync(translated, "utf8") : null;
        const recorded = translation === null ? null : parse(translation).meta.source;
        if (translation === null || recorded !== sourceHash) {
          problems.push(`${lang}/${relativePath}: ${translation === null ? "missing" : `stale (made from ${recorded ?? "?"}, English is now ${sourceHash})`}`);
          if (!allowStale) {
            continue;
          }
        } else {
          text = translation;
        }
      }

      const { meta, body } = parse(text);
      const strings = allStrings[lang];
      const values = { ...site, ...strings, year: new Date().getFullYear() };
      const url = meta.url ?? urlFor(relativePath);
      const layout = layouts[meta.layout];
      if (!layout) {
        throw new Error(`${relativePath}: unknown layout ${meta.layout}`);
      }

      const page = fill(layout, values, `${meta.layout}.html`)
        .replace("<!--content-->", fill(body, values, relativePath))
        .replace("<!--sidebar-->", docsSidebar(url, strings))
        .replace("<!--pager-->", docsPager(url, strings));
      const pageName = meta.title ? fill(meta.title, values, relativePath, false) : site.name;
      const title = pageName === site.name ? `${site.name} — ${strings.tagline}` : `${pageName} · ${site.name}`;
      const description = fill(meta.description ?? strings.tagline, values, relativePath, false);
      const header = fill(partial("header.html"), values, "header.html");
      let html = fill(shell, { ...values, pageTitle: title, pageDescription: description }, "shell.html")
        .replace("<!--header-->", header)
        .replace("<!--footer-->", fill(partial("footer.html"), values, "footer.html"))
        .replace("<!--page-->", page)
        .replace(/<a href="([^"]+)"(?![^>]*aria-current)([^>]*class="nav-link")/g, (all, href, rest) =>
          url.startsWith(href) && href !== "/" ? `<a href="${href}" aria-current="page"${rest}` : all);
      // The language menu and alternates go in after the page's own links point into its language.
      html = localizeLinks(html, lang).replace("<!--languages-->", languageMenu(url, lang, allStrings))
        .replace("<!--alternates-->", alternates(url, lang, meta.url === undefined));
      const outDir = join(dist, prefix(lang).slice(1));
      const target = url.endsWith("/") && meta.url === undefined
        ? join(outDir, url, "index.html")
        : join(outDir, relativePath);
      mkdirSync(dirname(target), { recursive: true });
      writeFileSync(target, versionAssets(highlight(html)));
    }
  }

  if (problems.length > 0) {
    const report = `Translations to update (${problems.length}):\n  ${problems.join("\n  ")}`;
    if (!allowStale) {
      throw new Error(`${report}\nUpdate them from the English page and set their source hash (node build.mjs --hashes), or build with --allow-stale.`);
    }
    console.warn(`${report}\nBuilt with English in their place (--allow-stale).`);
  }
  writeSitemap(sitemapUrls);
  console.log(`built ${relative(process.cwd(), dist) || "dist"} in ${languages.length} languages`);
}

// sitemap.xml lists every page in every language, each with its hreflang alternates, and
// robots.txt points crawlers at it. Both are generated so a new page cannot be left out.
function writeSitemap(urls) {
  const base = `https://${site.domain}`;
  const links = (url) => languages
    .map((lang) => `<xhtml:link rel="alternate" hreflang="${lang}" href="${base}${prefix(lang)}${url}"/>`)
    .join("");
  const entries = urls.toSorted().flatMap((url) => languages.map((lang) =>
    `  <url><loc>${base}${prefix(lang)}${url}</loc>${links(url)}<xhtml:link rel="alternate" hreflang="x-default" href="${base}${url}"/></url>`));
  writeFileSync(join(dist, "sitemap.xml"), [
    `<?xml version="1.0" encoding="UTF-8"?>`,
    `<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:xhtml="http://www.w3.org/1999/xhtml">`,
    ...entries,
    `</urlset>`,
    "",
  ].join("\n"));
  writeFileSync(join(dist, "robots.txt"), `User-agent: *\nAllow: /\n\nSitemap: ${base}/sitemap.xml\n`);
}

if (process.argv.includes("--hashes")) {
  for (const file of walk(src).filter((f) => extname(f) === ".html")) {
    console.log(`${hash(readFileSync(file, "utf8"))}  ${relative(src, file)}`);
  }
  process.exit(0);
}

build({ allowStale: process.argv.includes("--allow-stale") });

if (process.argv.includes("--serve")) {
  const types = { ".html": "text/html; charset=utf-8", ".css": "text/css", ".svg": "image/svg+xml", ".js": "text/javascript", ".png": "image/png", ".ico": "image/x-icon" };
  createServer((request, response) => {
    const path = decodeURIComponent(new URL(request.url, "http://localhost").pathname);
    let file = join(dist, path.endsWith("/") ? path + "index.html" : path);
    if (!file.startsWith(dist) || !existsSync(file)) {
      const lang = path.split("/")[1];
      file = join(dist, languages.includes(lang) && lang !== "en" ? lang : "", "404.html");
      response.statusCode = 404;
    }
    response.setHeader("Content-Type", types[extname(file)] ?? "application/octet-stream");
    response.end(readFileSync(file));
  }).listen(4321, "127.0.0.1", () => console.log("serving http://localhost:4321"));
}
