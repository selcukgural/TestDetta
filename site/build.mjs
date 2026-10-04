// Builds the static product site into dist/. No dependencies: every page in src/ is a fragment with
// a front-matter comment, wrapped in a layout from partials/, and {{key}} is replaced from site.json,
// so the product name, prices and contact details change in one place.
//
//   node build.mjs            build into dist/
//   node build.mjs --serve    build, then serve dist/ on http://localhost:4321
import { readFileSync, writeFileSync, mkdirSync, rmSync, readdirSync, statSync, copyFileSync, existsSync } from "node:fs";
import { createServer } from "node:http";
import { join, dirname, relative, extname, sep } from "node:path";
import { fileURLToPath } from "node:url";

const root = dirname(fileURLToPath(import.meta.url));
const src = join(root, "src");
const dist = join(root, "dist");
const site = JSON.parse(readFileSync(join(root, "site.json"), "utf8"));

// Docs navigation, in reading order: [path, title].
const docsNav = [
  ["/docs/", "Getting started"],
  ["/docs/how-it-works/", "How it decides"],
  ["/docs/github-actions/", "GitHub Actions"],
  ["/docs/other-ci/", "Other CI systems"],
  ["/docs/license/", "License"],
  ["/docs/limitations/", "Limitations"],
];

const escapeHtml = (text) =>
  String(text).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);

// Replaces {{key}} with the escaped site.json value; an unknown key fails the build rather than ship "{{key}}".
const fill = (text, values, file) =>
  text.replace(/\{\{(\w+)\}\}/g, (_, key) => {
    if (!(key in values)) {
      throw new Error(`${file}: unknown placeholder {{${key}}}`);
    }
    return escapeHtml(values[key]);
  });

// As fill, unescaped: for front-matter values that the shell escapes once more on the way in.
const fillRaw = (text, values, file) =>
  text.replace(/\{\{(\w+)\}\}/g, (_, key) => {
    if (!(key in values)) {
      throw new Error(`${file}: unknown placeholder {{${key}}}`);
    }
    return String(values[key]);
  });

// Front matter: a first-line comment such as <!-- title: Pricing; layout: page; description: ... -->.
function parse(file) {
  const text = readFileSync(file, "utf8");
  const match = text.match(/^<!--([\s\S]*?)-->\s*/);
  const meta = { layout: "page", title: site.name, description: site.tagline };
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

function docsSidebar(url) {
  return docsNav
    .map(([href, title]) => `<li><a href="${href}"${href === url ? ' aria-current="page"' : ""}>${escapeHtml(title)}</a></li>`)
    .join("\n          ");
}

function docsPager(url) {
  const at = docsNav.findIndex(([href]) => href === url);
  if (at < 0) {
    return "";
  }
  const previous = at > 0 ? `<a class="pager-prev" href="${docsNav[at - 1][0]}"><span>Previous</span>${escapeHtml(docsNav[at - 1][1])}</a>` : "<span></span>";
  const next = at < docsNav.length - 1 ? `<a class="pager-next" href="${docsNav[at + 1][0]}"><span>Next</span>${escapeHtml(docsNav[at + 1][1])}</a>` : "<span></span>";
  return `<nav class="pager" aria-label="Docs pages">${previous}${next}</nav>`;
}

function walk(directory) {
  return readdirSync(directory).flatMap((name) => {
    const path = join(directory, name);
    return statSync(path).isDirectory() ? walk(path) : [path];
  });
}

function build() {
  rmSync(dist, { recursive: true, force: true });
  const partial = (name) => readFileSync(join(root, "partials", name), "utf8");
  const shell = partial("shell.html");
  const layouts = { page: partial("page.html"), docs: partial("docs.html") };
  for (const file of walk(src)) {
    const relativePath = relative(src, file);
    if (extname(file) !== ".html") {
      const target = join(dist, relativePath);
      mkdirSync(dirname(target), { recursive: true });
      copyFileSync(file, target);
      continue;
    }

    const { meta, body } = parse(file);
    const url = meta.url ?? urlFor(relativePath);
    const layout = layouts[meta.layout];
    if (!layout) {
      throw new Error(`${relativePath}: unknown layout ${meta.layout}`);
    }

    const values = { ...site, year: new Date().getFullYear() };
    const page = layout
      .replace("<!--content-->", fill(body, values, relativePath))
      .replace("<!--sidebar-->", docsSidebar(url))
      .replace("<!--pager-->", docsPager(url));
    const pageName = fillRaw(meta.title, values, relativePath);
    const title = pageName === site.name ? `${site.name} — ${site.tagline}` : `${pageName} · ${site.name}`;
    const description = fillRaw(meta.description, values, relativePath);
    const html = fill(shell, { ...values, pageTitle: title, pageDescription: description }, "shell.html")
      .replace("<!--header-->", fill(partial("header.html"), values, "header.html"))
      .replace("<!--footer-->", fill(partial("footer.html"), values, "footer.html"))
      .replace("<!--page-->", page)
      .replace(/<a href="([^"]+)"(?![^>]*aria-current)([^>]*class="nav-link")/g, (all, href, rest) =>
        url.startsWith(href) && href !== "/" ? `<a href="${href}" aria-current="page"${rest}` : all);
    const target = url.endsWith("/") && meta.url === undefined && relativePath !== "404.html"
      ? join(dist, url, "index.html")
      : join(dist, relativePath);
    mkdirSync(dirname(target), { recursive: true });
    writeFileSync(target, html);
  }
  console.log(`built ${relative(process.cwd(), dist) || "dist"}`);
}

build();

if (process.argv.includes("--serve")) {
  const types = { ".html": "text/html; charset=utf-8", ".css": "text/css", ".svg": "image/svg+xml", ".js": "text/javascript", ".png": "image/png", ".ico": "image/x-icon" };
  createServer((request, response) => {
    const path = decodeURIComponent(new URL(request.url, "http://localhost").pathname);
    let file = join(dist, path.endsWith("/") ? path + "index.html" : path);
    if (!file.startsWith(dist) || !existsSync(file)) {
      file = join(dist, "404.html");
      response.statusCode = 404;
    }
    response.setHeader("Content-Type", types[extname(file)] ?? "application/octet-stream");
    response.end(readFileSync(file));
  }).listen(4321, "127.0.0.1", () => console.log("serving http://localhost:4321"));
}
