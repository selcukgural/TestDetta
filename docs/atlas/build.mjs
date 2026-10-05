// Builds docs/atlas/testdetta-atlas.html, a single self-contained page, from the shell and the content files.
//   node docs/atlas/build.mjs           write the page
//   node docs/atlas/build.mjs --check   exit 1 when the page is not up to date with its sources
import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const read = file => readFileSync(join(here, file), "utf8");

// Content files share helpers defined in the first one, so their order matters.
const content = ["content/1-overview-record.js", "content/2-test-eval-catalog.js", "content/3-coverage-map.js"];
const page = [
  read("shell/head.html"),
  read("shell/body.html"),
  "<script>\n",
  ...content.map(read),
  read("shell/atlas.js"),
  "\nAtlas.start([CH_OVERVIEW, CH_RECORD, CH_TEST, CH_MAP, CH_EVAL], CATALOG);\n</script>\n",
].join("\n");

const out = join(here, "testdetta-atlas.html");
if (process.argv.includes("--check")) {
  const current = existsSync(out) ? readFileSync(out, "utf8") : "";
  if (current !== page) {
    console.error("docs/atlas/testdetta-atlas.html is out of date: run node docs/atlas/build.mjs");
    process.exit(1);
  }
  console.log("atlas up to date");
} else {
  // Fails on a syntax error before anything is written.
  new Function(page.split("<script>\n")[1].split("</script>")[0]);
  writeFileSync(out, page);
  console.log(`wrote ${out} (${page.length} bytes)`);
}
