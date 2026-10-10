// Publishes dist/ to Firebase Hosting through its REST API. No dependencies.
//
// firebase-tools would pull several hundred packages into the job that holds the deploy
// credentials; the five calls below are all a static site needs. firebase.json stays the one place
// for the hosting settings: this script sends the same settings, so `firebase deploy` from a
// machine and a deploy from CI publish the same site. A setting it does not know stops the deploy
// instead of being dropped.
//
//   FIREBASE_SITE=<site id> GOOGLE_ACCESS_TOKEN=$(gcloud auth print-access-token) node deploy.mjs
//
// Run `node build.mjs` first. The new version goes live only after every file is uploaded, so a
// failed run leaves the current site in place.
import { readFileSync, readdirSync, statSync, existsSync } from "node:fs";
import { createHash } from "node:crypto";
import { gzipSync } from "node:zlib";
import { join, dirname, relative, sep } from "node:path";
import { fileURLToPath } from "node:url";

const root = dirname(fileURLToPath(import.meta.url));
const api = "https://firebasehosting.googleapis.com/v1beta1";
const site = process.env.FIREBASE_SITE;
const token = process.env.GOOGLE_ACCESS_TOKEN;
if (!site || !token) {
  throw new Error("Set FIREBASE_SITE and GOOGLE_ACCESS_TOKEN.");
}

const hosting = JSON.parse(readFileSync(join(root, "firebase.json"), "utf8")).hosting;
const dist = join(root, hosting.public);
if (!existsSync(join(dist, "index.html"))) {
  throw new Error(`${hosting.public}/index.html is missing; run node build.mjs first.`);
}

// firebase.json's hosting section in the shape the API takes.
function versionConfig() {
  const config = {};
  for (const [key, value] of Object.entries(hosting)) {
    switch (key) {
      case "public":
        break;
      case "trailingSlash":
        config.trailingSlashBehavior = value ? "ADD" : "REMOVE";
        break;
      case "headers":
        config.headers = value.map((rule) => ({
          glob: rule.source,
          headers: Object.fromEntries(rule.headers.map((header) => [header.key, header.value])),
        }));
        break;
      case "redirects":
        config.redirects = value.map((rule) => ({
          regex: sourceRegex(rule.source),
          location: rule.destination,
          statusCode: rule.type ?? 301,
        }));
        break;
      default:
        throw new Error(`firebase.json: hosting.${key} is not supported by deploy.mjs; add it there.`);
    }
  }
  return config;
}

// A firebase.json source path ("/de/:path*") as the RE2 expression the API takes ("^/de/(?P<path>.*)$"): ":name*" captures
// the rest of the path and ":name" one segment, under names the destination refers to the same way.
function sourceRegex(source) {
  const parts = source.split(/(:[A-Za-z_][A-Za-z0-9_]*\*?)/);
  const pattern = parts.map((part) => {
    const capture = part.match(/^:([A-Za-z_][A-Za-z0-9_]*)(\*?)$/);
    if (capture) {
      return capture[2] ? `(?P<${capture[1]}>.*)` : `(?P<${capture[1]}>[^/]+)`;
    }
    return part.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  });
  return `^${pattern.join("")}$`;
}

function walk(directory) {
  const files = [];
  for (const name of readdirSync(directory)) {
    const path = join(directory, name);
    if (statSync(path).isDirectory()) {
      files.push(...walk(path));
    } else {
      files.push(path);
    }
  }
  return files;
}

// One API call; a refusal stops the deploy with Google's message, which never contains the token.
async function call(method, url, body, contentType = "application/json") {
  const response = await fetch(url, {
    method,
    headers: { Authorization: `Bearer ${token}`, "Content-Type": contentType },
    body: body === undefined || contentType !== "application/json" ? body : JSON.stringify(body),
  });
  if (!response.ok) {
    throw new Error(`${method} ${url.split("?")[0]}: ${response.status} ${await response.text()}`);
  }
  const text = await response.text();
  return text ? JSON.parse(text) : {};
}

// Hosting identifies files by the SHA-256 of their gzipped bytes and asks only for the ones it
// does not hold yet, so an unchanged file is never uploaded again.
const files = new Map();
for (const file of walk(dist)) {
  const gzipped = gzipSync(readFileSync(file), { level: 9 });
  const path = "/" + relative(dist, file).split(sep).join("/");
  files.set(path, { gzipped, hash: createHash("sha256").update(gzipped).digest("hex") });
}

const version = await call("POST", `${api}/sites/${site}/versions`, { config: versionConfig() });
const byHash = new Map([...files.values()].map((file) => [file.hash, file.gzipped]));
const paths = [...files.keys()];
for (let start = 0; start < paths.length; start += 1000) {
  const chunk = Object.fromEntries(paths.slice(start, start + 1000).map((path) => [path, files.get(path).hash]));
  const { uploadRequiredHashes = [], uploadUrl } = await call("POST", `${api}/${version.name}:populateFiles`, { files: chunk });
  for (let i = 0; i < uploadRequiredHashes.length; i += 8) {
    await Promise.all(uploadRequiredHashes.slice(i, i + 8).map((hash) =>
      call("POST", `${uploadUrl}/${hash}`, byHash.get(hash), "application/octet-stream")));
  }
  console.log(`${Object.keys(chunk).length} files, ${uploadRequiredHashes.length} uploaded`);
}

await call("PATCH", `${api}/${version.name}?update_mask=status`, { status: "FINALIZED" });
await call("POST", `${api}/sites/${site}/releases?versionName=${version.name}`);
console.log(`released ${version.name}`);
