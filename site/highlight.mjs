// Syntax highlighting at build time, so pages need no highlighting script and the CSP stays strict.
//
// Every <pre> without markup of its own is looked at once the page is filled in: its language comes
// from data-lang, or is recognised from the text (YAML, JSON, shell, a Jenkinsfile). Output such as
// logs and usage text is left alone. Because it works on the built page, translations get the same
// colours without carrying any markup. Blocks that already contain <span>s (diffs, terminal replays)
// are hand-made and skipped.
//
// data-lines on the <pre> wraps every line in <span class="line"> for line numbers, and
// data-mark="1-4:a,6-11:b" adds mark-a or mark-b to those lines.

const decode = (html) =>
  html.replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&amp;/g, "&");
const encode = (text) => text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
const token = (cls, text) => `<span class="tk-${cls}">${encode(text)}</span>`;

// Splits a line into tokens with one regular expression of named groups; text between matches stays
// plain. The first group that matches names the token class.
function tokenize(line, pattern) {
  let out = "";
  let last = 0;
  for (const match of line.matchAll(pattern)) {
    out += encode(line.slice(last, match.index));
    const [cls, text] = Object.entries(match.groups).find(([, value]) => value !== undefined);
    out += token(cls, text);
    last = match.index + match[0].length;
  }
  return out + encode(line.slice(last));
}

const shared = String.raw`(?<var>\$\{\{[^}]*\}\}|\$[A-Za-z_][A-Za-z0-9_]*|\$\{[^}]*\})|(?<ph><[a-z][a-z|-]*>)`;

const yamlValue = new RegExp(String.raw`(?<com>\s#.*$)|(?<str>'[^']*'|"[^"]*")|${shared}|(?<num>\b(?:true|false|null|\d+(?:\.\d+)?)\b)`, "g");

function yamlLine(line) {
  if (/^\s*#/.test(line)) {
    return token("com", line);
  }
  const key = line.match(/^(\s*)(- )?([\w.\-/]+)(:)(?=\s|$)/);
  if (!key) {
    const dash = line.match(/^(\s*)(- )(.*)$/);
    return dash ? encode(dash[1]) + token("punct", dash[2]) + tokenize(dash[3], yamlValue) : tokenize(line, yamlValue);
  }
  const [whole, indent, dash = "", name, colon] = key;
  return encode(indent) + (dash ? token("punct", dash) : "") + token("key", name) + token("punct", colon)
    + tokenize(line.slice(whole.length), yamlValue);
}

const jsonPattern = /(?<key>"[^"]*"(?=\s*:))|(?<str>"[^"]*")|(?<num>\b(?:true|false|null)\b|-?\d+(?:\.\d+)?)/g;

const shellPattern = new RegExp(String.raw`(?<com>(?:^|\s)#.*$)|(?<str>'[^']*'|"[^"]*")|${shared}|(?<flag>(?<=\s)--?[A-Za-z][\w-]*)|(?<punct>\|\||&&|\||2?>>?)`, "g");

// A shell line: the command name after any prompt or VAR=value assignments is the function token.
function shellLine(line) {
  const head = line.match(/^(\s*)((?:\$ )?)((?:[A-Z_][A-Z0-9_]*=\S*\s+)*)([\w./-]+)/);
  if (!head || /^\s*#/.test(line)) {
    return tokenize(line, shellPattern);
  }
  const [whole, indent, prompt, assignments, command] = head;
  const assigned = assignments.replace(/([A-Z_][A-Z0-9_]*)(=)(\S*)(\s+)/g, (_, name, eq, value, space) =>
    `\u0000${name}\u0001${eq}${value}${space}`);
  const assignedHtml = encode(assigned).replace(/\u0000([A-Z_0-9]+)\u0001/g, (_, name) => `<span class="tk-var">${name}</span>`);
  return encode(indent) + (prompt ? token("prompt", prompt) : "") + assignedHtml + token("fn", command)
    + tokenize(line.slice(whole.length), shellPattern);
}

const groovyPattern = /(?<com>\/\/.*$)|(?<str>'[^']*'|"[^"]*")|(?<kw>\b(?:pipeline|agent|any|environment|stages|stage|steps|sh|post|always|when|branch|changeRequest)\b)/g;

const languages = {
  yaml: yamlLine,
  json: (line) => tokenize(line, jsonPattern),
  shell: shellLine,
  groovy: (line) => tokenize(line, groovyPattern),
};

// Recognises the language of a block, or returns null for output that should stay plain.
function detect(text) {
  const lines = text.split("\n").filter((line) => line.trim() !== "");
  if (lines.length === 0) {
    return null;
  }
  if (/^\s*pipeline\s*\{/m.test(text)) {
    return "groovy";
  }
  if (/^\s*[{[]/.test(text) && /"\s*:/.test(text)) {
    return "json";
  }
  const yamlish = lines.filter((line) => /^\s*(- )?[\w.\-/]+:(\s|$)/.test(line) || /^\s*- /.test(line)).length;
  if (yamlish >= 2 && yamlish >= lines.length * 0.6 && !/;\s*$/m.test(text)) {
    return "yaml";
  }
  const commands = /^\s*(?:\$ |#|[A-Z_][A-Z0-9_]*=\S*\s+)?(?:td|dotnet|git|jq|curl|export|xargs|cd|echo|mkdir|cp|aws|gsutil|az)\b/;
  if (lines.filter((line) => commands.test(line) || /^\s*#/.test(line)).length >= Math.ceil(lines.length * 0.6)
      && !/^\s*td \w+ \[/m.test(text)) {
    return "shell";
  }
  return null;
}

// Parses data-mark="1-4:a,6-11:b" into a line number → mark name lookup.
function marks(spec) {
  const byLine = new Map();
  for (const part of (spec ?? "").split(",").filter(Boolean)) {
    const [range, name] = part.split(":");
    const [from, to = from] = range.split("-").map(Number);
    for (let line = from; line <= to; line++) {
      byLine.set(line, name);
    }
  }
  return byLine;
}

const attribute = (attributes, name) => attributes.match(new RegExp(`${name}="([^"]*)"`))?.[1];

export function highlight(html) {
  return html.replace(/<pre([^>]*)>([\s\S]*?)<\/pre>/g, (whole, attributes, body) => {
    const inner = body.match(/^<code([^>]*)>([\s\S]*)<\/code>$/);
    const content = inner ? inner[2] : body;
    if (content.includes("<span") || content.includes("<a ")) {
      return whole;
    }
    const text = decode(content);
    const lang = attribute(attributes, "data-lang") ?? detect(text);
    if (!lang || !languages[lang]) {
      return whole;
    }
    const lineMarks = marks(attribute(attributes, "data-mark"));
    const numbered = attributes.includes("data-lines");
    const lines = text.split("\n").map((line, index) => {
      const coloured = languages[lang](line);
      if (!numbered) {
        return coloured;
      }
      const mark = lineMarks.get(index + 1);
      return `<span class="line${mark ? ` mark-${mark}` : ""}">${coloured || " "}</span>`;
    });
    const joined = lines.join(numbered ? "" : "\n");
    const code = inner ? `<code${inner[1]}>${joined}</code>` : joined;
    return `<pre${attributes} data-highlighted="${lang}">${code}</pre>`;
  });
}
