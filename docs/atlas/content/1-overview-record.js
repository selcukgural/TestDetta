// Chapters 1 and 2. Helpers: C() prints an escaped code block; c() an inline code span.
const esc = s => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
const C = s => `<code class="sig">${esc(s.trim())}</code>`;
const c = s => `<code>${esc(s)}</code>`;
const ul = items => `<ul>${items.map(i => `<li>${i}</li>`).join("")}</ul>`;

const CH_OVERVIEW = {
  id: "genel", short: "Genel bakış", eyebrow: "Bölüm 1 · Uçtan uca",
  title: "Bir değişiklik main'den pull request'e nasıl gider",
  lead: `TestDetta iki anda çalışır. <b>main</b> dalında testlerin tamamı bir kez koşarken her test sınıfının hangi kodu çalıştırdığı kaydedilir (kapsam haritası). Bir <b>pull request</b> geldiğinde değişiklik bu haritayla karşılaştırılır ve yalnız etkilenebilecek test sınıfları koşturulur. Örnek: ${c("Calculator.Tax")} değişti; ${c("Calc.Tests")} içinden yalnız ${c("TaxTests")} koşar.`,
  width: 1000, height: 600,
  zones: [
    { x: 10, y: 20, w: 980, h: 150, label: "main dalı · her birleştirmeden sonra (record modu)" },
    { x: 10, y: 250, w: 980, h: 340, label: "pull request · her push'ta (test modu)" },
  ],
  nodes: [
    { id: "dev", kind: "actor", x: 30, y: 300, w: 180, h: 50, label: "Geliştirici", sub: "push · birleştirme" },
    { id: "cimain", kind: "actor", x: 30, y: 70, w: 170, h: 56, label: "GitHub Actions", sub: "mode: record" },
    { id: "rec", kind: "cmd", x: 280, y: 70, w: 160, h: 56, label: "td record", sub: "RecordCoverage" },
    { id: "maps", kind: "data", x: 510, y: 66, w: 200, h: 64, label: ".testdetta/coverage", sub: "Calc.Tests.json" },
    { id: "cache", kind: "data", x: 770, y: 66, w: 200, h: 64, label: "Actions cache", sub: "coverage-<sha>" },
    { id: "cipr", kind: "actor", x: 30, y: 420, w: 170, h: 56, label: "GitHub Actions", sub: "mode: test" },
    { id: "td", kind: "cmd", x: 280, y: 420, w: 160, h: 56, label: "td test", sub: "CliApp.Run" },
    { id: "lic", kind: "core", x: 280, y: 520, w: 160, h: 50, label: "LicenseGate", sub: "Check · Apply" },
    { id: "pipe", kind: "core", x: 510, y: 420, w: 200, h: 56, label: "AnalysisPipeline", sub: "Run → AnalysisResult" },
    { id: "dt", kind: "tool", x: 770, y: 420, w: 200, h: 56, label: "dotnet test", sub: "--filter …TaxTests" },
    { id: "sum", kind: "data", x: 770, y: 296, w: 200, h: 60, label: "İş özeti", sub: "$GITHUB_STEP_SUMMARY" },
  ],
  edges: [
    { id: "merge", from: "dev", to: "cimain", label: "birleştirme", lx: 44 },
    { id: "run", from: "cimain", to: "rec", label: "record" },
    { id: "write", from: "rec", to: "maps", label: "Write" },
    { id: "save", from: "maps", to: "cache", label: "save" },
    { id: "push", from: "dev", to: "cipr", label: "push", lx: 26 },
    { id: "restore", from: "cache", to: "td", label: "restore → .testdetta/coverage", bend: { x: 40, y: 60 }, ly: -8 },
    { id: "start", from: "cipr", to: "td", label: "--base" },
    { id: "license", from: "td", to: "lic", label: "lisans", lx: 30 },
    { id: "analyze", from: "td", to: "pipe", label: "Run" },
    { id: "invoke", from: "pipe", to: "dt", label: "komutlar" },
    { id: "report", from: "dt", to: "sum", label: "özet", lx: 26 },
  ],
  steps: [
    {
      title: "main'e birleştirme record işini başlatır", nodes: ["dev", "cimain"], edges: ["merge"],
      who: "GitHub Actions iş akışı, TestDetta Action'ı (<code>action.yml</code>) <code>mode: record</code> ile çağırır",
      file: "action.yml",
      input: "<code>mode: record</code>, <code>configuration</code> (Debug), <code>projects</code>, <code>parallel</code>, <code>no-build</code>",
      output: "Bir sonraki adımda çalışacak <code>td record</code> komutu",
      purpose: "Kapsam haritası testlerin hepsinin koştuğu yerde, yani main'de çıkarılır. PR'lar bu haritaya göre seçim yapar.",
      body: ul([
        "Action önce sığ (shallow) checkout'ı reddeder: <code>fetch-depth: 0</code> şarttır, çünkü TestDetta git geçmişini (merge base, haritanın commit'i) okur.",
        "<code>actions/setup-dotnet</code> 10.0.x kurulur; TestDetta ya depodaki kaynaktan derlenir ya da <code>tool.lock</code>'taki sürüm indirilip SHA-512'si doğrulanır.",
      ]),
    },
    {
      title: "td record her test projesini kaydeder", nodes: ["cimain", "rec"], edges: ["run"],
      who: "<code>CliApp.RecordCoverage</code> → her test projesi için <code>CoverageRecorder.Record</code>",
      file: "src/TestDetta.Cli/CliApp.cs:382",
      input: "<code>dotnet TestDetta.Cli.dll record -c &lt;cfg&gt; --parallel n [--no-build] [--project …]</code>",
      output: "Proje başına <code>(CoverageMap Map, bool TestsPassed)</code>; çıkış kodu 0, 1 (hiç harita yazılmadı) ya da 3 (bir test başarısız)",
      purpose: "Her test sınıfının hangi metotları çalıştırdığını ölçmek. Ayrıntılar Bölüm 2'de.",
      body: C(`
var graph = new ProjectGraphLoader(..., new MsBuildProjectEvaluator(lf)).Load(root);
foreach (var project in SelectTestProjects(graph, root, projects))
{
    var (map, passed) = recorder.Record(
        new RecordRequest(root, graph, project, new RecordOptions(cfg, fw, parallel) { NoBuild = noBuild }),
        stdout);
    CoverageMapStore.Write(CoverageMapStore.PathFor(root, project.Path), map);
}`) + ul([
        "<code>NoTestsRecordedException</code>: proje \"skipped\" olarak raporlanır (hiç test koşmadı).",
        "<code>InvalidOperationException</code> (ör. derlenmedi): proje \"not recorded\" olur; PR'da isimle izlenerek seçilir.",
        "<code>RecordingFailedException</code> (2026-10-07, TestDetta tarafında başarısız kayıt): test dll'i enstrümante edilen dll'ler arasında değil, testler geçti ama izleyici sonuç yazmadı ya da hiç sınıf kaydedilmedi. Proje yine \"not recorded\" olur, sebep aynen yazılır (<code>TestsPassed</code>: geçti / düştü / koşmadı); her sınıfı \"failed\" gösteren harita ya da yaşanmamış bir çökme raporu yazılmaz. Çıkış kodları değişmez.",
      ]),
    },
    {
      title: "Harita dosyası yazılır", nodes: ["rec", "maps"], edges: ["write"],
      who: "<code>CoverageMapStore.Write(path, map)</code>",
      file: "src/TestDetta.Core/CoverageMapStore.cs:27",
      input: "<code>PathFor(repoRoot, csproj)</code> → <code>&lt;kök&gt;/.testdetta/coverage/Calc.Tests.json</code>, ve <code>CoverageMap</code>",
      output: "Format sürümü 2 olan sıkıştırılmamış JSON (üretilmiş kaynaklar içinde gzip+base64)",
      purpose: "Haritayı bir commit'e bağlı, sonradan okunabilir bir dosya olarak saklamak. <code>.testdetta/</code> git'e girmez.",
      body: C(`
{ "version": 2, "testProject": "tests/Calc.Tests/Calc.Tests.csproj", "commit": "9f3c…",
  "methods": [["Calc.Calculator","Add"], ["Calc.Calculator","Tax"]],
  "classes": { "Calc.Tests.AddTests": [0], "Calc.Tests.TaxTests": [1] },
  "fixtures": {}, "ambient": [], "initialization": [],
  "fileReads": { "classes": {}, "fixtures": {}, "ambient": [], "initialization": [] },
  "failedClasses": [], "timings": { "classes": { "Calc.Tests.TaxTests": 0.41 } } }`) + "<p>Sayılar <code>methods</code> dizisindeki sıra numaralarıdır: <code>TaxTests</code> 1 numaralı metodu, yani <code>Calculator.Tax</code>'ı çalıştırmış.</p>",
    },
    {
      title: "Harita commit anahtarıyla önbelleğe konur", nodes: ["maps", "cache"], edges: ["save"],
      who: "<code>actions/cache/save</code> (Action'ın record modu)",
      file: "action.yml",
      input: "<code>.testdetta/coverage</code> klasörü, anahtar <code>testdetta-coverage-${{ github.sha }}</code>",
      output: "Commit'e bağlı bir Actions önbellek girdisi",
      purpose: "PR işleri haritayı yeniden üretmeden, kendi merge base'ine en yakın commit'in haritasını bulabilsin. GitHub dışı CI'da aynı işi <code>--maps-dir</code> yapar (<code>MapDirectory.Save/Restore</code>).",
    },
    {
      title: "Geliştirici PR'a push eder", nodes: ["dev", "cipr"], edges: ["push"],
      who: "GitHub Actions, Action'ı <code>mode: test</code> ile çağırır",
      file: "action.yml",
      input: "<code>base</code> (PR'ın taban dalı), <code>max-map-age</code> (50), <code>drift-fallback</code> (all), <code>license</code>, <code>dotnet-test-args</code>",
      output: "Bir sonraki adımda kullanılacak taban ref'i ve merge base",
      purpose: "PR'ın hangi commit'ten ayrıldığını bulmak: seçim merge base ile HEAD arasındaki değişikliğe göre yapılır.",
    },
    {
      title: "En yakın harita bulunur ve geri yüklenir", nodes: ["cache", "cipr", "td"], edges: ["restore"],
      who: "Action'ın 'find the newest map' adımı + <code>actions/cache/restore</code>",
      file: "action.yml",
      input: "<code>gh cache list --key testdetta-coverage-</code> (token <code>actions: read</code> ister) ve <code>git rev-list --first-parent -n max+1 &lt;merge-base&gt;</code>",
      output: "<code>.testdetta/coverage/*.json</code>, merge base'te ya da en çok <code>max-map-age</code> commit gerisinde kaydedilmiş",
      purpose: "Harita PR'ın tabanıyla tam aynı commit'te kaydedilmemiş olabilir. Aradaki fark (drift) sonradan analizde köprülenir.",
      body: "<p>Token yoksa yalnız <code>testdetta-coverage-&lt;merge-base&gt;</code> denenir.</p>",
    },
    {
      title: "td test başlar ve lisans arka planda kontrol edilir", nodes: ["cipr", "td", "lic"], edges: ["start", "license"],
      who: "<code>CliApp.Run(args, stdout, stderr, CliHost)</code> → <code>LicenseGate.Refresh</code> (arka plan Task), sonra <code>LicenseGate.Check</code> ve <code>Apply</code>",
      file: "src/TestDetta.Cli/CliApp.cs:32, src/TestDetta.Cli/LicenseGate.cs",
      input: "<code>td test --base origin/main --max-map-age 50 --drift-fallback all --summary-file $GITHUB_STEP_SUMMARY -- -c Debug</code>, ortamda <code>TESTDETTA_LICENSE</code>",
      output: "<code>LicenseStatus</code> (Valid, Warning, PublicRepository, Missing, Invalid, Expired, OverSeats…)",
      purpose: "Geçerli lisans yoksa seçim yapılmaz, bütün testler koşar; iş hiçbir zaman lisans yüzünden başarısız olmaz.",
      body: ul([
        "Lisans metni <code>testdetta1.&lt;base64url claims&gt;.&lt;imza&gt;</code>; ECDSA P-256 imzası ağ olmadan doğrulanır (<code>License.Verify</code>).",
        "Koltuk hesabı: son 30 günün bot olmayan commit yazarları; izin = koltuk + max(1, ⌈koltuk × 0,2⌉).",
        "<b>Bugün:</b> <code>LicenseKeys.Enforced = false</code>, yani kontrol raporlanır ama seçimi kapatmaz.",
        "<b>Shadow mode</b> (<code>td test --shadow</code>) bu adıma hiç gelmez: <code>CliApp.Run</code> onu yenileme başlamadan <code>CliApp.Shadow</code>'a gönderir; lisans okunmaz, yenilenmez, TestDetta'nın kendisi ağ çağrısı yapmaz (ağı kullanan TestDetta değildir: Action aracı nuget.org'dan indirir ve Actions önbelleğini kullanır, base commit'teki yeniden koşu o commit'in paketlerini her derleme gibi geri yükler).",
        "<b>td audit</b> de gelmez: <code>CliApp.Run</code> onu da yenilemeden önce <code>CliApp.Audit</code>'e gönderir (SIGINT/SIGTERM iptal belirtecine bağlanır); lisans okunmaz, ağ çağrısı yoktur (docs/design/audit.md).",
      ]),
    },
    {
      title: "Analiz: değişiklikten test sınıflarına", nodes: ["td", "pipe"], edges: ["analyze"],
      who: "<code>AnalysisPipeline.Run(repoPath, baseRef, AnalysisOptions)</code>",
      file: "src/TestDetta.Core/AnalysisPipeline.cs:62",
      input: "Depo yolu, <code>origin/main</code>, <code>AnalysisOptions { MaxMapAge, DriftFallback, GeneratedCode }</code>",
      output: "<code>AnalysisResult(RepoRoot, ChangedFiles, Graph, Impact, Plan)</code>; <code>Plan.Projects</code> proje başına <code>TestProjectSelection</code>",
      purpose: "Hangi test projesinin tamamen, hangisinin kısmen (hangi sınıflarla) koşacağına ve hangisinin hiç koşmayacağına karar vermek. Ayrıntılar Bölüm 3 ve 4'te.",
      body: C(`
var changed = git.ChangedFiles(root, baseRef);           // src/Calc/Calculator.cs
var graph   = new ProjectGraphLoader(...).Load(root);    // Calc, Calc.Tests
var codeChanges = reader.Read(root, mergeBase, changed, symbolSets);  // Calculator.Tax: Modified
var impact  = impactAnalyzer.Analyze(graph, root, changed, codeChanges, packageChanges);
var (maps, mapCommits) = LoadCoverageMaps(git, root, graph, mergeBase, options);
var tests   = new TestSelector(...).Select(new TestSelectionRequest(graph, root, changed, impact, maps));
// Calc.Tests: Filtered [Calc.Tests.TaxTests]`),
    },
    {
      title: "Seçim dotnet test komutlarına dönüşür", nodes: ["pipe", "dt"], edges: ["invoke"],
      who: "<code>TestInvocationBuilder.Build</code> → <code>TestRunner.RunAll</code>",
      file: "src/TestDetta.Core/TestInvocation.cs:25, src/TestDetta.Cli/TestRunner.cs:41",
      input: "<code>analysis.Tests</code>, repo kökü, <code>--</code> sonrası argümanlar",
      output: "Proje başına <code>TestInvocation(Selection, Arguments, Location)</code>; çalıştıktan sonra <code>TestRunResult</code> listesi",
      purpose: "Seçilen sınıfları test çerçevesinin anladığı filtreye çevirip projeleri sırayla koşturmak.",
      body: C(`dotnet test tests/Calc.Tests/Calc.Tests.csproj --filter "FullyQualifiedName~Calc.Tests.TaxTests." -c Debug`) + ul([
        "Filtre sözdizimi projeye göre: VSTest <code>--filter FullyQualifiedName~…</code>, xUnit v3 (MTP) <code>--filter-class</code>, TUnit <code>--treenode-filter</code>.",
        "Filtre hiç teste uymazsa (\"No test matches…\" ya da MTP çıkış kodu 8) proje filtresiz yeniden koşar: daha fazla, asla daha az.",
        "Skipped projeler için komut üretilmez; bir proje başarısız olsa da diğerleri koşar.",
      ]),
    },
    {
      title: "Sonuç ve gerekçeler iş özetine yazılır", nodes: ["dt", "sum"], edges: ["report"],
      who: "<code>ResultWriter.WriteRunSummary</code> (konsol) ve <code>MarkdownSummary.Write</code> (dosya)",
      file: "src/TestDetta.Cli/MarkdownSummary.cs",
      input: "<code>SummaryInput(analysis, baseRef, results)</code>, <code>--summary-file</code> yolu",
      output: "<code>### TestDetta test selection</code> başlıklı Markdown; çıkış kodu: herhangi bir dotnet test başarısızsa 3, değilse 0",
      purpose: "Her projenin neden tamamen, kısmen ya da hiç koşmadığını ve her sınıfın kanıtını göstermek (ör. \"ran Calculator.Tax, which changed (map 9f3c…)\").",
    },
  ],
};

