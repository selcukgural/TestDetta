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
      file: "src/TestDetta.Core/CoverageMapStore.cs:29",
      input: "<code>PathFor(repoRoot, csproj)</code> → <code>&lt;kök&gt;/.testdetta/coverage/Calc.Tests.json</code>, ve <code>CoverageMap</code>",
      output: "Format sürümü 5 olan sıkıştırılmamış JSON (üretilmiş kaynaklar içinde gzip+base64). Başka formattaki bir harita okunmaz (0.1.x'in yazdığı format 2, 0.2.0–0.2.2'nin yazdığı format 3 ve 0.3.0'ın yazdığı format 4 dahil): <code>AnalysisPipeline.ReadMap</code> \"record the maps again\" uyarısıyla onu atlar, proje haritasız, isimlerle seçilir. Format 3 (2026-10-12): fixture türünü kendisi kuran bir testin kodu kendi sınıfında kalır; format 2 harita bunu ayırt edemez. Format 4 (2026-10-19): <code>collectionFixtureUsers</code>, xUnit collection fixture'larının collection üyeleri; format 3 haritada yoktur, fixture'ı anmayan bir üye kaçabilirdi. Format 5 (2026-10-21): aynı alanlar; paylaşılan bir test kütüphanesinde tanımlı xUnit v3 assembly fixture'ının kurulumu artık assembly setup scope'una yazılır, format 4 harita onu testlerin dışında çalışan kod sayardı ve ayırt edilemez",
      purpose: "Haritayı bir commit'e bağlı, sonradan okunabilir bir dosya olarak saklamak. <code>.testdetta/</code> git'e girmez.",
      body: C(`
{ "version": 5, "testProject": "tests/Calc.Tests/Calc.Tests.csproj", "commit": "9f3c…",
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
      input: "<code>base</code> (PR'ın taban dalı), <code>max-map-age</code> (50), <code>drift-fallback</code> (all), <code>dotnet-test-args</code>",
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
      title: "td test başlar", nodes: ["cipr", "td"], edges: ["start"],
      who: "<code>CliApp.Run(args, stdout, stderr, CliHost)</code> komut satırını ayrıştırır ve komutu kendi akışına gönderir",
      file: "src/TestDetta.Cli/CliApp.cs",
      input: "<code>td test --base origin/main --max-map-age 50 --drift-fallback all --summary-file $GITHUB_STEP_SUMMARY -- -c Debug</code>",
      output: "Seçilen akış: <code>td test</code> analize gider; <code>--shadow</code> <code>CliApp.Shadow</code>'a, <code>td audit</code> <code>CliApp.Audit</code>'e",
      purpose: "Seçim her depoda ve her makinede çalışır (TestDetta 2026-10-24'ten beri Apache-2.0 ile açık kaynak ve ücretsizdir). TestDetta'nın kendisi hiçbir komutta ağ çağrısı yapmaz.",
      body: ul([
        "<b>Shadow mode</b> (<code>td test --shadow</code>) <code>CliApp.Shadow</code>'a gider. Ağı kullanan TestDetta değildir: Action aracı nuget.org'dan indirir ve Actions önbelleğini kullanır, base commit'teki yeniden koşu o commit'in paketlerini her derleme gibi geri yükler.",
        "<code>td --version</code> komut ayrıştırılmadan önce <code>CliApp.Version</code>'ı yazar (paket sürümü + derlendiği commit, <code>AssemblyInformationalVersion</code>); shadow ve audit raporları da sürümü buradan alır.",
        "<b>td audit</b> <code>CliApp.Audit</code>'e gider (SIGINT/SIGTERM iptal belirtecine bağlanır); TestDetta'nın kendi ağ çağrısı yoktur (docs/design/audit.md).",
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
        "<b>Ekibin yapılandırması</b> (2026-10-10, docs/design/team-test-configuration.md): VSTest'te komut satırındaki <code>--filter</code> projenin <code>VSTestTestCaseFilter</code>'ını ezer; <code>TestFilter.WithTeamFilter</code> onu kullanıcının filtresi gibi ekler ve <code>WithinUserFilter</code> <code>(ekip)&amp;(TestDetta)</code> koşturur. Kullanıcının kendi <code>--filter</code>'ı varsa o geçerli (dotnet test de öyle yapar). MTP projenin <code>TestingPlatformCommandLineArguments</code>'ını zaten uygular, runsettings içindeki <code>TestCaseFilter</code> da komut satırınınkiyle birlikte geçerlidir. Konsol ve iş özeti korunan yapılandırmayı yazar (<code>ResultWriter.TeamConfiguration</code>).",
        "Filtre hiç teste uymazsa (\"No test matches…\" ya da MTP çıkış kodu 8) proje filtresiz yeniden koşar: daha fazla, asla daha az.",
        "Skipped projeler için komut üretilmez; bir proje başarısız olsa da diğerleri koşar.",
      ]),
    },
    {
      title: "Sonuç ve gerekçeler iş özetine yazılır", nodes: ["dt", "sum"], edges: ["report"],
      who: "<code>TextReport.WriteRunSummary</code> (konsol, tablo) ve <code>MarkdownSummary.Write</code> (dosya)",
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
      file: "src/TestDetta.Cli/CliApp.cs:399",
      input: "<code>RecordSettings(Configuration, Projects, Parallelism, Framework) { NoBuild, Incremental, Thresholds, DotnetTestArguments }</code>; <code>td record … -- &lt;ekibin dotnet test argümanları&gt;</code> (Action'da <code>dotnet-test-args</code>)",
      output: "<code>ProjectGraph</code> ve kaydedilecek test projelerinin listesi",
      purpose: "Hangi projelerin test projesi olduğunu (MSBuild değerlendirmesi ya da <code>*.Tests</code> adı) bulmak.",
      body: ul([
        "<code>GitClient.TopLevel(repo)</code> depo kökünü verir.",
        "<code>ProjectGraphLoader.Load(root)</code> her <code>.csproj</code>'u MSBuild ile değerlendirir (önbellek: <code>.testdetta/cache/graph</code>). Ardından <code>LinkPackagesToProjects</code>: deponun kendi projelerinden birinin kimliğine (düz <code>PackageId</code>'si ya da assembly adı) verilen <code>PackageReference</code> o projeye referans sayılır; NuGet aynı restore'da ikisi varsa paketin yerine projeyi kullanır, bu yüzden bağ yalnız bir projenin referans kapanışı ikisini birden tutuyorsa eklenir (<code>RestoredTogether</code>; paketi tek başına alan, ör. Vogen'in tüketici testleri, yayınlanmış paketi çalıştırır) (2026-10-11, AICentral: <code>AICentral</code> Core'u paket, testler proje olarak alıyordu; bağ yokken <code>HttpContextWrapper</code> \"hiçbir kod anmıyor\" sanılıp üç test projesi tamamen koşuyordu).",
        "<code>--project</code> verilmemişse bütün test projeleri kaydedilir.",
        "<code>--incremental</code> ile her proje için önce bir adım planlanır (son adım: \"Artımlı kayıt\"); yoksa hepsi tam kaydedilir.",
      ]),
    },
    {
      title: "Kayıt isteği kurulur", nodes: ["graph", "rec"], edges: ["call"],
      who: "<code>CoverageRecorder.Record(RecordRequest request, TextWriter output)</code>",
      file: "src/TestDetta.Coverage/CoverageRecorder.cs:84 (RecordClasses:95)",
      input: "<code>RecordRequest(RepoRoot, Graph, TestProject, RecordOptions(Configuration = \"Debug\", Framework = null, Parallelism = 1, Timeout = null) { NoBuild })</code>",
      output: "<code>(CoverageMap Map, bool TestsPassed)</code>",
      purpose: "Tek bir test projesinin baştan sona kaydı. Aşağıdaki adımların hepsi ortak <code>RecordShards</code> metodunun içindedir; <code>RecordClasses</code> aynı yolu <code>RecordOptions.Classes</code> ile yalnız seçilen sınıflar için yürür ve <code>PartialRecording(Map, TestsPassed, AssemblyClasses, Complete)</code> döner.",
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
      output: "<code>List&lt;GeneratedSource(Project, Name, Text) { Framework }&gt;</code>, sınıf → <code>[(File, Line)]</code> ve proje başına <code>BuildInputs</code> (git'in izlemediği derlenen belgelerin PDB sağlama toplamları, gömülü kaynakların SHA-256'sı); <code>-f</code> olmadan kaydedilen çok framework'lü projede <code>ReadOtherFrameworks</code> diğer framework'lerin üretilen kodunu ve girdilerini de okur (<code>FrameworkBuildInputs</code>)",
      purpose: "Kaynak jeneratörlerinin ürettiği kodu (diskte olmayan, PDB'ye gömülü belgeler) ve sınıfların kaynakta nerede olduğunu haritaya eklemek. Yeniden yazmadan önce yapılır, çünkü Cecil PDB'yi değiştirir.",
    },
    {
      title: "Dll'ler probe'larla yeniden yazılır", nodes: ["rec", "instr", "dlls"], edges: ["i1", "i2"],
      who: "<code>AssemblyInstrumenter.Instrument(assemblies, testAssemblies, backupDirectory, thirdParty, productAssemblies)</code>",
      file: "src/TestDetta.Coverage/AssemblyInstrumenter.cs:83 (InstrumentOne:325, InsertProbes:1007)",
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
        "<b>Üçüncü taraf dll'ler</b> (2026-10-10, docs/design/non-code-files.md): depo dll'lerinden sonra test çıktısındaki diğer her <code>.dll</code> (<code>ThirdPartyAssemblies.Find</code>: deponun bütün assembly adları, uydu kaynak dll'leri, test platformu, adaptörler, kapsam toplayıcıları ve TestDetta'nın kendisi hariç) açılır; üye referanslarında sarmalanan bir okuma ya da tarama metodu varsa (<code>FileReadCalls.ReferencesReaders</code>) yalnız okuma ve tarama sarmalayıcılarıyla yeniden yazılır (tarama 2026-10-12): probe yok, id yok, süreç başlatma sarmalayıcısı yok (<code>AssemblyInstrumenter.WrapThirdPartyReads</code>). Verify'ın <code>*.verified.txt</code> okuması böylece onu koşan sınıfın altına düşer. Yeniden yazılamayan dll (mixed-mode, yazma hatası) yedekten geri konur ve haritanın <code>fileReadsUnseen</code> listesine girer.",
        "Süreç başlatma ve assembly yükleme (<code>Process.Start</code>, <code>Assembly.LoadFrom</code>, <code>AssemblyLoadContext.LoadFromAssemblyPath</code> …) aynı yolla sarmalanır; ilk iki argüman <code>OutputUsed(first, second)</code>'a gider. Taramalar (2026-10-12, <code>FileReadCalls.Scanners</code>) kendi probe'larına gider, <code>OutputScanned(alıcı)</code>: <code>AppDomain.GetAssemblies</code> ve <code>AssemblyLoadContext.Assemblies</code> o anda yüklü her assembly'yi, <code>Assembly.GetTypes</code>/<code>GetExportedTypes</code>/<code>DefinedTypes</code>/<code>ExportedTypes</code> çağrıldığı assembly'yi tarama olarak, <code>scan:</code> önekiyle yazar (<code>TestDettaTracker.ScanPrefix</code>). Bir assembly süreç başına bir kez yüklenir; önceki bir sınıfın yüklediği eklentiyi bulup türlerine bakan sınıf yükleme yapmaz ama bu çağrıyı yapar, bu yüzden sıra artık belirlemez. Ayrı probe, çünkü <code>LoadFromAssemblyPath</code>'in ilk argümanı da bir bağlam: yükleme, bağlamdaki her şeyin taraması değildir (<code>OrderDependentLoadTests</code>). Şekil okumaları (2026-10-12, <code>FileReadCalls.ShapeReaders</code>): <code>Type.GetProperties</code>, <code>GetCustomAttributes</code>, System.Text.Json'un <code>JsonSchemaExporter</code>/<code>GetTypeInfo</code> çağrıları depo ve üçüncü taraf kodda <code>ShapeRead(ilk, ikinci)</code>'a gider; okunan depo türü (taban türleri ve özelliklerinin tuttuğu depo türleri, iki seviye) <code>shape:Tür</code> olarak yazılır. Depo türü: derlemesinde enstrümantörün eklediği <code>&lt;TestDettaRepository&gt;</code> işaret türü olan. Seçim: bir türün metadata'sı değişince şeklini okuyan sınıflar da seçilir (<code>CoverageSelection.AddShapeReaders</code>; OData'nın taban controller özniteliğini türetilenler üzerinden okuması). Testlerin dışında da çalışan koddaki ada göre yüklemeler (<code>Assembly.Load(name)</code>, <code>LoadFromAssemblyName</code>, <code>ExecuteAssemblyByName</code>) de ayrı yazılır: <code>OutputLoadedByName</code>, <code>name:</code> öneki (<code>NameLoadPrefix</code>, 2026-10-11); Carter'ın modül kataloğu bağımlılık bağlamındaki her kütüphaneyi adıyla yüklüyordu. Hangi kodun testlerin dışında çalıştığını <code>ProjectGraph.RunsOutsideTests</code> söyler (hiçbir projenin referans vermediği bir uygulama ya da örnek, veya testlerin dışında çalışan test dışı bir projenin referans verdiği proje); kayıt bunu derleme başına <code>Instrument(..., productAssemblies)</code> ile geçirir. Testin ya da yalnız testlerin kullandığı bir yardımcının kendi ada göre yüklemesi yola göre yükleme gibi kalır: AfterApply'ın mimari testleri katmanları adıyla yükleyip NetArchTest ile gövdelerin bağımlılıklarını inceler. Ada göre yükleme görülen bir yükleme sayılır (<code>outputLoadsUnseen</code>'e girmez), tarama sayılmaz. Taramayı ve ada göre yüklemeyi yalnız çalışma anında bulunan çıktıların kuralları (opak çıktılar, <code>AnswerByLoaders</code>) sayar; derlenen bir projenin yolundan yüklenmesi kuralı (<code>AddLoadedOutputUsers</code>) saymaz: Newtonsoft.Json'un başlatmadaki taraması taze kayıtta her değişikliği bütün sınıflara yayıyordu (%100).",
        "Yalnız test projesinin derlerken referans verdiği projelerin dll'leri yeniden yazılır: test çıktısına kopyalanmış başka bir projenin exe'si (LibGit2Sharp'ın test uygulaması) dokunulmadan kalır, ayrı süreçte çalışabilsin. Aynı adı paylaşan projelerde (bir kütüphanenin sürümleri, her birinin kendi test projesi) ad, sahiplerinden herhangi biri kapanıştaysa yeniden yazılır; test dll'i de dahil.",
        "<b>Assembly fixture'ları (2026-10-21):</b> <code>AssemblyFixtures.Find</code>, hiçbir dll yeniden yazılmadan önce test dll'lerindeki <code>[assembly: AssemblyFixture(typeof(X))]</code>'leri okur; <code>X</code>'i (generic ise tanımını) ve depodaki taban türlerini (assembly adı, tür adı) listeler. Her dll yeniden yazılırken bu türlerin kurucuları ve <code>InitializeAsync</code>'i <code>AddAssemblyFixtureSetups</code> ile assembly setup scope'una (<code>[setup-scope]</code>) alınır; fixture paylaşılan bir test kütüphanesinde olsa da. Önceden yalnız test dll'indekiler alınıyordu, kütüphanedekinin kurulumu testlerin dışında çalışan kod sayılıyordu.",
        "Fixture kurulumu: <code>EnterFixtureInstance(this)</code> (2026-10-12: çağrı yığınında çalışan testin kendi kodu varsa, ör. test <code>new StoreFixture()</code> diyorsa, testin kovası kalır, <code>CalledFromRunningTest</code>; aynı gün: o test fixture\'ın kullanıcısı da sayılır, <code>fixtureBuilders</code>, ve akışının geri kalanı, başlattığı task\'lar dahil, fixture kovasına da yazılır, <code>TestBuiltFixture</code>: FastEndpoints fixture türü başına bir sunucu önbelleğe alır); fixture'ın senkron <code>Dispose()</code>'u (2026-10-11): <code>EnterFixtureTeardown(this)</code> → yerel değişken, gövde <code>try</code>/<code>finally</code> içinde <code>ExitFixtureTeardown(kayıt)</code> ile önceki kova geri gelir; hiçbir test kodu çalışmıyorsa (paralel kayıtta akış testlerin dışındaysa) fixture kovasına geçilir, test içinde çağrılırsa testin kovası kalır (GraphQL: fixture'ın kapattığı şema yüzünden şemayı kuran kod \"testlerin dışında\" sayılıyordu). Veri kaynağı: <code>EnterDataSource(\"Sınıf\")</code>; birden çok sınıfın kullandığı (ya da soyut bir tabanın tanımladığı) veri üyesi (2026-10-11): <code>EnterSharedDataSource(\"[data-source]Tip\")</code> kendi kovasına girer, kullanıcıları (özniteliği taşıyan sınıflar ve onlardan türeyen her somut sınıf) haritanın <code>dataSourceUsers</code> alanına yazılır ve seçim onları fixture kullanıcısı gibi okur (generic bir kullanıcı varsa eski kural: testlerin dışı). Repodaki test eklentisi öznitelikleri (2026-10-12, <code>ExtensionAttributes.Find</code>, kayıt öncesi test derlemeleri okunur): test sınıfı/metodu/parametresine konan repo öznitelik tipi (<code>System.*</code> ve <code>Microsoft.CodeAnalysis.*</code> gömülüleri hariç) aynı paylaşılan kovayı alır, kullanıcıları onu taşıyan sınıflar ve onlardan türeyen somut sınıflardır; metotları artık üçüncü taraf geri çağırma (tek seferlik kod) sayılmaz (AutoFixture <code>[AutoData]</code>, FsCheck <code>[Property]</code>). xUnit collection üyeliği (2026-10-19, <code>CollectionFixtures.Find</code>): test derlemesinde <code>[Collection(\"ad\")]</code>, <code>[Collection(typeof(T))]</code> ya da <code>[Collection&lt;T&gt;]</code> taşıyan (ya da bunu taşıyan bir tabandan türeyen) her somut sınıf, collection tanımının <code>ICollectionFixture&lt;F&gt;</code> fixture'larının kullanıcısı olarak haritanın <code>collectionFixtureUsers</code> alanına yazılır; ad, tracker'ın fixture adıyla aynı yazılır (iç içe tip <code>+</code>, generic argümanlar köşeli parantezde).",
      ]),
    },
    {
      title: "İzleyici çalışma zamanı çıktıya konur", nodes: ["dlls", "deploy"], edges: ["d"],
      who: "<code>new RuntimeDeployment(targetPath)</code>",
      file: "src/TestDetta.Coverage/RuntimeDeployment.cs",
      input: "Test dll'inin yolu",
      output: "Çıktıda <code>TestDetta.Coverage.Runtime.dll</code> ve güncellenmiş <code>deps.json</code>",
      purpose: "Yeniden yazılan dll'ler <code>TestDettaTracker</code>'ı çağırıyor; test host'unun onu yükleyebilmesi gerekir. İzleyici iki derlemeyle gelir: .NET Framework test assembly'si için <code>runtime/net462</code>, diğerleri için netstandard2.0 (<code>RuntimeDeployment.TrackerFor</code>). .NET Framework çıktısında <code>deps.json</code> yoktur; dosyayı kopyalamak yeter. VSTest collector'ı da run settings'te bu kopyadan yüklenir (<code>RuntimeDeployment.DeployedPath</code>): .NET Framework iki yoldan yüklenen aynı assembly'yi iki ayrı örnek tutar, collector TestDetta'nın kendi klasöründen yüklendiğinde test sınıfları bir kopyaya işaretlenip sonuçları öbür kopya yazıyordu.",
    },
    {
      title: "Testler parçalara bölünüp tek tek koşturulur", nodes: ["instr", "run", "host"], edges: ["r", "h"],
      who: "<code>Shards</code> → <code>RunShards</code> → her parça için <code>RunShard</code>",
      file: "src/TestDetta.Coverage/CoverageRecorder.cs",
      input: "<code>dotnet test [--project] &lt;proj&gt; --no-build -c Debug --settings testdetta.runsettings</code>; ortam: <code>TESTDETTA_METHOD_COUNT</code>, <code>TESTDETTA_FIELD_COUNT</code>, <code>TESTDETTA_COVERAGE_OUTPUT=work/coverage-&lt;i&gt;.json</code>, <code>TESTDETTA_STARTUP_HOOK</code> (kanca dll'i), <code>TESTDETTA_PROCESS_STARTS=work/process-starts-&lt;i&gt;.tsv</code>; birden çok parçada <code>TMPDIR</code>/<code>TMP</code>/<code>TEMP</code>=<code>&lt;sistemin geçici klasörü&gt;/td&lt;hex&gt;</code>, <code>NUGET_SCRATCH</code>=<code>&lt;sistemin geçici klasörü&gt;/NuGetScratch</code>, Windows dışında <code>TESTINGPLATFORM_PIPE_DIRECTORY</code>=<code>&lt;sistemin geçici klasörü&gt;</code>",
      output: "Parça başına çıkış kodu, TRX raporu ve süreç başına bir sonuç dosyası",
      purpose: "Paralellik kapatılır (xUnit <code>MaxParallelThreads=1</code>, NUnit <code>NumberOfTestWorkers=0</code>, MSTest <code>Workers=1</code>) ki bir anda tek test sınıfı çalışsın ve vuruşlar doğru sınıfa yazılsın. Test AppDomain'leri de kapatılır (<code>DisableAppDomain</code>, xUnit <code>AppDomain=denied</code>): .NET Framework'te izleyicinin verisi aksi halde ayrı domain'de kalır.",
      body: ul([
        "<code>--parallel n</code>: sınıflar <code>n</code> parçaya dağıtılır, her parça ayrı süreç; sonda bilinen sınıfları dışlayan bir \"artakalan\" parça. MTP'de artakalan parça çoğu zaman hiç test bulmaz (çıkış kodu 8); <code>AcceptingZeroTests</code> <code>--ignore-exit-code 8</code>'i yalnız ekipte yoksa ekler, ekibin komut satırındakine 8'i katar (<code>2</code> → <code>2;8</code>). Platform ikinci bir <code>--ignore-exit-code</code>'u çıkış kodu 5 ile reddeder: EF Core'un 15 haritasından 13'ünün güvenilmez olmasının nedeni buydu.",
        "<b>Ekibin yapılandırması</b> (2026-10-10): <code>WriteRunSettings</code> ekibin runsettings dosyasının (<code>--settings</code>, yoksa <code>RunSettingsFilePath</code>/<code>VSTestSetting</code>) bir kopyasından başlar ve yalnız kendi öğelerini koyar (sıralı koşu, AppDomain yok, toplayıcı); ekibin <code>TestCaseFilter</code>, <code>EnvironmentVariables</code>, <code>TestRunParameters</code> ve toplayıcıları kalır, dosya okunamazsa kayıt durur. Parça filtresi <code>WithinTeamFilter</code> ile <code>(ekip)&amp;(parça)</code> olur. Ekibin argümanları <code>TeamTestArguments.Parse</code> ile ayrılır: filtre, ayarlar, kaydın denetlediği seçenekler (<code>-c</code>, <code>-f</code>, <code>--no-build</code>, logger, sonuç klasörü, rapor seçenekleri: atılır), geri kalanlar olduğu gibi geçer, <code>--</code> sonrası satır içi ayarlar en sona.",
        "Artımlı adımda (<code>ClassShards</code>, <code>CoverageRecorder.cs:531</code>) yalnız seçilen sınıflar parçalara dağıtılır (paralellik 1 olsa da filtreli); artakalan parça kaynak indeksinin bilmediği testleri yine koşturur. Bir parça çökerse proje filtresiz kaydedilir ve sonuç tam kayıt sayılır (<code>Complete = true</code>).",
        "Aynı anda koşan parçaların her biri kendi geçici klasörünü alır (<code>ShardTemporary</code>, parça bitince <code>RemoveTemporary</code> siler): ekibin koşusunda tek test süreci vardır; sistemin geçici klasöründe sabit adları sahiplenen ya da temizleyen fixture'lar iki parça aynı anda yapınca bir parçanın bütün sınıflarını düşürüyordu (LibGit2Sharp <code>BaseFixture</code>, 2026-10-11: 64 sınıftan 15'i, sonra her değişiklikte koştular). Klasör sistemin kendi geçici klasöründe açılır, test takımı kendi koşularındaki yolu görür: <code>/tmp</code> altındaki ilk sürümde (macOS'ta <code>/private/tmp</code>'ye bağlantı) libgit2'nin çözdüğü yollarla karşılaştıran 4 LibGit2Sharp sınıfı düştü. Unix soket yolu en çok 103 bayt (<code>SocketPathLimit</code>); en uzun soket adı (<code>LongestSocketName</code>, 41) sığmazsa <code>/tmp</code>'nin gerçek yolu kullanılır. Onaltılık ad, Türkçe <code>ToUpper</code> için <code>i</code> içermez. NuGet, ortak HTTP önbelleğine sırayla yazmak için kilitlerini geçici klasörde tutar (<code>NuGetScratch/lock</code>); parçalar bu yüzden ayrı klasör olmadan paylaşacakları scratch klasörünü korur (<code>NuGetScratch.KeepShared</code> → <code>NUGET_SCRATCH</code>, ekibin verdiği korunur; 2026-10-11: Carter'ın analizör testleri iki parçada aynı önbellek dosyasına yazıp düşüyordu). Microsoft.Testing.Platform'un soketleri (<code>MONITORTOHOST_&lt;guid&gt;</code>, 46 karakter) de sistemin geçici klasöründe kalır (<code>TestingPlatformPipes.KeepShared</code> → <code>TESTINGPLATFORM_PIPE_DIRECTORY</code>, ekibin verdiği korunur): macOS'ta parçanın kendi klasöründe yol 104 bayta çıkıyordu; hang/crash dump eklentili Orleans projelerinin her parçası başlarken çöktü, proje tek süreçte yeniden kaydedildi ve <code>Orleans.Core.Tests</code> 30 dakikaya sığmadı (2026-10-12). <code>td audit</code> ve değerlendirme işçileri de aynısını yapar.",
        "Bir parça sonuç yazmazsa tek süreçte filtresiz tekrar; o da olmazsa testin kendi paralelliğiyle (<code>TESTDETTA_PARALLEL_FLOWS=1</code>).",
        "Zaman aşımı <code>RecordingTimedOutException</code>; hiç test koşmadıysa <code>NoTestsRecordedException</code>.",
        "Parçanın TRX'i bozuk XML ise (test host'u yazarken çöktü ya da öldürüldü; 2026-10-12'de MSBuild'in bir parçası raporu bir özniteliğin ortasında kesti ve bütün kayıt düştü) <code>ReadReport</code> raporu yok sayar ve parça çıktısına bir satır yazar: başarısız bir parça raporsuz kalınca bütün sınıflar güvenilmez olur ve harita seçimde kullanılmaz (<code>Untrusted</code> null), hiçbir sınıf atlanmış bilinmez (<code>Skipped</code> null).",
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
        "<code>FileRead(path)</code> yolu tam yola çevirip aynı kovanın <code>Files</code> kümesine ekler. <code>File.Exists</code> <code>FileChecked</code>'e gider (2026-10-11): yol başında <code>?</code> ile (<code>CheckPrefix</code>) yazılır; takip edilen dosya için okuma gibi sayılır (<code>FileReadPaths</code> öneki atar), build çıktısı adlarına (<code>RawFileReads.BuildOutputs</code>) girmez. Neden: NodaTime'da <code>XmlSerializer</code> ve <code>ResourceManager</code> bulunamayan derlemeleri adıyla arar, NUnit motorunun çözücüsü de her ad için bağımlılık bağlamındaki her kütüphaneyi build çıktısında yoklar; 27 sınıf her çıktıyı \"okumuş\" görünüyordu.",
        "<b>Görülmeyen yüklemeler</b> (2026-10-10, docs/design/loaded-outputs.md): izleyici <code>AppDomain.AssemblyLoad</code>'u dinler ve açılırken yüklü olanlara bakar; önünde onu anan sarmalanmış bir çağrı olmadan yüklenen assembly (üçüncü taraf eklenti yükleyicisi, çalışma zamanı, <code>Assembly.Load(bytes)</code>, izleyiciden önce) <code>LoadsUnseen</code>'e, sonuçta <code>outputLoadsUnseen</code>'e yazılır (<code>WrappedNames</code> süreç genelinde sarmalanmış çağrıların adları).",
        "<code>OutputUsed</code> başlatılan dosya, argümanlar, çalışma klasörü ve assembly adından isimleri çıkarır (son yol parçası, <code>.dll</code>/<code>.exe</code>/<code>.csproj</code> olmadan) ve kovanın <code>Outputs</code> kümesine ekler.",
        "Başlatma kapsamındaki (statik kurucu, cache doldurma) dosya okuması ve çıktı kullanımı önce en içteki kapsama yazılır. Kapsamın sonucu izlenen bir alana yazıldıysa (bağlı kapsam) kullanımlar o alana gider (<code>GiveUses</code> → <code>FieldFiles</code>/<code>FieldOutputs</code>) ve kapsam kapanırken dıştaki kapsama aktarılır; bağlanmayan ya da yarıda kalan kapsamınkiler <code>initialization</code>'a düşer (<code>PassUses</code>). Örnek: AfterApply'ın <code>static readonly Assembly X = Assembly.Load(...)</code> alanı artık yalnız o alanı okuyan sınıfa yazılır.",
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
  "fieldBuilders": {}, "fileReads": { "classes": {}, "fixtures": {}, "ambient": [], "initialization": [], "fields": {} },
  "classSeconds": { "Calc.Tests.TaxTests": 0.41 }, "startupSeconds": 0.9 }`),
    },
    {
      title: "Bütün süreçlerin sonuçları birleştirilir", nodes: ["results", "merge"], edges: ["m"],
      who: "<code>CoverageRecorder.BuildMap(request, methods, resultsFiles)</code>",
      file: "src/TestDetta.Coverage/CoverageRecorder.cs:850",
      input: "<code>InstrumentationResult.Methods</code> ve parça başına sonuç dosyaları",
      output: "<code>CoverageMap(Header, Methods, Hits, Timings) { Initialization, LinkedInitialization, BuiltValues, FileReads, OutputUses, BuildInputs, … }</code>",
      purpose: "Parçaları tek haritada toplamak ve ham bitleri anlamlı bölümlere ayırmak.",
      body: ul([
        "Kovalar sınıf adına göre birleşir (<code>MergeBuckets</code>). <code>methodCount</code> üstündeki id'ler alan okumasıdır: o alanın değerini kuran metotlar (<code>fieldBuilders</code>, zincirleme) <code>BuiltValues</code>'a gider.",
        "<code>LinkedInitialization</code>: bir süreçte bağlanmış, hiçbir süreçte bağlanmamış (unlinked) olmayan başlatma kodu.",
        "<code>NestedMethods</code>: lambda, anonim metot ve yerel fonksiyonlardan (ve onların state machine'lerinden) derlenen metotların id'leri (<code>MethodNames.IsNested</code>). Aynı üyenin adını taşırlar ama başka zaman çalışırlar: uç nokta kaydı her host açılışında, handler yalnız istekte. Üst düzey kodun metodu <code>&lt;Main&gt;$</code> (ve async giriş noktası <code>&lt;Main&gt;</code>) kendi adını, <code>&lt;Main&gt;$</code>\'ı korur; kaynak indeksi de onu böyle adlandırır (2026-10-12: önceden <code>Main</code> olarak kaydediliyordu, host\'u kayıtları çalıştırıp hiçbir lambda\'ya girmeyen sınıf eşleşmiyordu, <code>TopLevelStatementsTests</code>).",
        "<code>fileReads.fields</code> ve <code>outputUses.fields</code> (alan → yollar/adlar): bir kova o alanı ya da değeri ondan kurulan bir alanı okuduysa (zincirleme, <code>FieldReads.AddFields</code>), alanın kullanımları o kovanın kendi okuması/kullanımı sayılır (<code>AddFieldUses</code>). Harita biçimi değişmez.",
        "Dosya okumaları <code>FileReadPaths</code> ile depo yollarına çevrilir: depo dışı atılır, <code>bin/obj</code> kopyası aynı adlı ve içerikli izlenen dosyaya eşlenir.",
        "<code>OutputLoadsUnseen</code>: süreçlerin <code>outputLoadsUnseen</code> listelerinin birleşimi (<code>CoverageRecorder.LoadsUnseen</code>); bir süreç yazmadıysa null. <code>CoverageMap.KnowsLoadsOf(names)</code>: yüklemeler izlendi ve adlardan hiçbiri görülmeden yüklenmedi.",
        "<code>OutputUses</code>: tracker'ın <code>outputUses</code>'ı, kancanın <code>process-starts-*.tsv</code> satırları ve <code>bin/obj</code> altında okunup hiçbir izlenen dosyaya karşılık gelmeyen dosyaların adları (<code>RawOutputUses</code>).",
        "Başlık: <code>(tests/Calc.Tests/Calc.Tests.csproj, git HEAD, UtcNow)</code>.",
      ]),
    },
    {
      title: "Güven bilgileri ve harita yazılır", nodes: ["merge", "map"], edges: ["out"],
      who: "<code>Record</code> metodunun sonu, ardından <code>CoverageMapStore.Write</code>",
      file: "src/TestDetta.Coverage/CoverageRecorder.cs, src/TestDetta.Core/CoverageMapStore.cs:29",
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
    {
      title: "Artımlı kayıt: yalnız değişikliğin ulaşabildiği sınıflar (2026-10-09)", nodes: ["cli", "rec", "map"], edges: ["load", "call", "out"],
      who: "<code>IncrementalRecording.Plan</code> → <code>CoverageRecorder.RecordClasses</code> → <code>CoverageMapMerge.Merge</code>",
      file: "src/TestDetta.Cli/IncrementalRecording.cs:58 (Select:164), src/TestDetta.Core/CoverageMapMerge.cs:31 (UnobservedBy:113), src/TestDetta.Core/MapDivergence.cs:20",
      input: "<code>td record --incremental [--max-map-age n] [--full-record-every 20] [--full-record-days 7] [--full-record-share 60]</code>; <code>.testdetta/coverage</code> (ya da <code>--maps-dir</code>) içindeki önceki harita, commit'i A",
      output: "Proje başına <code>ProjectStep</code>: <code>Full</code>, <code>Partial</code>, <code>Carry</code> ya da <code>Current</code>; HEAD'de (H) birleşik harita, <code>Lineage(FullCommit, FullRecordedAt, Steps)</code> ve <code>Unobserved</code> ile",
      purpose: "Her merge'de bütün testleri enstrümante koşturmak yerine A..H değişikliğinin etkileyebileceği sınıfları yeniden kaydetmek, gerisini taşımak (docs/design/incremental-recording.md). Seçim, <code>td test --base A</code>'nın yapacağı seçimin aynısıdır: A'daki harita kendi commit'inde tam haritadır.",
      body: ul([
        "Tam kayıt sebepleri: önceki harita yok ya da okunamıyor, harita sınıfları tam bilmiyor (<code>KnowsClasses</code>), başka platform ya da framework, A <code>--max-map-age</code> içinde ata değil, eşikler (adım sayısı, gün), seçim projenin tamamı (<code>TestRunMode.All</code>, ör. reflection ile bulunabilen yeni tür) ya da sınıfların %60'ından fazlası.",
        "<code>Carry</code>: projede hiçbir şey değişikliği göremez; harita H commit'ine taşınır, soy bir adım uzar.",
        "<code>Merge</code>: seçilen sınıflar yeni girdilerini alır, diğerleri A'dan (yöntem id'leri tip+üye adıyla yeniden eşlenir); ambient, başlatma ve okumalar iki tarafın birleşimi; H'de silinmiş bir metodu anan taşınmış sınıf <code>FailedClasses</code>'a girer; test dll'inin PDB'sinde artık olmayan sınıf düşer.",
        "<code>UnobservedBy</code>: A..H'de eklenen üyeler, bildirimi eklenen/değişen türlerin bütün üyeleri ve metadata'sı değişen üyeler <code>Unobserved</code> olur ve bir sonraki tam kayda kadar öyle kalır (Bölüm 4, Senaryo 12).",
        "Tam kayıt, yerini aldığı artımlı haritayla karşılaştırılır (<code>MapDivergence.Find</code>): bir sınıf artımlı girdisinde olmayan (bilinen, görülmemiş ya da başlatma kodu olmayan) bir üyeyi koştuysa özet bunu \"selection gap worth reporting\" diye listeler.",
      ]),
    },
  ],
};
