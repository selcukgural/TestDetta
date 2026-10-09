// IDE shell for the atlas: Explorer (chapters → steps), four center views (Şema, Çağrı ağacı, Kod, Okuma),
// Inspector, ⌘K palette, player and keyboard shortcuts. Content comes from the CH_* chapter objects and CATALOG.

// Call trees (Çağrı ağacı): [call, depth, step index]. Positions show order and nesting, not time.
const CALLS = {
  record: [
    ["CliApp.RecordCoverage", 0, 0], ["ProjectGraphLoader.Load", 1, 0], ["CoverageRecorder.Record", 1, 1],
    ["dotnet build · FindTargetPath", 2, 2], ["GeneratedSourceReader.Read · ClassLocationReader.Read", 2, 3],
    ["AssemblyInstrumenter.Instrument", 2, 4], ["FileReadCalls.Wrap", 3, 4], ["new RuntimeDeployment", 2, 5],
    ["RunShards → RunShard", 2, 6], ["TestDettaTracker.Hit / Enter…", 3, 7], ["TestDettaTracker.WriteResults", 3, 8],
    ["BuildMap", 2, 9], ["FileReadPaths.ResolveAll", 3, 9], ["Untrusted · Skipped · Reported", 2, 10],
    ["finally: Restore", 2, 11], ["CoverageMapStore.Write", 1, 10],
    ["IncrementalRecording.Plan", 1, 12], ["AnalysisPipeline.Run (base A)", 2, 12], ["CoverageRecorder.RecordClasses · ClassShards", 1, 12],
    ["CoverageMapMerge.Merge · UnobservedBy", 1, 12], ["MapDivergence.Find", 1, 12],
  ],
  test: [
    ["CliApp.Run", 0, 0], ["AnalysisPipeline.Run", 1, 1], ["GitClient.ChangedFiles · MergeBase", 2, 1],
    ["ProjectGraphLoader.Load", 2, 2], ["MsBuildProjectEvaluator.Evaluate", 3, 2], ["CodeChangeReader.Read", 2, 3],
    ["CodeChangeDetector.Detect", 3, 3], ["ImpactAnalyzer.Analyze", 2, 4], ["LoadCoverageMaps · MapDrift", 2, 5],
    ["TestSelector.Select", 2, 6], ["FindFileReaders", 3, 6], ["WithoutDriftedMaps · FindProjectLevelChanges", 3, 7], ["BuildOutputUse.Find · AddOutputUsers · AddLoadedOutputUsers", 4, 7],
    ["CompareGeneratorOutput", 3, 8], ["SourceIndexBuilder.Build", 3, 9], ["CoverageSelection.TryResolve", 3, 10],
    ["Trace (ImpactPropagation) · Bridge", 3, 11], ["Decide · WithFileReaders", 3, 12], ["Estimate · Plan", 2, 13],
    ["CliApp.RunTests", 1, 14], ["TestInvocationBuilder.Build", 2, 14], ["TestRunner.RunAll", 2, 14], ["MarkdownSummary.Write", 2, 14],
  ],
  harita: [
    ["new CoverageIndex(map, …)", 0, 0], ["ClassesUsing (fixtures)", 1, 1], ["_ambient", 1, 2], ["AddUsers (builtValues)", 1, 3],
    ["initialization − linked", 1, 4], ["tek kova kuralı (kaldırıldı)", 1, 5], ["externalCallbacks", 1, 6],
    ["TryResolve · S1 gövde", 0, 7], ["TryResolve · S2 eklendi", 0, 8], ["TryResolve · S3 .cctor", 0, 9], ["TryResolve · S4 .ctor", 0, 10],
    ["TryResolve · S5 ambient", 0, 11], ["TryResolve · S6 önbellek", 0, 12], ["TryResolve · S7 singleton", 0, 13],
    ["TryResolve · S8 metaveri", 0, 14], ["TryResolve · S9 harita yok", 0, 15], ["TryResolve(reached) · S10", 0, 16],
    ["TryResolve · S12 görülmemiş (Unobserved)", 0, 20],
  ],
  eval: [
    ["BatchRunner.Run", 0, 0], ["EvalApp.RunOne", 1, 1], ["SyntheticEvaluator.Evaluate", 2, 1], ["EvalSteps.RecordAt", 3, 1],
    ["TargetSampler.Sample · DataFileSampler.Sample", 3, 2], ["RunBaseline / StoredBaselineFor", 3, 3], ["Select → Analyze (her hedef)", 3, 4],
    ["GeneratorWorkspace.Read", 4, 4], ["TrySchema · Parallelism", 3, 5], ["Run (her mutant, paralel)", 3, 6], ["Apply · RunAll", 4, 6],
    ["Confirm", 4, 7], ["Store · SyntheticReport · BatchSummary", 3, 8],
  ],
};