const CH_RECORD = {
  id: "record", short: "td record", eyebrow: "Bölüm 2 · Kayıt",
  title: "td record: kapsam haritası nasıl çıkarılır",
  lead: `Kayıt, test projesinin derlenmiş dll'lerini Mono.Cecil ile yeniden yazar: her metodun başına bir "buradan geçildi" çağrısı ekler, testleri tek tek koşturur, hangi test sınıfı sırasında hangi metodun çalıştığını toplar ve dll'leri eski haline döndürür. Örnek: ${c("AddTests")} koşarken ${c("Calculator.Add")} (id 0) çalışır, ${c("TaxTests")} koşarken ${c("Calculator.Tax")} (id 1).`,
  width: 1000, height: 640,
  zones: [
    { x: 10, y: 10, w: 980, h: 150, label: "td süreci" },
    { x: 10, y: 190, w: 600, h: 290, label: "test çıktısı (bin/Debug/net10.0)" },
    { x: 630, y: 190, w: 360, h: 290, label: "test host süreci (dotnet test)" },
    { x: 10, y: 500, w: 980, h: 130, label: "birleştirme ve yazma" },
  ],
  nodes: [
    { id: "cli", kind: "cmd", x: 30, y: 60, w: 150, h: 56, label: "td record", sub: "RecordCoverage" },
    { id: "graph", kind: "core", x: 220, y: 60, w: 170, h: 56, label: "ProjectGraphLoader", sub: "Load(root)" },
    { id: "rec", kind: "core", x: 430, y: 60, w: 180, h: 56, label: "CoverageRecorder", sub: "Record(request)" },
    { id: "build", kind: "tool", x: 650, y: 60, w: 140, h: 56, label: "dotnet build", sub: "-c Debug" },
    { id: "pre", kind: "core", x: 820, y: 54, w: 160, h: 68, label: ["GeneratedSource…", "ClassLocation…"], sub: "PDB okuma" },
    { id: "dlls", kind: "data", x: 30, y: 230, w: 200, h: 76, label: ["Calc.dll", "Calc.Tests.dll"], sub: "+ .pdb" },
    { id: "instr", kind: "core", x: 300, y: 236, w: 230, h: 64, label: "AssemblyInstrumenter", sub: "Instrument → Methods" },
    { id: "deploy", kind: "core", x: 30, y: 384, w: 200, h: 64, label: "RuntimeDeployment", sub: "tracker dll + deps" },
    { id: "run", kind: "core", x: 300, y: 384, w: 230, h: 64, label: "RunShards", sub: "parça başına RunShard" },
    { id: "host", kind: "tool", x: 660, y: 226, w: 310, h: 56, label: "dotnet test --no-build", sub: "tek tek: MaxParallelThreads=1" },
    { id: "tracker", kind: "core", x: 660, y: 316, w: 310, h: 64, label: "TestDettaTracker", sub: "Hit · Enter… · FileRead" },
    { id: "results", kind: "data", x: 660, y: 404, w: 310, h: 56, label: "coverage-0.<pid>.json", sub: "WriteResults" },
    { id: "merge", kind: "core", x: 300, y: 540, w: 230, h: 60, label: "BuildMap", sub: "+ FileReadPaths" },
    { id: "map", kind: "data", x: 660, y: 536, w: 310, h: 68, label: "CoverageMap", sub: ".testdetta/coverage/…json" },
    { id: "restore", kind: "core", x: 30, y: 540, w: 200, h: 60, label: "Restore", sub: "yedekler geri" },
  ],
  edges: [
    { id: "load", from: "cli", to: "graph", label: "" },
    { id: "call", from: "graph", to: "rec", label: "" },
    { id: "b", from: "rec", to: "build", label: "" },
    { id: "p", from: "build", to: "pre", label: "" },
    { id: "i1", from: "rec", to: "instr", label: "Instrument" },
    { id: "i2", from: "instr", to: "dlls", label: "yazar" },
    { id: "d", from: "dlls", to: "deploy", label: "" },
    { id: "r", from: "instr", to: "run", label: "id tablosu", lx: 44 },
    { id: "h", from: "run", to: "host", label: "env + filtre" },
    { id: "t", from: "host", to: "tracker", label: "probe'lar", lx: 44 },
    { id: "w", from: "tracker", to: "results", label: "" },
    { id: "m", from: "results", to: "merge", label: "sonuç dosyaları" },
    { id: "out", from: "merge", to: "map", label: "Write" },
    { id: "back", from: "run", to: "restore", label: "finally" },
  ],
  steps: [
    {
      title: "Komut, projeleri ve seçenekleri toplar", nodes: ["cli", "graph"], edges: ["load"],
      who: "<code>CliApp.RecordCoverage</code>",
      file: "src/TestDetta.Cli/CliApp.cs:382",
      input: "<code>RecordSettings(Configuration, Projects, Parallelism, Framework) { NoBuild }</code>",
      output: "<code>ProjectGraph</code> ve kaydedilecek test projelerinin listesi",
      purpose: "Hangi projelerin test projesi olduğunu (MSBuild değerlendirmesi ya da <code>*.Tests</code> adı) bulmak.",
      body: ul([
        "<code>GitClient.TopLevel(repo)</code> depo kökünü verir.",
        "<code>ProjectGraphLoader.Load(root)</code> her <code>.csproj</code>'u MSBuild ile değerlendirir (önbellek: <code>.testdetta/cache/graph</code>).",
        "<code>--project</code> verilmemişse bütün test projeleri kaydedilir.",
      ]),
    },
    {
      title: "Kayıt isteği kurulur", nodes: ["graph", "rec"], edges: ["call"],
      who: "<code>CoverageRecorder.Record(RecordRequest request, TextWriter output)</code>",
      file: "src/TestDetta.Coverage/CoverageRecorder.cs:67",
      input: "<code>RecordRequest(RepoRoot, Graph, TestProject, RecordOptions(Configuration = \"Debug\", Framework = null, Parallelism = 1, Timeout = null) { NoBuild })</code>",
      output: "<code>(CoverageMap Map, bool TestsPassed)</code>",
      purpose: "Tek bir test projesinin baştan sona kaydı. Aşağıdaki adımların hepsi bu metodun içindedir.",
      body: "<p><code>DotnetTestLocation.For(root, project)</code> çalışma klasörünü (en yakın <code>global.json</code>) ve modu (VSTest ya da Microsoft.Testing.Platform) belirler.</p>",
    },
    {
      title: "Proje derlenir, çıktı yolu sorulur", nodes: ["rec", "build"], edges: ["b"],
      who: "<code>DotnetProcess.Run</code> ile <code>dotnet build</code>, sonra <code>FindTargetPath</code>",
      file: "src/TestDetta.Coverage/CoverageRecorder.cs",
      input: "<code>dotnet build &lt;proj&gt; -c Debug [-f fw]</code>; <code>dotnet msbuild -getProperty:TargetPath -getProperty:TargetFrameworks</code>",
      output: "<code>TargetPath</code> = <code>…/bin/Debug/net10.0/Calc.Tests.dll</code>",
      purpose: "Ölçülecek dll'leri üretmek. Derleme başarısızsa hatalar <code>ErrorCollectingWriter</code> ile toplanıp <code>InvalidOperationException</code> atılır.",
      body: "<p>Eski tip (SDK olmayan, <code>packages.config</code>) proje ya da onun referans verdiği eski tip bir proje varsa <code>FullMsBuild</code> geri yükleme ve derlemeyi tam MSBuild ile yapar (<code>TESTDETTA_MSBUILD</code>, Windows'ta <code>vswhere</code>, yoksa PATH'teki <code>msbuild</code>); testler <code>dotnet test &lt;TargetPath&gt;</code> ile derlenmiş dll'den koşar.</p><p>Birden çok framework'lü projede bu makinede çalışabilen ilk framework kaydedilir (<code>RecordingFramework</code>: .NET Framework yalnız Windows'ta); <code>--no-build</code> ile mevcut çıktı kullanılır, yoksa hata.</p>",
    },
    {
      title: "Enstrümantasyondan önce PDB'ler okunur", nodes: ["build", "pre"], edges: ["p"],
      who: "<code>GeneratedSourceReader.Read</code>, <code>ClassLocationReader.Read</code>",
      file: "src/TestDetta.Coverage/GeneratedSourceReader.cs, GeneratedSources.cs, ClassLocationReader.cs",
      input: "Çıktıdaki depo dll'leri (<code>RepositoryAssemblies</code>; yalnız test projesinin derlerken referans verdiği projelerinkiler, <code>CompiledAgainst</code>) ve <code>.pdb</code>'leri. Aynı dll adını birden çok proje üretebilir (Meziantou.Analyzer'ın Roslyn sürümleri): <code>ProjectAssemblies.Owners</code> ad başına hepsini listeler, ad sahiplerinden biri referans kapanışındaysa kalır ve o proje sahibi olur (birden çoksa yola göre ilki)",
      output: "<code>List&lt;GeneratedSource(Project, Name, Text) { Framework }&gt;</code>, sınıf → <code>[(File, Line)]</code> ve proje başına <code>BuildInputs</code> (git'in izlemediği derlenen belgelerin PDB sağlama toplamları, gömülü kaynakların SHA-256'sı)",
      purpose: "Kaynak jeneratörlerinin ürettiği kodu (diskte olmayan, PDB'ye gömülü belgeler) ve sınıfların kaynakta nerede olduğunu haritaya eklemek. Yeniden yazmadan önce yapılır, çünkü Cecil PDB'yi değiştirir.",
    },
    {
      title: "Dll'ler probe'larla yeniden yazılır", nodes: ["rec", "instr", "dlls"], edges: ["i1", "i2"],
      who: "<code>AssemblyInstrumenter.Instrument(assemblies, testAssemblies, backupDirectory)</code>",
      file: "src/TestDetta.Coverage/AssemblyInstrumenter.cs:56 (InstrumentOne:163, InsertProbes:790)",
      input: "Depo dll'leri, hangilerinin test dll'i olduğu, yedek klasörü (<code>%TEMP%/testdetta-record-&lt;guid&gt;/backup</code>)",
      output: "<code>InstrumentationResult(Methods, Backups) { Fields, ExternalCallbacks }</code>: dizideki sıra = metot id'si",
      purpose: "Hangi kodun çalıştığını, test sınıfının girildiği anı ve başlatma (static ctor, önbellek doldurma) kapsamlarını görebilmek.",
      body: "<p>Her gövdeli metodun başına, sırasıyla:</p>" + C(`
// Test sınıfının girişinde (ör. TaxTests'in test metodu):
call TestDettaTracker.EnterTestInstance(this)   // sınıf kovası açılır
call TestDettaTracker.EnterTestCode()
// Her metotta:
ldc.i4  1                                       // Calculator.Tax'ın id'si
call    TestDettaTracker.Hit(int32)`) + ul([
        "Static ctor: <code>EnterTypeInitializer(id)</code> … her <code>ret</code> önünde <code>ExitTypeInitializer()</code>.",
        "<code>GetOrAdd</code>, <code>Lazy.Value</code> gibi önbellek doldurmalar <code>EnterCache</code>/<code>ExitCache</code> arasına alınır: içerde çalışan kod \"bir kez çalışır\" sayılır.",
        "İzlenen alanların okunması/yazılması: <code>Read(field)</code>, <code>Stored(field, methodId)</code> (kim değeri kurdu?).",
        "Dosya API çağrıları (<code>File.ReadAllText</code> vb.) <code>&lt;TestDettaFileReads&gt;</code> içindeki sarmalayıcılara yönlenir: önce <code>FileRead(path)</code>, sonra asıl çağrı (<code>FileReadCalls.Wrap</code>).",
        "Süreç başlatma ve assembly yükleme (<code>Process.Start</code>, <code>Assembly.LoadFrom</code>, <code>AssemblyLoadContext.LoadFromAssemblyPath</code> …) aynı yolla sarmalanır; ilk iki argüman <code>OutputUsed(first, second)</code>'a gider.",
        "Yalnız test projesinin derlerken referans verdiği projelerin dll'leri yeniden yazılır: test çıktısına kopyalanmış başka bir projenin exe'si (LibGit2Sharp'ın test uygulaması) dokunulmadan kalır, ayrı süreçte çalışabilsin. Aynı adı paylaşan projelerde (bir kütüphanenin sürümleri, her birinin kendi test projesi) ad, sahiplerinden herhangi biri kapanıştaysa yeniden yazılır; test dll'i de dahil.",
        "Fixture kurulumu: <code>EnterFixtureInstance(this)</code>; veri kaynağı: <code>EnterDataSource(\"Sınıf\")</code>.",
      ]),
    },
    {
      title: "İzleyici çalışma zamanı çıktıya konur", nodes: ["dlls", "deploy"], edges: ["d"],
      who: "<code>new RuntimeDeployment(targetPath)</code>",
      file: "src/TestDetta.Coverage/RuntimeDeployment.cs",
      input: "Test dll'inin yolu",
      output: "Çıktıda <code>TestDetta.Coverage.Runtime.dll</code> ve güncellenmiş <code>deps.json</code>",
      purpose: "Yeniden yazılan dll'ler <code>TestDettaTracker</code>'ı çağırıyor; test host'unun onu yükleyebilmesi gerekir. İzleyici iki derlemeyle gelir: .NET Framework test assembly'si için <code>runtime/net462</code>, diğerleri için netstandard2.0 (<code>RuntimeDeployment.TrackerFor</code>). .NET Framework çıktısında <code>deps.json</code> yoktur; dosyayı kopyalamak yeter.",
    },
    {
      title: "Testler parçalara bölünüp tek tek koşturulur", nodes: ["instr", "run", "host"], edges: ["r", "h"],
      who: "<code>Shards</code> → <code>RunShards</code> → her parça için <code>RunShard</code>",
      file: "src/TestDetta.Coverage/CoverageRecorder.cs",
      input: "<code>dotnet test [--project] &lt;proj&gt; --no-build -c Debug --settings testdetta.runsettings</code>; ortam: <code>TESTDETTA_METHOD_COUNT</code>, <code>TESTDETTA_FIELD_COUNT</code>, <code>TESTDETTA_COVERAGE_OUTPUT=work/coverage-&lt;i&gt;.json</code>, <code>TESTDETTA_STARTUP_HOOK</code> (kanca dll'i), <code>TESTDETTA_PROCESS_STARTS=work/process-starts-&lt;i&gt;.tsv</code>",
      output: "Parça başına çıkış kodu, TRX raporu ve süreç başına bir sonuç dosyası",
      purpose: "Paralellik kapatılır (xUnit <code>MaxParallelThreads=1</code>, NUnit <code>NumberOfTestWorkers=0</code>, MSTest <code>Workers=1</code>) ki bir anda tek test sınıfı çalışsın ve vuruşlar doğru sınıfa yazılsın. Test AppDomain'leri de kapatılır (<code>DisableAppDomain</code>, xUnit <code>AppDomain=denied</code>): .NET Framework'te izleyicinin verisi aksi halde ayrı domain'de kalır.",
      body: ul([
        "<code>--parallel n</code>: sınıflar <code>n</code> parçaya dağıtılır, her parça ayrı süreç; sonda bilinen sınıfları dışlayan bir \"artakalan\" parça.",
        "Bir parça sonuç yazmazsa tek süreçte filtresiz tekrar; o da olmazsa testin kendi paralelliğiyle (<code>TESTDETTA_PARALLEL_FLOWS=1</code>).",
        "Zaman aşımı <code>RecordingTimedOutException</code>; hiç test koşmadıysa <code>NoTestsRecordedException</code>.",
        "Testler geçti ama parça sonuç yazmadıysa bu çökme değildir: tekrar denenmez, <code>ThrowIfTrackerSilent</code> projeyi \"not recorded\" yapar. Tekrarlardan sonra da sonuç yoksa ama TRX test koştuğunu söylüyorsa mesaj \"no test ran\" değil, \"host sonuç yazmadan çöktü ya da izleyiciyi yüklemedi\" olur.",
        "Enstrümantasyondan önce: test dll'i (<code>TargetPath</code>) <code>RepositoryAssemblies</code> içinde değilse sınıflar kendini işaretleyemez; testler hiç koşmadan <code>RecordingFailedException</code>.",
      ]),
    },
    {
      title: "Probe'lar vuruşları kovalara yazar", nodes: ["host", "tracker"], edges: ["t"],
      who: "<code>TestDettaTracker.Hit(int id)</code>, <code>EnterTestInstance</code>, <code>BucketForHit()</code>",
      file: "src/TestDetta.Coverage.Runtime/TestDettaTracker.cs",
      input: "Metot id'si; o anki test akışı (<code>AsyncLocal Current</code>) ve en son girilen kova",
      output: "Kova başına bit kümesi (<code>ulong[]</code>): <code>Classes[\"Calc.Tests.TaxTests\"]</code> içinde bit 1",
      purpose: "Her metodun hangi test sınıfı (ya da fixture, ya da başlangıç) sırasında çalıştığını bilmek.",
      body: C(`
public static void Hit(int id)
{
    var word = id >> 6; var bit = 1UL << (id & 63);
    if (_typeInitializerDepth != 0 || _cacheDepth != 0)
        Mark(InitializationBits, word, bit);   // static ctor / önbellek içinde: bir kez çalışan kod
    var bucket = BucketForHit();              // Current ?? _lastEntered ?? Startup
    Mark(bucket.Bits, word, bit);
    if (IsBetweenTests(bucket)) { MarkBetween(word, bit); Mark(InitializationBits, word, bit); }
    Remember(id);                             // iş parçacığı halkası: alan değerlerini kimin kurduğu
}`) + ul([
        "Hiçbir test girilmeden çalışan kod (keşif, teori verisi) <code>Startup</code> kovasına, yani <code>ambient</code>'e gider.",
        "Başlatma kapsamında çalışan kod (<code>initialization</code>) ilk kullanıcıya yazılır; seçim tarafı bunu bilir ve tüm kullanıcıları seçer.",
        "<code>FileRead(path)</code> yolu tam yola çevirip aynı kovanın <code>Files</code> kümesine ekler.",
        "<code>OutputUsed</code> başlatılan dosya, argümanlar, çalışma klasörü ve assembly adından isimleri çıkarır (son yol parçası, <code>.dll</code>/<code>.exe</code>/<code>.csproj</code> olmadan) ve kovanın <code>Outputs</code> kümesine ekler.",
        "Tracker açılırken başlatma kancasını (<code>TestDetta.Coverage.StartupHook</code>, netstandard2.0) yalnız test host'un <code>DOTNET_STARTUP_HOOKS</code>'una ekler ve <code>TESTDETTA_BUCKET</code>'ı son girilen kovada tutar. Testten başlayan her .NET süreci, kim başlatırsa başlatsın, kova + giriş assembly'si + komut satırını <code>process-starts</code> dosyasına bir satır olarak yazar.",
      ]),
    },
    {
      title: "Süreç biterken sonuçlar JSON'a yazılır", nodes: ["tracker", "results"], edges: ["w"],
      who: "<code>TestDettaTracker.WriteResults()</code>: <code>TestDettaInProcCollector.TestSessionEnd</code> (VSTest) ve <code>ProcessExit</code>",
      file: "src/TestDetta.Coverage.Runtime/TestDettaTracker.cs",
      input: "<code>TESTDETTA_COVERAGE_OUTPUT</code>, süreç id'si",
      output: "<code>coverage-0.&lt;pid&gt;.json</code> (geçici dosya + atomik taşıma)",
      purpose: "Test host'u öldürülse bile yarım dosya kalmasın; her süreç kendi dosyasını yazsın.",
      body: C(`
{ "classes": { "Calc.Tests.AddTests": [0], "Calc.Tests.TaxTests": [1] }, "fixtures": {},
  "ambient": [], "initialization": [], "linked": [], "unlinked": [], "methodCount": 2,
  "fieldBuilders": {}, "fileReads": { "classes": {}, "fixtures": {}, "ambient": [], "initialization": [] },
  "classSeconds": { "Calc.Tests.TaxTests": 0.41 }, "startupSeconds": 0.9 }`),
    },
    {
      title: "Bütün süreçlerin sonuçları birleştirilir", nodes: ["results", "merge"], edges: ["m"],
      who: "<code>CoverageRecorder.BuildMap(request, methods, resultsFiles)</code>",
      file: "src/TestDetta.Coverage/CoverageRecorder.cs:712",
      input: "<code>InstrumentationResult.Methods</code> ve parça başına sonuç dosyaları",
      output: "<code>CoverageMap(Header, Methods, Hits, Timings) { Initialization, LinkedInitialization, BuiltValues, FileReads, OutputUses, BuildInputs, … }</code>",
      purpose: "Parçaları tek haritada toplamak ve ham bitleri anlamlı bölümlere ayırmak.",
      body: ul([
        "Kovalar sınıf adına göre birleşir (<code>MergeBuckets</code>). <code>methodCount</code> üstündeki id'ler alan okumasıdır: o alanın değerini kuran metotlar (<code>fieldBuilders</code>, zincirleme) <code>BuiltValues</code>'a gider.",
        "<code>LinkedInitialization</code>: bir süreçte bağlanmış, hiçbir süreçte bağlanmamış (unlinked) olmayan başlatma kodu.",
        "Dosya okumaları <code>FileReadPaths</code> ile depo yollarına çevrilir: depo dışı atılır, <code>bin/obj</code> kopyası aynı adlı ve içerikli izlenen dosyaya eşlenir.",
        "<code>OutputUses</code>: tracker'ın <code>outputUses</code>'ı, kancanın <code>process-starts-*.tsv</code> satırları ve <code>bin/obj</code> altında okunup hiçbir izlenen dosyaya karşılık gelmeyen dosyaların adları (<code>RawOutputUses</code>).",
        "Başlık: <code>(tests/Calc.Tests/Calc.Tests.csproj, git HEAD, UtcNow)</code>.",
      ]),
    },
    {
      title: "Güven bilgileri ve harita yazılır", nodes: ["merge", "map"], edges: ["out"],
      who: "<code>Record</code> metodunun sonu, ardından <code>CoverageMapStore.Write</code>",
      file: "src/TestDetta.Coverage/CoverageRecorder.cs, src/TestDetta.Core/CoverageMapStore.cs:27",
      input: "TRX raporları, sınıf konumları, üretilmiş kaynaklar",
      output: "<code>FailedClasses</code>, <code>SkippedClasses</code>, <code>Platform</code>, <code>Framework</code>, <code>ClassLocations</code>, <code>Generated</code>, <code>InstrumentedProjects</code> eklenmiş harita dosyası",
      purpose: "Haritaya güvenilemeyecek yerleri işaretlemek: kayıtta başarısız olan sınıf her proje etkilendiğinde koşar; atlanan sınıf başka platformda seçilir. <code>InstrumentedProjects</code> (2026-10-07, <code>CoverageRecorder.InstrumentedProjects</code>): enstrümante edilen dll'lerin projeleri, depo köküne göre, sıralı; seçim bunu B2 kuralında okur (Bölüm 4). Testler geçip hiç sınıf kaydedilmediyse (<code>ThrowIfNoClassRecorded</code>) harita yazılmaz, proje \"not recorded\" olur.",
    },
    {
      title: "Dll'ler her durumda eski haline döner", nodes: ["run", "restore"], edges: ["back"],
      who: "<code>finally</code>: <code>deployment.Dispose()</code>, <code>instrumenter.Restore(instrumentation)</code>, iş klasörü silinir",
      file: "src/TestDetta.Coverage/CoverageRecorder.cs",
      input: "<code>InstrumentationResult.Backups</code> (asıl dosya → yedek)",
      output: "Kayıttan önceki dll ve pdb'ler",
      purpose: "Kayıt sonrası aynı çıktıyla normal testler koşabilsin; yarıda kalan kayıt depoyu bozmasın.",
    },
  ],
};
