// Generates the X (Twitter) profile and header SVGs from the site's TD monogram and palette.
import { writeFileSync } from "node:fs";

const ink = "#14171a", green = "#0b6b4f", mint = "#7fd6b0", cream = "#f7f5f0", muted = "#535a60", cell = "#e3ded3";
const mono = (bar, arc) =>
  `<g fill="${bar}"><rect x="4" y="8" width="25" height="10"/><rect x="11.5" y="8" width="10" height="48"/></g><g fill="${arc}"><path d="M31 8 H38 A24 24 0 0 1 38 56 H31 V46 H38 A14 14 0 0 0 38 18 H31 Z"/></g>`;

// Profile: 400x400, X crops it to a circle, so the mark sits well inside the inscribed circle.
// The monogram spans x 4..62, y 8..56 of its 64 box; centre (33, 32).
const profile = (bg, bar, arc) => {
  const s = 3.9;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="400" height="400" viewBox="0 0 400 400"><rect width="400" height="400" fill="${bg}"/><g transform="translate(${200 - 33 * s} ${200 - 32 * s}) scale(${s})">${mono(bar, arc)}</g></svg>`;
};
writeFileSync("x-profile-dark.svg", profile(ink, cream, mint));
writeFileSync("x-profile-light.svg", profile(cream, ink, green));

// Header: 1500x500. The avatar covers the bottom-left corner and phones crop top and bottom,
// so text stays in x >= 90, y 120..360 and the grid in the right half.
// The grid is the OpenTelemetry .NET run shown on the site: 274 test classes, 8 of them run.
const total = 274, run = 8, cols = 23, pitch = 24, size = 19;
let seed = 7;
const rnd = () => (seed = (seed * 1103515245 + 12345) % 2147483648) / 2147483648;
const picked = new Set();
while (picked.size < run) picked.add(Math.floor(rnd() * total));

const header = (bg, fg, accent, sub, off) => {
  const gx = 1500 - 90 - cols * pitch + (pitch - size), gy = 100;
  let cells = "";
  for (let i = 0; i < total; i++) {
    const x = gx + (i % cols) * pitch, y = gy + Math.floor(i / cols) * pitch;
    cells += `<rect x="${x}" y="${y}" width="${size}" height="${size}" rx="4" fill="${picked.has(i) ? accent : off}"/>`;
  }
  const font = `font-family="IBM Plex Sans"`;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="1500" height="500" viewBox="0 0 1500 500">
<rect width="1500" height="500" fill="${bg}"/>
<text ${font} font-weight="700" font-size="58" fill="${fg}" letter-spacing="-1.4"><tspan x="90" y="168">Run only the .NET tests</tspan><tspan x="90" y="238">your change can <tspan fill="${accent}">break.</tspan></tspan></text>
<text x="92" y="300" ${font} font-size="26" fill="${sub}">Test impact analysis for .NET · testdetta.com</text>
${cells}
<text x="${gx}" y="${gy + 12 * pitch + 30}" ${font} font-weight="600" font-size="22" fill="${accent}">8 of 274 test classes ran · 22.4 s instead of 5 m 59 s</text>
</svg>`;
};
writeFileSync("x-header-light.svg", header(cream, ink, green, muted, cell));
writeFileSync("x-header-dark.svg", header(ink, cream, mint, "#a3aab0", "#262b30"));