(function () {
  const SVGNS = "http://www.w3.org/2000/svg";
  const reduced = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const $ = s => document.querySelector(s);
  const h = (tag, cls, text) => { const n = document.createElement(tag); if (cls) n.className = cls; if (text != null) n.textContent = text; return n; };
  const sv = (tag, attrs = {}, parent) => { const n = document.createElementNS(SVGNS, tag); for (const [k, v] of Object.entries(attrs)) n.setAttribute(k, v); if (parent) parent.appendChild(n); return n; };
  // Authored content carries a little markup. It is parsed in an inert template and rebuilt through DOM
  // APIs with an allowlist (tags below, class on code and span only), never written with innerHTML.
  const ALLOWED = new Set(["B", "I", "STRONG", "EM", "CODE", "UL", "LI", "P", "BR", "SPAN", "KBD"]);
  function sanitizedNodes(source) {
    const out = document.createDocumentFragment();
    for (const node of source.childNodes) {
      if (node.nodeType === Node.TEXT_NODE) { out.append(document.createTextNode(node.textContent)); continue; }
      if (node.nodeType !== Node.ELEMENT_NODE) continue;
      if (!ALLOWED.has(node.tagName)) { out.append(sanitizedNodes(node)); continue; }
      const copy = document.createElement(node.tagName.toLowerCase());
      const cls = node.getAttribute("class");
      if (cls && (node.tagName === "CODE" || node.tagName === "SPAN")) copy.className = cls.replace(/[^\w -]/g, "");
      copy.append(sanitizedNodes(node));
      out.append(copy);
    }
    return out;
  }
  function setHtml(el, html) {
    const t = document.createElement("template");
    t.innerHTML = html || ""; // inert: a template's content is never rendered or run
    el.replaceChildren(sanitizedNodes(t.content));
  }
  const store = { get(k) { try { return localStorage.getItem(k); } catch { return null; } }, set(k, v) { try { localStorage.setItem(k, v); } catch { /* unavailable */ } } };

  // ---------- content helpers ----------
  const textOf = html => { const t = document.createElement("template"); t.innerHTML = html || ""; return t.content.textContent.replace(/\s+/g, " ").trim(); };
  function split(body) {
    const t = document.createElement("template");
    t.innerHTML = body || "";
    const code = [...t.content.querySelectorAll("code.sig")].map(n => { const s = n.textContent; n.remove(); return s; });
    return { code, rest: t.content };
  }
  const TOKENS = /(\/\/[^\n]*|^\s*#[^\n]*)|(\$?"(?:[^"\\\n]|\\.)*")|\b(\d+(?:\.\d+)?)\b|\b(var|new|public|private|internal|static|return|if|else|foreach|in|is|null|true|false|bool|int|string|void|using|throw|try|finally|catch|readonly|sealed|class|record|with|out|ref|await|async|call|ldc\.i4)\b|\b([A-Z][A-Za-z0-9_]*)\b/gm;
  function highlight(src) {
    const frag = document.createDocumentFragment();
    for (const line of src.split("\n")) {
      const ln = h("span", "ln");
      let last = 0;
      for (const m of line.matchAll(TOKENS)) {
        if (m.index > last) ln.append(document.createTextNode(line.slice(last, m.index)));
        const [, com, str, num, kw] = m;
        ln.append(h("span", `tk-${com ? "com" : str ? "str" : num ? "num" : kw ? "kw" : "type"}`, m[0]));
        last = m.index + m[0].length;
      }
      if (last < line.length) ln.append(document.createTextNode(line.slice(last)));
      if (!line.length) ln.append(document.createTextNode(" "));
      frag.append(ln);
    }
    return frag;
  }
  async function copy(text, button) {
    const old = button.textContent;
    try { await navigator.clipboard.writeText(text); button.textContent = "Kopyalandı"; }
    catch { button.textContent = "Seçip kopyalayın"; }
    setTimeout(() => (button.textContent = old), 1400);
  }

  // ---------- diagram ----------
  function anchor(node, toward) {
    const cx = node.x + node.w / 2, cy = node.y + node.h / 2, dx = toward.x - cx, dy = toward.y - cy;
    if (Math.abs(dx) * node.h > Math.abs(dy) * node.w) { const sx = Math.sign(dx) * node.w / 2; return { x: cx + sx, y: cy + dy * (sx / dx) }; }
    const sy = Math.sign(dy || 1) * node.h / 2; return { x: cx + (dy ? dx * (sy / dy) : 0), y: cy + sy };
  }
  function drawDiagram(ch, svg) {
    svg.replaceChildren();
    const byId = Object.fromEntries(ch.nodes.map(n => [n.id, n]));
    svg.setAttribute("viewBox", `0 0 ${ch.width} ${ch.height}`);
    svg.setAttribute("role", "img"); svg.setAttribute("aria-label", ch.title + " şeması");
    const defs = sv("defs", {}, svg);
    for (const [id, fill] of [["ah", "var(--line2)"], ["ahon", "var(--signal)"]]) {
      const m = sv("marker", { id: `${id}-${ch.id}`, viewBox: "0 0 10 10", refX: "9", refY: "5", markerWidth: "7", markerHeight: "7", orient: "auto-start-reverse" }, defs);
      sv("path", { d: "M0,0 L10,5 L0,10 z", style: `fill:${fill}` }, m);
    }
    for (const z of ch.zones || []) { const g = sv("g", { class: "zone" }, svg); sv("rect", { x: z.x, y: z.y, width: z.w, height: z.h, rx: 12 }, g); sv("text", { x: z.x + 12, y: z.y + 18 }, g).textContent = z.label; }
    const edges = {};
    for (const e of ch.edges) {
      const a = byId[e.from], b = byId[e.to];
      const ca = { x: a.x + a.w / 2, y: a.y + a.h / 2 }, cb = { x: b.x + b.w / 2, y: b.y + b.h / 2 };
      const p1 = anchor(a, cb), p2 = anchor(b, ca);
      const g = sv("g", { class: "edge" }, svg);
      const d = e.bend ? `M${p1.x},${p1.y} Q${(p1.x + p2.x) / 2 + (e.bend.x || 0)},${(p1.y + p2.y) / 2 + (e.bend.y || 0)} ${p2.x},${p2.y}` : `M${p1.x},${p1.y} L${p2.x},${p2.y}`;
      const path = sv("path", { d, "marker-end": `url(#ah-${ch.id})` }, g);
      if (e.label) {
        const lx = (p1.x + p2.x) / 2 + (e.bend ? (e.bend.x || 0) / 2 : 0) + (e.lx || 0);
        const ly = (p1.y + p2.y) / 2 + (e.bend ? (e.bend.y || 0) / 2 : 0) - 6 + (e.ly || 0);
        sv("text", { x: lx, y: ly, "text-anchor": "middle" }, g).textContent = e.label;
      }
      edges[e.id] = { g, path };
    }
    const nodes = {};
    for (const n of ch.nodes) {
      const g = sv("g", { class: `node k-${n.kind}` }, svg);
      if (n.kind === "data") { const f = 12; sv("path", { class: "box", d: `M${n.x},${n.y} h${n.w - f} l${f},${f} v${n.h - f} h${-n.w} z` }, g); }
      else sv("rect", { x: n.x, y: n.y, width: n.w, height: n.h, rx: n.kind === "actor" ? n.h / 2 : n.kind === "tool" || n.kind === "q" ? 14 : 8 }, g);
      const lines = Array.isArray(n.label) ? n.label : [n.label];
      const total = lines.length + (n.sub ? 1 : 0), top = n.y + n.h / 2 - (total - 1) * 8 + 4;
      lines.forEach((line, i) => (sv("text", { x: n.x + n.w / 2, y: top + i * 16, "text-anchor": "middle" }, g).textContent = line));
      if (n.sub) sv("text", { x: n.x + n.w / 2, y: top + lines.length * 16, "text-anchor": "middle", class: "sub" }, g).textContent = n.sub;
      nodes[n.id] = g;
    }
    const token = sv("circle", { r: 6, class: "token", opacity: 0 }, svg);
    return { nodes, edges, token };
  }

  // ---------- app ----------
  window.Atlas = { start(chapters, catalog) {
    const all = [...chapters, { id: "katalog", short: "Bileşen kataloğu", catalog: true }];
    const shell = $(".shell"), tree = $("#tree"), tabs = $("#tabs"), insp = $("#inspector");
    const views = { diagram: $("#v-diagram"), calls: $("#v-calls"), code: $("#v-code"), reading: $("#v-reading"), catalog: $("#v-catalog") };
    const svg = document.createElementNS(SVGNS, "svg");
    const legend = h("div", "legend");
    views.diagram.append(legend, svg);
    const state = { ch: null, step: 0, view: store.get("atlas.view") || "diagram", timer: null };
    let drawn = null, raf = 0;

    // Explorer
    const stepButtons = {};
    all.forEach((ch, ci) => {
      const wrap = h("div", "ch" + (ci === 0 ? " open" : ""));
      const head = h("button"); head.type = "button";
      head.append(h("span", "chev", "▶"), h("span", "num", String(ci + 1)), document.createTextNode(ch.short));
      head.addEventListener("click", () => { wrap.classList.toggle("open"); if (ch.catalog) go(ch, 0); else if (state.ch !== ch) go(ch, 0); });
      wrap.appendChild(head);
      const steps = h("div", "steps");
      stepButtons[ch.id] = [];
      (ch.steps || []).forEach((s, si) => {
        const b = h("button"); b.type = "button";
        b.append(h("span", "n", String(si + 1).padStart(2, "0")), h("span", null, s.title));
        b.dataset.text = (s.title + " " + textOf(s.who)).toLowerCase();
        b.addEventListener("click", () => { go(ch, si); shell.classList.remove("nav-open"); });
        steps.appendChild(b); stepButtons[ch.id].push(b);
      });
      wrap.appendChild(steps); tree.appendChild(wrap);
      ch._wrap = wrap;
    });
    $("#tree-filter").addEventListener("input", ev => {
      const q = ev.target.value.trim().toLowerCase();
      for (const ch of all) {
        let any = false;
        for (const b of stepButtons[ch.id]) { const hit = !q || b.dataset.text.includes(q); b.hidden = !hit; any ||= hit; }
        if (q) ch._wrap.classList.toggle("open", any);
      }
    });

    // Tabs
    const tabDefs = [["diagram", "Şema", "1"], ["calls", "Çağrı ağacı", "2"], ["code", "Kod", "3"], ["reading", "Okuma", "4"]];
    for (const [id, label, key] of tabDefs) {
      const b = h("button"); b.type = "button"; b.dataset.view = id;
      b.append(document.createTextNode(label), h("kbd", null, key));
      b.addEventListener("click", () => setView(id));
      tabs.appendChild(b);
    }
    function setView(id) {
      if (state.ch.catalog) return;
      state.view = id; store.set("atlas.view", id);
      for (const b of tabs.children) b.classList.toggle("on", b.dataset.view === id);
      for (const [k, v] of Object.entries(views)) v.hidden = k !== id;
      render();
    }

    // Navigation
    function go(ch, step) {
      const changed = state.ch !== ch;
      state.ch = ch; state.step = Math.max(0, Math.min((ch.steps || [1]).length - 1, step));
      if (changed && !ch.catalog) {
        drawn = drawDiagram(ch, svg);
        legend.replaceChildren(...(ch.legend || [["actor", "kişi / CI"], ["cmd", "komut"], ["core", "bileşen"], ["data", "veri / dosya"], ["tool", "dış araç"]]).map(([k, label]) => {
          const s = h("span"), i = h("i"); i.style.borderColor = `var(--${({ actor: "focus", cmd: "signal", core: "line2", data: "type", tool: "muted", q: "focus", yes: "ok", no: "warn" })[k]})`;
          s.append(i, document.createTextNode(label)); return s;
        }));
        ch._wrap.classList.add("open");
      }
      if (ch.catalog) { stop(); for (const [k, v] of Object.entries(views)) v.hidden = k !== "catalog"; tabs.hidden = true; $(".player").hidden = true; }
      else { tabs.hidden = false; $(".player").hidden = false; for (const [k, v] of Object.entries(views)) v.hidden = k !== state.view; for (const b of tabs.children) b.classList.toggle("on", b.dataset.view === state.view); }
      store.set("atlas.at", `${ch.id}:${state.step}`);
      render();
    }
    function render() {
      const ch = state.ch;
      for (const [id, list] of Object.entries(stepButtons)) list.forEach((b, i) => b.classList.toggle("on", id === ch.id && i === state.step));
      stepButtons[ch.id]?.[state.step]?.scrollIntoView({ block: "nearest" });
      if (ch.catalog) { crumbs("Bileşen kataloğu"); renderCatalogInspector(); $("#status-left").textContent = `${catalog.length} sınıf`; return; }
      const s = ch.steps[state.step];
      crumbs(ch.short, s.title);
      $("#count").textContent = `${state.step + 1} / ${ch.steps.length}`;
      $("#scrub i").style.width = `${((state.step + 1) / ch.steps.length) * 100}%`;
      $("#status-left").textContent = `${ch.eyebrow} · adım ${state.step + 1}/${ch.steps.length}`;
      renderInspector(ch, s);
      if (state.view === "diagram") renderDiagram(s);
      if (state.view === "calls") renderCalls(ch);
      if (state.view === "code") renderCode(s);
      if (state.view === "reading") renderReading(ch);
    }

    function crumbs(...parts) {
      const last = parts.pop();
      $("#crumbs").replaceChildren(document.createTextNode(["Atlas", ...parts].join(" › ") + " › "), h("b", null, last));
    }

    function renderDiagram(s) {
      const onN = new Set(s.nodes || []), onE = new Set(s.edges || []);
      for (const [id, g] of Object.entries(drawn.nodes)) { g.classList.toggle("on", onN.has(id)); g.classList.toggle("dim", onN.size > 0 && !onN.has(id)); }
      for (const [id, e] of Object.entries(drawn.edges)) {
        e.g.classList.toggle("on", onE.has(id)); e.g.classList.toggle("dim", onE.size > 0 && !onE.has(id));
        e.path.setAttribute("marker-end", `url(#${onE.has(id) ? "ahon" : "ah"}-${state.ch.id})`);
      }
      cancelAnimationFrame(raf);
      const paths = (s.edges || []).map(id => drawn.edges[id]?.path).filter(Boolean);
      if (!paths.length || reduced) { drawn.token.setAttribute("opacity", 0); return; }
      const start = performance.now(), per = 800;
      const tick = now => {
        const t = (now - start) / per, k = Math.min(paths.length - 1, Math.floor(t)), f = Math.min(1, t - k);
        const p = paths[k], pt = p.getPointAtLength(p.getTotalLength() * f);
        drawn.token.setAttribute("cx", pt.x); drawn.token.setAttribute("cy", pt.y); drawn.token.setAttribute("opacity", 1);
        if (t < paths.length) raf = requestAnimationFrame(tick); else drawn.token.setAttribute("opacity", 0);
      };
      raf = requestAnimationFrame(tick);
    }

    function renderCalls(ch) {
      const rows = CALLS[ch.id] || ch.steps.map((s, i) => [s.title, 0, i]);
      const v = views.calls; v.replaceChildren();
      const wrap = h("div", "waterfall");
      const ruler = h("div", "wf-ruler"); ruler.append(h("span", null, "çağrı"));
      const ticks = h("span", "ticks"); ticks.append(h("span", null, "başla"), h("span", null, "sıra · iç içelik"), h("span", null, "bitti")); ruler.append(ticks);
      wrap.append(ruler);
      const colors = ["var(--signal)", "var(--focus)", "var(--type)", "var(--ok)", "var(--warn)"];
      rows.forEach(([name, depth, step], i) => {
        let end = i + 1; while (end < rows.length && rows[end][1] > depth) end++;
        const row = h("div", "wf-row" + (step === state.step ? " on" : ""));
        const nm = h("div", "nm"); nm.style.paddingLeft = `${depth * 16}px`;
        nm.append(h("i", null, depth ? "└ " : ""), document.createTextNode(name));
        const lane = h("div", "lane"), bar = h("b");
        bar.style.left = `${(i / rows.length) * 100}%`; bar.style.width = `${Math.max(1.5, ((end - i) / rows.length) * 100)}%`; bar.style.background = colors[depth % colors.length];
        lane.append(bar); row.append(nm, lane);
        row.title = `Adım ${step + 1}: ${ch.steps[step].title}`;
        row.addEventListener("click", () => go(ch, step));
        wrap.append(row);
      });
      wrap.append(h("div", "wf-note", "Çubuklar çağrı sırasını ve iç içeliği gösterir, süreyi değil. Bir satıra tıklayınca o adım seçilir; argümanlar ve dönüş Inspector'da."));
      v.append(wrap);
    }

    function renderCode(s) {
      const v = views.code; v.replaceChildren();
      const box = h("div", "codeview");
      const { code } = split(s.body);
      const blocks = [...code];
      if (!blocks.length) {
        const e = h("div", "empty"); e.append(h("b", null, "Bu adımda kod alıntısı yok."), h("span", null, "İmza ve dosya Inspector'da; yandaki adımlarda alıntılar var."));
        box.append(e);
      }
      blocks.forEach((src, i) => {
        const cb = h("div", "codeblock");
        const head = h("div", "cb-head"); head.append(h("span", null, `${s.file ? textOf(s.file).split(",")[0] : "alıntı"} · ${i + 1}/${blocks.length}`));
        const btn = h("button", "mini", "Kopyala"); btn.type = "button"; btn.addEventListener("click", () => copy(src, btn));
        head.append(btn);
        const pre = h("pre"); pre.append(highlight(src));
        cb.append(head, pre); box.append(cb);
      });
      v.append(box);
    }

    function renderReading(ch) {
      const v = views.reading; v.replaceChildren();
      const r = h("div", "reading");
      r.append(h("div", "kicker", ch.eyebrow), h("h1", null, ch.title));
      const lead = h("p", "lead"); setHtml(lead, ch.lead); r.append(lead);
      if (ch.note) { const n = h("div", "note"); setHtml(n, ch.note); r.append(n); }
      const tl = h("div", "timeline");
      ch.steps.forEach((s, i) => {
        const item = h("div", "tl" + (i === state.step ? " on" : "")); item.dataset.n = String(i + 1);
        item.append(h("h4", null, s.title), h("div", "who", textOf(s.who)));
        const p = h("p"); setHtml(p, s.purpose); item.append(p);
        item.addEventListener("click", () => go(ch, i));
        tl.append(item);
      });
      r.append(tl); v.append(r);
      v.querySelector(".tl.on")?.scrollIntoView({ block: "nearest" });
    }

    function section(title, content, mono) {
      const s = h("div", "sect"); s.append(h("h3", null, title));
      const c = h("div", "c" + (mono ? " mono" : "")); if (typeof content === "string") setHtml(c, content); else if (content instanceof DocumentFragment) c.append(sanitizedNodes(content)); else c.append(content);
      s.append(c); return s;
    }
    function renderInspector(ch, s) {
      insp.replaceChildren();
      const head = h("div", "insp-head"); head.append(h("div", "eyebrow", `${ch.short} · adım ${state.step + 1}`), h("h2", null, s.title));
      insp.append(head);
      if (s.who) insp.append(section("Kim", s.who, true));
      if (s.file) {
        const f = h("div", "file"); const span = h("span"); setHtml(span, s.file);
        const b = h("button", "mini", "Kopyala"); b.type = "button"; b.addEventListener("click", () => copy(textOf(s.file), b));
        f.append(span, b); insp.append(section("Dosya", f));
      }
      if (s.input) insp.append(section("Girdi", s.input));
      if (s.output) insp.append(section("Çıktı", s.output));
      if (s.purpose) insp.append(section("Amaç", s.purpose));
      const { code, rest } = split(s.body);
      if (rest.textContent.trim()) insp.append(section("Ayrıntılar", rest));
      if (code.length) {
        const b = h("button", "mini", `Kod sekmesinde ${code.length} alıntı →`); b.type = "button"; b.addEventListener("click", () => setView("code"));
        insp.append(section("Kod", b));
      }
    }
    function renderCatalogInspector(row) {
      insp.replaceChildren();
      const head = h("div", "insp-head"); head.append(h("div", "eyebrow", "Bileşen kataloğu"), h("h2", null, row ? row.name : "Bir satır seçin"));
      insp.append(head);
      if (!row) { insp.append(section("Nasıl", "Akışlarda geçen her sınıf tek satırda. Süzmek için tablonun üstündeki kutuyu ya da <kbd>⌘K</kbd> paletini kullanın; satıra tıklayınca ayrıntı burada açılır.")); return; }
      insp.append(section("Dosya", row.file, true), section("Ne yapar", row.what), section("Ana metot", row.sig), section("Çağıran", row.by));
    }

    // Catalog view
    (function buildCatalog() {
      const v = views.catalog; const box = h("div", "catalog");
      const input = h("input"); input.type = "search"; input.id = "catalog-filter"; input.placeholder = "Süz: ör. Selector, map, record"; input.setAttribute("aria-label", "Kataloğu süz");
      input.style.cssText = "background:var(--panel2);border:1px solid var(--line);border-radius:7px;padding:7px 10px;color:var(--fg);font:13px var(--sans);width:min(420px,100%)";
      const table = h("table"), thead = h("thead"), hr = h("tr");
      hr.append(h("th", null, "Sınıf"), h("th", null, "Ne yapar"), h("th", null, "Ana metot")); thead.append(hr); table.append(thead);
      const tb = h("tbody");
      for (const r of catalog) {
        const tr = h("tr"); const n = h("td", "name"); n.append(document.createTextNode(r.name), h("br"), h("span", "area", r.file));
        const w = h("td"); setHtml(w, r.what); const sg = h("td"); setHtml(sg, r.sig);
        tr.append(n, w, sg); tr.dataset.text = (r.name + " " + r.file + " " + textOf(r.what) + " " + textOf(r.sig)).toLowerCase();
        tr.style.cursor = "pointer"; tr.addEventListener("click", () => renderCatalogInspector(r));
        tb.append(tr);
      }
      table.append(tb);
      input.addEventListener("input", () => { const q = input.value.trim().toLowerCase(); for (const tr of tb.children) tr.hidden = q.length > 0 && !tr.dataset.text.includes(q); });
      box.append(input, table); v.append(box);
      Atlas.filterCatalog = q => { input.value = q; input.dispatchEvent(new Event("input")); };
    })();

    // Player
    function stop() { clearInterval(state.timer); state.timer = null; $("#play").textContent = "▶ Oynat"; }
    function play() {
      if (state.timer) { stop(); return; }
      if (state.step >= state.ch.steps.length - 1) go(state.ch, 0);
      $("#play").textContent = "❚❚ Durdur";
      state.timer = setInterval(() => { if (state.step >= state.ch.steps.length - 1) { stop(); return; } go(state.ch, state.step + 1); }, 4200);
    }
    $("#play").addEventListener("click", play);
    $("#prev").addEventListener("click", () => { stop(); go(state.ch, state.step - 1); });
    $("#next").addEventListener("click", () => { stop(); go(state.ch, state.step + 1); });
    $("#scrub").addEventListener("click", ev => {
      const r = ev.currentTarget.getBoundingClientRect(); stop();
      go(state.ch, Math.floor(((ev.clientX - r.left) / r.width) * state.ch.steps.length));
    });

    // Palette
    const pal = $("#palette"), palInput = $("#palette-input"), palList = $("#palette-list");
    const items = [];
    chapters.forEach((ch, ci) => ch.steps.forEach((s, si) => items.push({ where: `${ci + 1}.${String(si + 1).padStart(2, "0")}`, title: s.title, sub: textOf(s.who), run: () => go(ch, si) })));
    catalog.forEach(r => items.push({ where: "sınıf", title: r.name, sub: r.file, run: () => { go(all[all.length - 1], 0); Atlas.filterCatalog(r.name.split(" ")[0]); renderCatalogInspector(r); } }));
    let palSel = 0, palHits = [];
    function openPalette() { pal.hidden = false; palInput.value = ""; filterPalette(); palInput.focus(); }
    function closePalette() { pal.hidden = true; }
    function filterPalette() {
      const words = palInput.value.trim().toLowerCase().split(/\s+/).filter(Boolean);
      palHits = items.filter(it => { const t = (it.where + " " + it.title + " " + it.sub).toLowerCase(); return words.every(w => t.includes(w)); }).slice(0, 60);
      palSel = 0; drawPalette();
    }
    function drawPalette() {
      palList.replaceChildren(...palHits.map((it, i) => {
        const li = h("li", i === palSel ? "on" : ""); li.setAttribute("role", "option");
        const what = h("div", "what"); what.append(document.createTextNode(it.title), h("small", null, it.sub));
        li.append(h("span", "where", it.where), what);
        li.addEventListener("click", () => { closePalette(); it.run(); });
        return li;
      }));
      palList.children[palSel]?.scrollIntoView({ block: "nearest" });
    }
    palInput.addEventListener("input", filterPalette);
    palInput.addEventListener("keydown", ev => {
      if (ev.key === "ArrowDown") { palSel = Math.min(palHits.length - 1, palSel + 1); drawPalette(); ev.preventDefault(); }
      if (ev.key === "ArrowUp") { palSel = Math.max(0, palSel - 1); drawPalette(); ev.preventDefault(); }
      if (ev.key === "Enter" && palHits[palSel]) { closePalette(); palHits[palSel].run(); }
      if (ev.key === "Escape") closePalette();
    });
    pal.addEventListener("click", ev => { if (ev.target === pal) closePalette(); });
    $("#palette-btn").addEventListener("click", openPalette);

    // Keys, theme, menu
    document.addEventListener("keydown", ev => {
      if ((ev.metaKey || ev.ctrlKey) && ev.key.toLowerCase() === "k") { ev.preventDefault(); pal.hidden ? openPalette() : closePalette(); return; }
      if (!pal.hidden || ev.target.closest("input")) return;
      if (ev.key === "/") { ev.preventDefault(); openPalette(); }
      else if (ev.key === "ArrowRight") { stop(); go(state.ch, state.step + 1); }
      else if (ev.key === "ArrowLeft") { stop(); go(state.ch, state.step - 1); }
      else if (ev.key === " " && !ev.target.closest("button")) { ev.preventDefault(); play(); }
      else if (["1", "2", "3", "4"].includes(ev.key)) setView(tabDefs[Number(ev.key) - 1][0]);
    });
    const themes = ["system", "dark", "light"];
    function applyTheme(t) { if (t === "system") document.documentElement.removeAttribute("data-theme"); else document.documentElement.setAttribute("data-theme", t); $("#theme").textContent = { system: "Tema: sistem", dark: "Tema: koyu", light: "Tema: açık" }[t]; store.set("atlas.theme", t); }
    let theme = store.get("atlas.theme") || "system"; applyTheme(theme);
    $("#theme").addEventListener("click", () => { theme = themes[(themes.indexOf(theme) + 1) % 3]; applyTheme(theme); });
    $("#menu").addEventListener("click", () => shell.classList.toggle("nav-open"));

    // Start where the viewer left off, or at the beginning.
    const [atCh, atStep] = (store.get("atlas.at") || "").split(":");
    const first = all.find(c => c.id === atCh) || chapters[0];
    for (const b of tabs.children) b.classList.toggle("on", b.dataset.view === state.view);
    go(first, Number(atStep) || 0);
    if (!first.catalog) setView(state.view);
  } };
})();
