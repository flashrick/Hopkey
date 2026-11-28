using System.Diagnostics;
using Hopkey.Core.Config;
using Hopkey.Core.Exclusion;
using Hopkey.Core.History;
using Hopkey.Core.Indexing;
using Hopkey.Core.Native;
using Hopkey.Core.Pinyin;
using Hopkey.Core.Search;
using Hopkey.Core.Search.Providers;
using Hopkey.Core.Update;

namespace Hopkey.SelfTest;

/// <summary>
/// Zero-dependency self test for Hopkey.Core: unit tests, an end-to-end index test on a temp folder,
/// and a synthetic large-index performance benchmark.
/// Usage: Hopkey.SelfTest [--no-perf] [--perf-only] [--entries N] [--crawl DIR]
/// Exit code 0 = all checks passed.
/// </summary>
public static class Program
{
    private static int s_failed, s_passed;
    private static readonly List<string> s_failures = new();
    private static readonly List<string> s_perfWarnings = new();

    public static int Main(string[] args)
    {
        bool perf = !args.Contains("--no-perf");
        bool perfOnly = args.Contains("--perf-only");
        int entries = 2_000_000;
        string? crawlDir = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--entries") entries = int.Parse(args[i + 1]);
            if (args[i] == "--crawl") crawlDir = args[i + 1];
        }

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        PinyinInitialsTable.Initialize();

        if (!perfOnly)
        {
            Section("HotkeySpec", HotkeyTests);
            Section("Calculator", CalculatorTests);
            Section("QueryParser", QueryParserTests);
            Section("ExclusionMatcher", ExclusionTests);
            Section("NameMatcher", NameMatcherTests);
            Section("Pinyin", PinyinTests);
            Section("TopN", TopNTests);
            Section("Index end-to-end (temp folder)", IndexEndToEnd);
            Section("Exclusion prune on index", PruneTests);
            Section("USN hard-link changes (temp folder)", HardLinkTests);
            Section("History", HistoryTests);
            Section("User data export/import", UserDataTests);
            Section("Onboarding", OnboardingTests);
            Section("Updates", UpdateTests);
        }
        if (perf) Section($"Performance (synthetic {entries:N0} entries)", () => PerfBenchmark(entries));
        if (crawlDir != null) Section($"Crawl benchmark {crawlDir}", () => CrawlBenchmark(crawlDir));

        Console.WriteLine();
        Console.WriteLine($"RESULT: {s_passed} passed, {s_failed} failed, {s_perfWarnings.Count} perf warnings");
        foreach (var f in s_failures) Console.WriteLine("  FAIL " + f);
        foreach (var w in s_perfWarnings) Console.WriteLine("  PERF-WARN " + w);
        return s_failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ helpers

    private static string ReleaseJson(string tag, string asset, string? digest, string host = "github.com", bool prerelease = false) =>
        "{\"tag_name\":\"" + tag + "\",\"draft\":false,\"prerelease\":" + (prerelease ? "true" : "false") +
        ",\"html_url\":\"https://github.com/flashrick/Hopkey/releases/tag/" + tag + "\",\"assets\":[{\"name\":\"" + asset +
        "\",\"size\":1068459,\"browser_download_url\":\"https://" + host + "/flashrick/Hopkey/releases/download/" + tag + "/" + asset + "\"" +
        (digest == null ? "" : ",\"digest\":\"" + digest + "\"") + "}]}";

    private static void UpdateTests()
    {
        const string sha = "9e2d962611ae9ed70f2e65c8d118224d6ff395f4490c74f5e1e1bae136bf6514";
        Check(UpdateChecker.TryParseTag("v0.5.0", out var v) && v == new Version(0, 5, 0), "parse tag v0.5.0");
        Check(UpdateChecker.TryParseTag("1.2", out v) && v == new Version(1, 2, 0), "parse tag 1.2");
        Check(UpdateChecker.TryParseTag("v1.0.0-beta+abc", out v) && v == new Version(1, 0, 0), "parse tag with suffix");
        Check(!UpdateChecker.TryParseTag("latest", out _), "reject non-version tag");

        var r = UpdateChecker.ParseLatest(ReleaseJson("v0.6.0", "Hopkey-v0.6.0-win-x64.zip", "sha256:" + sha.ToUpperInvariant()));
        Check(r != null && r.Version == new Version(0, 6, 0) && r.Sha256 == sha && r.Size == 1068459, "parse latest release");
        Check(r != null && UpdateChecker.IsNewer(r, new Version(0, 5, 0)), "0.6.0 newer than 0.5.0");
        Check(r != null && !UpdateChecker.IsNewer(r, new Version(0, 6, 0, 0)), "same version is not newer");
        Check(r != null && !UpdateChecker.IsNewer(r, new Version(0, 10, 0)), "0.6.0 older than 0.10.0");
        Check(UpdateChecker.ParseLatest(ReleaseJson("v0.6.0", "Hopkey-v0.6.0-win-x64.zip", null)) == null, "no digest → rejected");
        Check(UpdateChecker.ParseLatest(ReleaseJson("v0.6.0", "Hopkey-v0.6.0-win-x64.zip", "md5:abc")) == null, "non-sha256 digest → rejected");
        Check(UpdateChecker.ParseLatest(ReleaseJson("v0.6.0", "Hopkey-v0.6.0-win-x64.zip", "sha256:" + sha, host: "evil.example.com")) == null, "untrusted host → rejected");
        Check(UpdateChecker.ParseLatest(ReleaseJson("v0.6.0", "Other.zip", "sha256:" + sha)) == null, "missing win-x64 asset → null");
        Check(UpdateChecker.ParseLatest(ReleaseJson("v0.6.0", "Hopkey-v0.6.0-win-x64.zip", "sha256:" + sha, prerelease: true)) == null, "prerelease ignored");
        Check(UpdateChecker.IsTrustedUrl("https://objects.githubusercontent.com/x") && !UpdateChecker.IsTrustedUrl("http://github.com/x"), "trusted URL rules");

        // Applying a staged release: files replaced, extras kept, rollback on failure.
        var root = Path.Combine(Path.GetTempPath(), "hk-update-test-" + Guid.NewGuid().ToString("N")[..8]);
        var target = Path.Combine(root, "My Hopkey (any name)");
        var stage = Path.Combine(root, "stage");
        var backup = Path.Combine(root, "backup");
        try
        {
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(stage);
            foreach (var f in UpdateApplier.RequiredFiles)
            {
                File.WriteAllText(Path.Combine(target, f), "old " + f);
                File.WriteAllText(Path.Combine(stage, f), "new " + f);
            }
            File.WriteAllText(Path.Combine(target, "user-notes.txt"), "keep me");
            File.WriteAllText(Path.Combine(stage, "README.md"), "new readme");
            File.WriteAllText(Path.Combine(root, "abc.txt"), "abc");
            Eq(UpdateChecker.Sha256Of(Path.Combine(root, "abc.txt")), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "sha256 of 'abc'");
            Check(UpdateApplier.CanWrite(target), "temp target writable");

            int n = UpdateApplier.Apply(stage, target, backup, TimeSpan.FromSeconds(1));
            Eq(n, UpdateApplier.RequiredFiles.Length + 1, "files copied");
            Check(UpdateApplier.RequiredFiles.All(f => File.ReadAllText(Path.Combine(target, f)) == "new " + f), "files replaced");
            Eq(File.ReadAllText(Path.Combine(target, "README.md")), "new readme", "new file added");
            Eq(File.ReadAllText(Path.Combine(target, "user-notes.txt")), "keep me", "unrelated file kept");
            Eq(File.ReadAllText(Path.Combine(backup, "Hopkey.exe")), "old Hopkey.exe", "overwritten file backed up");

            // Rollback: a locked file makes the update fail; everything goes back to the previous state.
            foreach (var f in UpdateApplier.RequiredFiles) File.WriteAllText(Path.Combine(stage, f), "newer " + f);
            File.Delete(Path.Combine(target, "README.md"));
            bool threw = false;
            using (new FileStream(Path.Combine(target, "Hopkey.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { UpdateApplier.Apply(stage, target, backup, TimeSpan.FromMilliseconds(300)); }
                catch (IOException) { threw = true; }
            }
            Check(threw, "locked file → update fails");
            Check(UpdateApplier.RequiredFiles.All(f => File.ReadAllText(Path.Combine(target, f)) == "new " + f), "rollback restored every file");
            Check(!File.Exists(Path.Combine(target, "README.md")), "rollback removed files the update added");

            File.Delete(Path.Combine(stage, "Hopkey.Core.dll"));
            bool rejected = false;
            try { UpdateApplier.Apply(stage, target, backup, TimeSpan.Zero); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && File.ReadAllText(Path.Combine(target, "Hopkey.exe")) == "new Hopkey.exe", "incomplete package rejected before copying");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void Section(string name, Action body)
    {
        Console.WriteLine($"== {name}");
        try { body(); }
        catch (Exception ex) { Fail($"{name}: exception {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
    }

    private static void Check(bool cond, string what)
    {
        if (cond) { s_passed++; return; }
        Fail(what);
    }

    private static void Eq<T>(T actual, T expected, string what) =>
        Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{what}: expected <{expected}> got <{actual}>");

    private static void Fail(string what)
    {
        s_failed++;
        s_failures.Add(what);
        Console.WriteLine("  FAIL " + what);
    }

    private static void Perf(string name, double value, double target, string unit = "ms")
    {
        bool ok = value <= target;
        Console.WriteLine($"  PERF {name,-38} {value,10:F2} {unit}  target <= {target} {unit}  {(ok ? "OK" : "WARN")}");
        if (!ok) s_perfWarnings.Add($"{name}: {value:F2}{unit} > {target}{unit}");
    }

    // ------------------------------------------------------------------ unit tests

    private static void HotkeyTests()
    {
        var h = HotkeySpec.Parse("Ctrl+Alt+Space");
        Check(h.IsValid && !h.IsDoubleCtrl, "Ctrl+Alt+Space valid");
        Eq(h.Modifiers, User32.MOD_CONTROL | User32.MOD_ALT, "Ctrl+Alt modifiers");
        Eq(h.VirtualKey, 0x20u, "Space vk");
        Eq(h.ToString(), "Ctrl+Alt+Space", "round trip");
        Check(HotkeySpec.Parse("DoubleCtrl").IsDoubleCtrl, "DoubleCtrl");
        Check(!HotkeySpec.Parse("Ctrl+Foo").IsValid, "invalid key name");
        Check(!HotkeySpec.Parse("").IsValid, "empty");
        Eq(HotkeySpec.Parse("ctrl+g").ToString(), "Ctrl+G", "case-insensitive parse");
        Eq(HotkeySpec.Parse("F12").VirtualKey, 0x7Bu, "F12");
    }

    private static void CalculatorTests()
    {
        static double Ev(string s) => Calculator.TryEvaluate(s, out var v) ? v : double.NaN;
        Eq(Ev("1+2*3"), 7.0, "precedence");
        Eq(Ev("(1+2)*3"), 9.0, "parens");
        Eq(Ev("2^3^2"), 512.0, "right-assoc pow");
        Eq(Ev("-3+5"), 2.0, "unary minus");
        Eq(Ev("10/4"), 2.5, "division");
        Eq(Ev("7%3"), 1.0, "modulo");
        Eq(Ev("1,000*2"), 2000.0, "thousands separator");
        Check(!Calculator.TryEvaluate("1/0", out _), "divide by zero rejected");
        Check(!Calculator.TryEvaluate("1+", out _), "syntax error rejected");
        Check(Calculator.LooksLikeExpression("1+1"), "looks like expr");
        Check(!Calculator.LooksLikeExpression("2024"), "plain number is not expr");
        Check(!Calculator.LooksLikeExpression("abc"), "text is not expr");
        Check(!Calculator.LooksLikeExpression("2024-report"), "file-like name is not expr");
        Eq(Calculator.Format(2.5), "2.5", "format decimal");
        Eq(Calculator.Format(3.0), "3", "format integer");
    }

    private static void QueryParserTests()
    {
        var web = WebSearchEngine.Defaults();
        var q = QueryParser.Parse("folder:proj Readme", web);
        Eq(q.Type, TypeFilter.Folder, "folder filter");
        Check(q.Keywords.SequenceEqual(new[] { "proj", "readme" }), $"keywords lowered [{string.Join(",", q.Keywords)}]");
        q = QueryParser.Parse("ext:pdf,DOCX report", web);
        Check(q.Extensions != null && q.Extensions.SequenceEqual(new[] { ".pdf", ".docx" }), "ext list");
        Check(q.Keywords.SequenceEqual(new[] { "report" }), "ext keywords");
        q = QueryParser.Parse("g hello world", web);
        Eq(q.Web?.Keyword, "g", "web keyword");
        Eq(q.WebText, "hello world", "web text");
        Check(!q.Keywords.Contains("g"), "web keyword not a search keyword");
        q = QueryParser.Parse("g", web);
        Check(q.Web == null, "lone keyword is not web search");
        q = QueryParser.Parse("=1+2", web);
        Eq(q.Expression, "1+2", "= expression");
        q = QueryParser.Parse(@"C:\Users", web);
        Check(q.Keywords.Count == 1 && q.Keywords[0] == @"c:\users", "drive path kept as keyword");
        q = QueryParser.Parse("   ", web);
        Check(q.IsEmpty, "blank is empty");
        q = QueryParser.Parse("app:code", web);
        Eq(q.Type, TypeFilter.App, "app filter");
        Check(q.Keywords.SequenceEqual(new[] { "code" }), "app filter attached keyword");
    }

    private static ExclusionMatcher Matcher(Action<ExclusionSettings> configure)
    {
        var s = new ExclusionSettings { Rules = new(), ExcludeHidden = false, ExcludeSystem = false };
        configure(s);
        return ExclusionMatcher.Create(s);
    }

    private static void ExclusionTests()
    {
        var m = Matcher(s =>
        {
            s.Rules.Add(new ExclusionRule(ExclusionKind.Name, "node_modules", ExclusionTarget.DirectoriesOnly));
            s.Rules.Add(new ExclusionRule(ExclusionKind.Name, "*.tmp", ExclusionTarget.FilesOnly));
            s.Rules.Add(new ExclusionRule(ExclusionKind.Path, @"C:\Data\Secret", ExclusionTarget.DirectoriesOnly));
            s.Rules.Add(new ExclusionRule(ExclusionKind.Glob, @"**\build\obj", ExclusionTarget.DirectoriesOnly));
            s.Rules.Add(new ExclusionRule(ExclusionKind.Regex, @"\\cache\d+$", ExclusionTarget.DirectoriesOnly));
        });
        Check(m.IsExcludedEntry("node_modules", @"C:\p", true, 0), "name dir excluded");
        Check(m.IsExcludedEntry("NODE_MODULES", @"C:\p", true, 0), "name match case-insensitive");
        Check(!m.IsExcludedEntry("node_modules", @"C:\p", false, 0), "dir-only rule ignores file");
        Check(m.IsExcludedEntry("a.tmp", @"C:\p", false, 0), "*.tmp file excluded");
        Check(!m.IsExcludedEntry("a.tmp", @"C:\p", true, 0), "*.tmp ignores dirs");
        Check(!m.IsExcludedEntry("a.tmpx", @"C:\p", false, 0), "*.tmp anchored");
        Check(m.IsExcludedEntry("Secret", @"C:\Data", true, 0), "path rule at index time");
        Check(!m.IsExcludedEntry("Secret2", @"C:\Data", true, 0), "path rule exact");
        Check(m.IsExcludedPath(@"C:\Data\Secret\x.txt", false), "path rule subtree");
        Check(!m.IsExcludedPath(@"C:\Data\Secret2\x.txt", false), "path prefix not substring");
        Check(m.IsExcludedPath(@"D:\src\app\build\obj", true), "glob dir");
        Check(m.IsExcludedPath(@"D:\src\app\build\obj\x.dll", false), "glob subtree file");
        Check(!m.IsExcludedPath(@"D:\src\app\build\objx", true), "glob segment boundary");
        Check(m.IsExcludedEntry("obj", @"D:\src\build", true, 0), "glob at index time");
        Check(m.IsExcludedPath(@"E:\tmp\cache12", true), "regex dir");
        Check(m.IsExcludedPath(@"E:\proj\node_modules\lib\a.js", false), "name rule on ancestor");
        Check(!m.IsExcludedPath(@"E:\proj\src\a.js", false), "unrelated path kept");

        var attr = Matcher(s => { s.ExcludeSystem = true; });
        Check(attr.IsExcludedEntry("x", @"C:\", true, FileAttributes.Hidden | FileAttributes.System | FileAttributes.Directory), "hidden+system excluded");
        Check(!attr.IsExcludedEntry("x", @"C:\", true, FileAttributes.System | FileAttributes.Directory), "system-only kept");
        Check(!attr.IsExcludedEntry("x", @"C:\", true, FileAttributes.Hidden | FileAttributes.Directory), "hidden kept when only ExcludeSystem");
        var hid = Matcher(s => { s.ExcludeHidden = true; });
        Check(hid.IsExcludedEntry("x", @"C:\", false, FileAttributes.Hidden), "hidden excluded");

        // Diff + fingerprint
        var a = Matcher(s => s.Rules.Add(new ExclusionRule(ExclusionKind.Name, "foo")));
        var b = Matcher(s => { s.Rules.Add(new ExclusionRule(ExclusionKind.Name, "foo")); s.Rules.Add(new ExclusionRule(ExclusionKind.Path, @"C:\bar")); });
        var b2 = Matcher(s => { s.Rules.Add(new ExclusionRule(ExclusionKind.Path, @"C:\bar")); s.Rules.Add(new ExclusionRule(ExclusionKind.Name, "foo")); });
        Eq(b.Fingerprint, b2.Fingerprint, "fingerprint order independent");
        Check(a.Fingerprint != b.Fingerprint, "fingerprint differs");
        var d = b.Diff(a);
        Check(d.HasAdditions && !d.HasRemovals, "diff additions");
        d = a.Diff(b);
        Check(!d.HasAdditions && d.HasRemovals && !d.RemovedNonPathRules, "diff path removal");
        Check(d.RemovedPathRules.Any(p => p.Equals(@"C:\bar", StringComparison.OrdinalIgnoreCase)), $"removed path listed [{string.Join(",", d.RemovedPathRules)}]");

        // Validation
        Check(new ExclusionRule(ExclusionKind.Path, "relative\\x").Validate() != null, "relative path rejected");
        Check(new ExclusionRule(ExclusionKind.Name, "a\\b").Validate() != null, "name with separator rejected");
        Check(new ExclusionRule(ExclusionKind.Regex, "([").Validate() != null, "bad regex rejected");
        Check(ExclusionRuleIO.ParseLine(@"C:\Temp")?.Kind == ExclusionKind.Path, "IO parse path");
        Check(ExclusionRuleIO.ParseLine(@"**\obj")?.Kind == ExclusionKind.Glob, "IO parse glob");
        Check(ExclusionRuleIO.ParseLine("dist")?.Kind == ExclusionKind.Name, "IO parse name");

        // Defaults compile
        var def = ExclusionMatcher.Create(new ExclusionSettings());
        Check(def.IsExcludedPath(@"C:\proj\.git\objects", true), "default .git excluded");
        Check(def.IsExcludedPath(Environment.ExpandEnvironmentVariables(@"%SystemRoot%\WinSxS\x"), true), "default WinSxS excluded");
    }

    private static void NameMatcherTests()
    {
        double exact = NameMatcher.ScoreSubstring("readme", "readme", "readme", out _);
        double prefix = NameMatcher.ScoreSubstring("readme.md", "Readme.md", "read", out _);
        double boundary = NameMatcher.ScoreSubstring("my_readme.md", "my_readme.md", "read", out _);
        double sub = NameMatcher.ScoreSubstring("unreadable.txt", "unreadable.txt", "read", out _);
        Check(exact > prefix && prefix > boundary && boundary > sub && sub > 0, $"ranking exact {exact} > prefix {prefix} > boundary {boundary} > substring {sub}");
        Check(NameMatcher.ScoreSubstring("abc", "abc", "x", out _) < 0, "miss is negative");
        double camel = NameMatcher.ScoreSubstring("myreadme", "MyReadme", "read", out int pos);
        Check(camel >= NameMatcher.WordBoundary - 10 && pos == 2, "camel-case boundary");
        Eq(AppCatalog.BuildAcronym("Visual Studio Code"), "vsc", "acronym");
        Eq(AppCatalog.BuildAcronym("PowerShell_ISE"), "psi", "acronym camel/underscore");
        var hl = new List<(int, int)>();
        Check(NameMatcher.ScoreItem("Visual Studio Code", "visual studio code", "visual studio code", null, "vsc", new[] { "vsc" }, hl) > 0, "app acronym match");
        hl.Clear();
        Check(NameMatcher.ScoreFuzzy("photoshop", "pshp", hl) > 0, "fuzzy subsequence");
        Check(NameMatcher.ScoreFuzzy("photoshop", "zz", null) < 0, "fuzzy miss");
    }

    private static void PinyinTests()
    {
        Eq(PinyinHelper.GetInitials("报告"), "bg", "报告 → bg");
        Eq(PinyinHelper.GetInitials("中文abc"), "zwabc", "mixed");
        Eq(PinyinHelper.GetInitials("项目计划书.docx"), "xmjhs.docx", "with extension");
        Check(PinyinHelper.ContainsCjk("a报"), "contains cjk");
        Check(!PinyinHelper.ContainsCjk("abc"), "no cjk");
        Check(PinyinHelper.IsPinyinQuery("bg"), "pinyin query");
        Check(!PinyinHelper.IsPinyinQuery("b.g"), "punctuation not pinyin query");
    }

    private static void TopNTests()
    {
        var t = new TopN(3);
        foreach (var (s, i) in new[] { (5.0, 1), (1.0, 2), (9.0, 3), (7.0, 4), (3.0, 5) }) t.Push(s, i, 0);
        var got = Enumerable.Range(0, t.Count).Select(i => t[i].Score).OrderByDescending(x => x).ToArray();
        Check(got.SequenceEqual(new[] { 9.0, 7.0, 5.0 }), $"top3 [{string.Join(",", got)}]");
        Eq(t.Pushed, 5L, "pushed count");
    }

    // ------------------------------------------------------------------ integration

    private static string MakeTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "cl-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "projects", "alpha", "docs"));
        Directory.CreateDirectory(Path.Combine(root, "projects", "beta", "node_modules", "lib"));
        Directory.CreateDirectory(Path.Combine(root, "photos", "2023"));
        File.WriteAllText(Path.Combine(root, "projects", "alpha", "docs", "Readme.md"), "x");
        File.WriteAllText(Path.Combine(root, "projects", "alpha", "main.cs"), "x");
        File.WriteAllText(Path.Combine(root, "projects", "beta", "node_modules", "lib", "readme.js"), "x");
        File.WriteAllText(Path.Combine(root, "projects", "beta", "notes.tmp"), "x");
        File.WriteAllText(Path.Combine(root, "photos", "2023", "beach.jpg"), "x");
        File.WriteAllText(Path.Combine(root, "年度报告.docx"), "x");
        return root;
    }

    private static (FileIndex Index, VolumeIndex Volume, ExclusionMatcher Matcher) CrawlTree(string root, ExclusionMatcher? m = null)
    {
        m ??= ExclusionMatcher.Create(new ExclusionSettings()); // defaults: node_modules, *.tmp …
        var v = new DirectoryCrawler(m).Crawl(root, null, CancellationToken.None);
        var fi = new FileIndex();
        fi.SetVolume(v);
        return (fi, v, m);
    }

    private static IReadOnlyList<SearchResult> Find(FileIndex fi, string query, ExclusionMatcher? m = null)
    {
        var settings = new AppSettings();
        var hist = HistoryStore.Load(Path.Combine(Path.GetTempPath(), "cl-selftest-history-" + Guid.NewGuid().ToString("N") + ".json"));
        var engine = new SearchEngine(new ISearchProvider[] { new IndexSearchProvider(fi, () => settings.Search) }, hist,
            () => m ?? ExclusionMatcher.Empty, () => settings);
        return engine.Search(query, CancellationToken.None);
    }

    private static bool Has(IReadOnlyList<SearchResult> r, string fileName) =>
        r.Any(x => string.Equals(Path.GetFileName(x.Path), fileName, StringComparison.OrdinalIgnoreCase));

    private static void IndexEndToEnd()
    {
        var root = MakeTree();
        try
        {
            var (fi, v, m) = CrawlTree(root);
            var all = Enumerable.Range(1, v.Count - 1).Where(i => !v.IsDeleted(i)).Select(v.GetFullPath).ToList();
            Check(all.Any(p => p.EndsWith(@"alpha\docs\Readme.md", StringComparison.Ordinal)), "full path reconstruction");
            Check(!all.Any(p => p.Contains("node_modules")), "node_modules pruned at crawl");
            Check(!all.Any(p => p.EndsWith("notes.tmp")), "*.tmp pruned at crawl");
            Eq(v.LiveCount, 10, $"live entry count ({string.Join(" | ", all.Select(p => p[root.Length..]))})");

            var r = Find(fi, "readme");
            Check(Has(r, "Readme.md"), "search readme");
            Check(!Has(r, "readme.js"), "excluded not found");
            Check(r.Count > 0 && Path.GetFileName(r[0].Path) == "Readme.md", "best hit first");
            Check(Has(Find(fi, "alpha readme"), "Readme.md"), "path keyword + name anchor");
            Check(!Has(Find(fi, "beta readme"), "Readme.md"), "path keyword filters");
            Check(Has(Find(fi, "ndbg"), "年度报告.docx"), "pinyin initials search");
            Check(Has(Find(fi, "报告"), "年度报告.docx"), "CJK substring search");
            Check(Find(fi, "folder:2023").All(x => x.Kind == ResultKind.Folder) && Has(Find(fi, "folder:2023"), "2023"), "folder filter");
            Check(Has(Find(fi, "ext:jpg b"), "beach.jpg") && !Has(Find(fi, "ext:md b"), "beach.jpg"), "ext filter");
            Check(Find(fi, "zzzqqq").Count == 0, "no results");
            // incremental narrowing: "rea" then "readm"
            var idxProv = new IndexSearchProvider(fi, () => new SearchSettings { Pinyin = false });
            var q1 = QueryParser.Parse("rea", null);
            idxProv.Search(q1, 50, CancellationToken.None);
            var r2 = idxProv.Search(QueryParser.Parse("readm", null), 50, CancellationToken.None);
            Check(r2.Any(x => x.Title == "Readme.md"), "incremental narrowing");

            // Live updates (FSW-style path changes)
            var newFile = Path.Combine(root, "projects", "alpha", "Design-Spec.pdf");
            File.WriteAllText(newFile, "x");
            Apply(v, m, new IndexChange(ChangeKind.Created, 0, 0, null, 0, newFile, null));
            Check(Has(Find(fi, "design"), "Design-Spec.pdf"), "created file found");
            var renamed = Path.Combine(root, "projects", "alpha", "Design-Specification-Final.pdf");
            File.Move(newFile, renamed);
            Apply(v, m, new IndexChange(ChangeKind.Renamed, 0, 0, null, 0, renamed, newFile));
            Check(Has(Find(fi, "final"), "Design-Specification-Final.pdf") && !Has(Find(fi, "design"), "Design-Spec.pdf"), "rename (longer name) applied");
            File.Delete(renamed);
            Apply(v, m, new IndexChange(ChangeKind.Deleted, 0, 0, null, 0, renamed, null));
            Check(!Has(Find(fi, "final"), "Design-Specification-Final.pdf"), "delete applied");
            // Directory moved in with content
            var srcDir = Path.Combine(Path.GetTempPath(), "cl-move-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(Path.Combine(srcDir, "inner"));
            File.WriteAllText(Path.Combine(srcDir, "inner", "moved-in.txt"), "x");
            var dst = Path.Combine(root, "incoming");
            Directory.Move(srcDir, dst);
            Apply(v, m, new IndexChange(ChangeKind.Created, 0, 0, null, 0, dst, null));
            Check(Has(Find(fi, "moved-in"), "moved-in.txt"), "moved-in directory content indexed");
            // Rename directory → children paths follow
            var dst2 = Path.Combine(root, "incoming-renamed-folder");
            Directory.Move(dst, dst2);
            Apply(v, m, new IndexChange(ChangeKind.Renamed, 0, 0, null, 0, dst2, dst));
            var mv = Find(fi, "moved-in");
            Check(mv.Any(x => x.Path!.Contains("incoming-renamed-folder")), $"children follow dir rename [{string.Join(",", mv.Select(x => x.Path))}]");

            // Launcher "Delete" action: drop a file / a folder subtree from the index right away
            var mainCs = Path.Combine(root, "projects", "alpha", "main.cs");
            Check(Has(Find(fi, "main"), "main.cs"), "file present before RemovePath");
            Eq(fi.RemovePath(mainCs), 1, "RemovePath(file) removed 1");
            Check(!Has(Find(fi, "main"), "main.cs"), "removed file no longer found");
            Eq(fi.RemovePath(mainCs), 0, "RemovePath again is a no-op");
            Eq(fi.RemovePath(Path.Combine(root, "photos")), 3, "RemovePath(folder) removes subtree");
            Check(!Has(Find(fi, "beach"), "beach.jpg") && !Has(Find(fi, "2023"), "2023"), "folder subtree no longer found");
            Check(Has(Find(fi, "readme"), "Readme.md"), "siblings untouched");
            Eq(fi.RemovePath(@"Z:\not-indexed\x.txt"), 0, "RemovePath outside roots is a no-op");

            // Persistence round trip
            var file = Path.Combine(Path.GetTempPath(), "cl-selftest-" + Guid.NewGuid().ToString("N") + ".bin");
            IndexPersistence.Save(file, m.Fingerprint, new[] { v });
            var loaded = IndexPersistence.Load(file);
            Check(loaded != null && loaded.Volumes.Count == 1, "persistence load");
            if (loaded != null)
            {
                Eq(loaded.Fingerprint, m.Fingerprint, "fingerprint persisted");
                var lv = loaded.Volumes[0];
                Eq(lv.LiveCount, v.LiveCount, "live count after reload");
                var lfi = new FileIndex();
                lfi.SetVolume(lv);
                Check(Has(Find(lfi, "readme"), "Readme.md") && Has(Find(lfi, "ndbg"), "年度报告.docx"), "search after reload (lower + pinyin rebuilt)");
                Check(Has(Find(lfi, "moved-in"), "moved-in.txt"), "appended entries persisted");
            }
            File.Delete(file);

            // Compaction keeps content
            var compact = v.Compact();
            var cfi = new FileIndex();
            cfi.SetVolume(compact);
            Eq(compact.LiveCount, v.LiveCount, "compaction preserves live count");
            Check(Has(Find(cfi, "moved-in"), "moved-in.txt") && Has(Find(cfi, "readme"), "Readme.md"), "search after compaction");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void Apply(VolumeIndex v, ExclusionMatcher m, params IndexChange[] changes)
    {
        v.Lock.EnterWriteLock();
        try { PathChangeApplier.Apply(v, changes, m); }
        finally { v.Lock.ExitWriteLock(); }
    }

    private static void PruneTests()
    {
        var root = MakeTree();
        try
        {
            var (fi, v, m) = CrawlTree(root);
            Check(Has(Find(fi, "beach"), "beach.jpg"), "before prune");
            var m2 = ExclusionMatcher.Create(new ExclusionSettings
            {
                Rules = DefaultExclusions.Create().Append(new ExclusionRule(ExclusionKind.Name, "photos", ExclusionTarget.DirectoriesOnly)).ToList(),
            });
            v.Lock.EnterWriteLock();
            int removed;
            try { removed = v.Prune(m2); } finally { v.Lock.ExitWriteLock(); }
            Eq(removed, 3, "prune removed photos subtree (photos, 2023, beach.jpg)");
            Check(!Has(Find(fi, "beach"), "beach.jpg"), "pruned entries not searchable");
            // Query-time filter alone (before re-index) hides excluded paths too
            var (fi2, _, _) = CrawlTree(root);
            var photosOnly = ExclusionMatcher.Create(new ExclusionSettings
            {
                Rules = new() { new ExclusionRule(ExclusionKind.Name, "photos", ExclusionTarget.DirectoriesOnly) }, ExcludeSystem = false,
            });
            Check(!Has(Find(fi2, "beach", photosOnly), "beach.jpg"), "search-time exclusion filter");
            Check(Has(Find(fi2, "readme", photosOnly), "Readme.md"), "search-time filter keeps others");
            // Path rule
            var m3 = ExclusionMatcher.Create(new ExclusionSettings
            {
                Rules = new() { new ExclusionRule(ExclusionKind.Path, Path.Combine(root, "projects"), ExclusionTarget.DirectoriesOnly) },
            });
            var (fi3, v3, _) = CrawlTree(root, m3);
            Check(!Enumerable.Range(1, v3.Count - 1).Select(v3.GetFullPath).Any(p => p.Contains(@"\projects")), "path rule prunes at crawl");
            Check(Has(Find(fi3, "beach"), "beach.jpg"), "other content kept");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void HistoryTests()
    {
        var file = Path.Combine(Path.GetTempPath(), "cl-hist-" + Guid.NewGuid().ToString("N") + ".json");
        var h = HistoryStore.Load(file);
        h.RecordLaunch(@"C:\a\x.txt", UsageKind.File, "xt");
        h.RecordLaunch(@"C:\a\x.txt", UsageKind.File, "xt");
        Check(h.GetBoost(@"C:\a\x.txt", "xt") > h.GetBoost(@"C:\a\x.txt", "zz"), "query association boost");
        Check(h.GetBoost(@"C:\nope", "") == 0, "no history no boost");
        h.RecordFolderVisit(Path.GetTempPath(), FolderVisitSource.Explorer);
        h.RecordFolderVisit(Environment.GetFolderPath(Environment.SpecialFolder.Windows), FolderVisitSource.Dialog);
        h.RecordFolderVisit(Path.GetTempPath(), FolderVisitSource.Explorer);
        var rec = h.GetRecentFolders(5);
        Check(rec.Count == 2 && Core.Util.PathUtil.Equal(rec[0].Path, Path.GetTempPath()), "recent folders dedupe + MRU");
        h.SaveNow();
        var h2 = HistoryStore.Load(file);
        Check(h2.HasUsage(@"C:\a\x.txt") && h2.GetRecentFolders(5).Count == 2, "history persisted");
        File.Delete(file);
    }

    private static void OnboardingTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hk-onboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string Sub(string name) { var d = Path.Combine(dir, name); Directory.CreateDirectory(d); return Path.Combine(d, "settings.json"); }
            const int V = OnboardingState.CurrentVersion;

            // Brand-new user: empty folder.
            var fresh = SettingsStore.Load(Sub("fresh"));
            Check(fresh.IsFirstRun, "empty folder = first run");
            Eq(fresh.Current.Onboarding!.SeenVersion, 0, "new user has not seen the guide");
            Check(OnboardingPolicy.ShouldAutoShow(fresh.Current.Onboarding, false, false, false), "auto-show for new user on manual start");
            Check(!OnboardingPolicy.ShouldAutoShow(fresh.Current.Onboarding!, autostart: true, false, false), "never auto-show at login");
            Check(!OnboardingPolicy.ShouldAutoShow(fresh.Current.Onboarding!, false, elevatedRestart: true, false), "never auto-show on elevated restart");
            Eq(fresh.Current.General.Language, OnboardingPolicy.DefaultLanguage(System.Globalization.CultureInfo.CurrentUICulture), "new user language follows Windows");
            Eq(OnboardingPolicy.DefaultLanguage(new System.Globalization.CultureInfo("zh-TW")), "zh-CN", "zh-TW → Chinese UI");
            Eq(OnboardingPolicy.DefaultLanguage(new System.Globalization.CultureInfo("de-DE")), "en", "de-DE → English UI");

            // Seen is persisted.
            fresh.Update(s => s.Onboarding!.SeenVersion = V);
            var reloaded = SettingsStore.Load(Path.Combine(dir, "fresh", "settings.json"));
            Check(!reloaded.IsFirstRun && reloaded.Current.Onboarding!.SeenVersion == V, "seen persisted, not first run any more");
            Check(!OnboardingPolicy.ShouldAutoShow(reloaded.Current.Onboarding!, false, false, false), "no auto-show once seen");
            Check(OnboardingPolicy.ShouldAutoShow(reloaded.Current.Onboarding!, false, false, forced: true), "--onboarding forces it");

            // Upgrading user: settings.json from before onboarding existed.
            var oldPath = Sub("old");
            File.WriteAllText(oldPath, "{ \"general\": { \"language\": \"en\" } }");
            var old = SettingsStore.Load(oldPath);
            Check(!old.IsFirstRun, "old settings = not first run");
            Check(old.Current.Onboarding!.SeenVersion == V && old.Current.Onboarding.TrayBalloonShown, "old settings = existing user, no guide/balloon");
            Eq(old.Current.General.Language, "en", "existing language untouched");
            Check(File.ReadAllText(oldPath).Contains("\"onboarding\""), "onboarding state written back");

            // Only history.json (user deleted settings to reset): not a newcomer.
            var histOnly = Sub("hist");
            File.WriteAllText(Path.Combine(dir, "hist", "history.json"), "{}");
            var h = SettingsStore.Load(histOnly);
            Check(!h.IsFirstRun && h.Current.Onboarding!.SeenVersion == V, "history only = existing user");

            // Corrupt settings: not a newcomer either.
            var badPath = Sub("bad");
            File.WriteAllText(badPath, "not json {");
            Eq(SettingsStore.Load(badPath).Current.Onboarding!.SeenVersion, V, "corrupt settings = existing user");

            // Importing someone's settings keeps this machine's guide state.
            var other = new AppSettings { Onboarding = OnboardingState.ForNewUser() };
            reloaded.ReplaceAll(other);
            Eq(reloaded.Current.Onboarding!.SeenVersion, V, "import keeps local onboarding state");

            // Keycaps.
            Check(OnboardingPolicy.Keycaps("DoubleCtrl").SequenceEqual(new[] { "Ctrl", "Ctrl" }), "keycaps DoubleCtrl");
            Check(OnboardingPolicy.Keycaps("Ctrl+Alt+Space").SequenceEqual(new[] { "Ctrl", "Alt", "Space" }), "keycaps combo");
            Check(OnboardingPolicy.Keycaps("").Count == 0 && OnboardingPolicy.Keycaps(null).Count == 0, "keycaps empty");

            // Tray promotion matching.
            const string self = @"C:\Apps\Hopkey\Hopkey.exe";
            Eq(OnboardingPolicy.EvaluatePromotion(new[] { (self, true) }, self), TrayPromotion.Promoted, "full path promoted");
            Eq(OnboardingPolicy.EvaluatePromotion(new[] { (self, false) }, self), TrayPromotion.Hidden, "full path hidden");
            Eq(OnboardingPolicy.EvaluatePromotion(new[] { (@"c:\apps\hopkey\HOPKEY.EXE", true) }, self), TrayPromotion.Promoted, "case-insensitive path");
            Eq(OnboardingPolicy.EvaluatePromotion(new[] { (@"{X}\Hopkey\Hopkey.exe", false), (@"C:\x.exe", true) }, self), TrayPromotion.Hidden, "unique file-name fallback");
            Eq(OnboardingPolicy.EvaluatePromotion(new[] { (@"D:\a\Hopkey.exe", true), (@"D:\b\Hopkey.exe", false) }, self), TrayPromotion.Unknown, "ambiguous file name = unknown");
            Eq(OnboardingPolicy.EvaluatePromotion(Array.Empty<(string, bool)>(), self), TrayPromotion.Unknown, "no entries = unknown");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void UserDataTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hk-userdata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Source machine: customized settings + usage preferences.
            var srcSettings = SettingsStore.Load(Path.Combine(dir, "src-settings.json"));
            srcSettings.Update(s =>
            {
                s.General.Language = "en";
                s.General.Theme = ThemeMode.Dark;
                s.Hotkeys.Launcher = "Alt+Space";
                s.Search.MaxResults = 77;
                s.Search.SortBy = ResultSortMode.ModifiedNewest;
                s.QuickSwitch.DisabledProcesses.Add("foo.exe");
                s.Favorites.Add(new FavoriteItem(@"C:\proj", "proj"));
                s.Exclusion.Rules.Add(new ExclusionRule(ExclusionKind.Name, "my_cache"));
                s.WebSearch.Add(new WebSearchEngine("dd", "DDG", "https://duckduckgo.com/?q={q}"));
            });
            var srcHistory = HistoryStore.Load(Path.Combine(dir, "src-history.json"));
            srcHistory.RecordLaunch(@"C:\proj\a.txt", UsageKind.File, "ax");
            srcHistory.RecordLaunch(@"C:\proj\a.txt", UsageKind.File, "ax");
            srcHistory.RecordFolderVisit(Path.GetTempPath(), FolderVisitSource.Explorer);
            srcHistory.RecordFacet("ext:pdf");
            var export = Path.Combine(dir, "out" + UserDataService.FileExtension);
            new UserDataService(srcSettings, srcHistory).Export(export, "9.9.9");
            Check(File.Exists(export), "export file written");

            var pkg = UserDataService.Read(export);
            Eq(pkg.AppVersion, "9.9.9", "package app version");
            Check(pkg.Settings != null && pkg.History != null, "package has settings + history");

            // Target machine: defaults plus some unrelated history that must be replaced.
            var dstSettingsFile = Path.Combine(dir, "dst-settings.json");
            var dstHistoryFile = Path.Combine(dir, "dst-history.json");
            var dstSettings = SettingsStore.Load(dstSettingsFile);
            var dstHistory = HistoryStore.Load(dstHistoryFile);
            dstHistory.RecordLaunch(@"C:\old\b.txt", UsageKind.File, "b");
            int changed = 0;
            dstSettings.Changed += (_, _) => changed++;
            var backupDir = Path.Combine(dir, "backups");
            var backup = new UserDataService(dstSettings, dstHistory).Import(pkg, "1.0.0", backupDir);

            var s2 = dstSettings.Current;
            Eq(s2.General.Language, "en", "imported language");
            Eq(s2.General.Theme, ThemeMode.Dark, "imported theme");
            Eq(s2.Hotkeys.Launcher, "Alt+Space", "imported hotkey");
            Eq(s2.Search.MaxResults, 77, "imported max results");
            Eq(s2.Search.SortBy, ResultSortMode.ModifiedNewest, "imported sort");
            Check(s2.QuickSwitch.DisabledProcesses.Contains("foo.exe"), "imported quick switch list");
            Check(s2.Favorites.Exists(f => f.Path == @"C:\proj" && f.Alias == "proj"), "imported favorite");
            Check(s2.Exclusion.Rules.Exists(r => r.Pattern == "my_cache"), "imported exclusion rule");
            Check(s2.WebSearch.Exists(w => w.Keyword == "dd"), "imported web search");
            Check(changed == 1, "settings Changed raised once");
            Check(dstHistory.HasUsage(@"C:\proj\a.txt") && !dstHistory.HasUsage(@"C:\old\b.txt"), "history replaced");
            Check(dstHistory.GetBoost(@"C:\proj\a.txt", "ax") > dstHistory.GetBoost(@"C:\proj\a.txt", "zz"), "query association imported");
            Eq(dstHistory.GetFacetCount("ext:pdf"), 1, "facet usage imported");
            Eq(dstHistory.GetRecentFolders(5, onlyExisting: false).Count, 1, "recent folders imported");

            // Persisted to disk, not just in memory.
            Check(SettingsStore.Load(dstSettingsFile).Current.Search.MaxResults == 77, "imported settings saved");
            Check(HistoryStore.Load(dstHistoryFile).HasUsage(@"C:\proj\a.txt"), "imported history saved");

            // Backup of the previous state can itself be imported back.
            Check(backup != null && File.Exists(backup), "backup written before import");
            var old = UserDataService.Read(backup!);
            Check(old.History!.Usage.Exists(u => u.Key == @"C:\old\b.txt") && old.Settings!.General.Language == OnboardingPolicy.DefaultLanguage(System.Globalization.CultureInfo.CurrentUICulture), "backup holds previous data");

            // Settings-only package leaves history alone.
            var partial = Path.Combine(dir, "partial.json");
            File.WriteAllText(partial, "{ \"format\": \"hopkey-userdata\", \"version\": 1, \"settings\": { \"search\": { \"maxResults\": 9999 } } }");
            var p2 = UserDataService.Read(partial);
            Check(p2.History == null && p2.Settings!.General != null && p2.Settings.Search.MaxResults == 50, "partial package normalized");
            new UserDataService(dstSettings, dstHistory).Import(p2, null, backupDir);
            Check(dstHistory.HasUsage(@"C:\proj\a.txt"), "settings-only import keeps history");

            // Rejections.
            static bool Rejects(string path, string json)
            {
                File.WriteAllText(path, json);
                try { UserDataService.Read(path); return false; }
                catch (InvalidDataException) { return true; }
            }
            var bad = Path.Combine(dir, "bad.json");
            Check(Rejects(bad, "{ \"general\": {} }"), "plain settings.json rejected");
            Check(Rejects(bad, "not json"), "garbage rejected");
            Check(Rejects(bad, "{ \"format\": \"hopkey-userdata\", \"version\": 99, \"settings\": {} }"), "newer format rejected");
            Check(Rejects(bad, "{ \"format\": \"hopkey-userdata\", \"version\": 1 }"), "empty package rejected");

            srcHistory.Dispose();
            dstHistory.Dispose();
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ------------------------------------------------------------------ performance

    private static readonly string[] s_words =
    {
        "report", "project", "readme", "config", "setup", "invoice", "photo", "design", "final", "draft", "notes", "budget",
        "meeting", "summary", "backup", "data", "index", "main", "test", "client", "server", "build", "release", "plan",
        "music", "video", "game", "archive", "contract", "resume", "schedule", "spec", "api", "model", "view", "util",
        "image", "logo", "icon", "theme", "style", "script", "module", "package", "library", "manual", "guide", "tutorial",
    };
    private static readonly string[] s_exts = { ".txt", ".pdf", ".docx", ".xlsx", ".jpg", ".png", ".cs", ".js", ".json", ".md", ".zip", ".mp4", ".dll", ".exe" };
    private static readonly string[] s_cjk = { "报告", "项目", "文档", "照片", "音乐", "设计", "合同", "会议", "预算", "笔记" };

    private static VolumeIndex BuildSynthetic(int n, out double ms, out long bytes)
    {
        var rnd = new Random(42);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetTotalMemory(true);
        var sw = Stopwatch.StartNew();
        var v = new VolumeIndex(@"X:\", IndexSourceKind.Usn, n + 16, n * 22);
        v.AppendRawNoIndex(-1, "X:", IndexEntryFlags.Directory, 5);
        int dirs = Math.Max(1, n / 20);
        Span<char> buf = stackalloc char[96];
        for (int i = 1; i < n; i++)
        {
            bool isDir = i <= dirs;
            int parent = i <= 1 ? 0 : (isDir ? rnd.Next(0, i) : rnd.Next(1, dirs + 1));
            int len = 0;
            int words = rnd.Next(1, 4);
            for (int w = 0; w < words; w++)
            {
                string word = rnd.Next(100) < 5 ? s_cjk[rnd.Next(s_cjk.Length)] : s_words[rnd.Next(s_words.Length)];
                if (w > 0) buf[len++] = rnd.Next(2) == 0 ? '_' : '-';
                if (w > 0 && rnd.Next(3) == 0 && char.IsAsciiLetter(word[0])) { buf[len++] = char.ToUpperInvariant(word[0]); word.AsSpan(1).CopyTo(buf[len..]); len += word.Length - 1; }
                else { word.AsSpan().CopyTo(buf[len..]); len += word.Length; }
            }
            if (rnd.Next(2) == 0) { var num = rnd.Next(1, 3000).ToString(); num.AsSpan().CopyTo(buf[len..]); len += num.Length; }
            if (!isDir) { var e = s_exts[rnd.Next(s_exts.Length)]; e.AsSpan().CopyTo(buf[len..]); len += e.Length; }
            v.AppendRawNoIndex(parent, buf[..len], isDir ? IndexEntryFlags.Directory : IndexEntryFlags.None, 100UL + (ulong)i);
        }
        v.BaseCount = v.CountInternal;
        v.BuildPinyin();
        ms = sw.Elapsed.TotalMilliseconds;
        bytes = GC.GetTotalMemory(true) - before;
        return v;
    }

    private static (double P50, double P95, double Max, int Results) Measure(SearchEngine engine, string q, int runs = 40)
    {
        for (int i = 0; i < 3; i++) engine.Search(q, CancellationToken.None);
        var times = new double[runs];
        int count = 0;
        for (int i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            count = engine.Search(q, CancellationToken.None).Count;
            times[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(times);
        return (times[runs / 2], times[(int)(runs * 0.95) - 1], times[^1], count);
    }

    private static void PerfBenchmark(int n)
    {
        Console.WriteLine($"  machine: {Environment.ProcessorCount} logical cores, {(System.Runtime.Intrinsics.X86.Avx2.IsSupported ? "AVX2" : "no AVX2")}{(System.Runtime.Intrinsics.X86.Avx512F.IsSupported ? "+AVX512" : "")}");
        var v = BuildSynthetic(n, out double buildMs, out long bytes);
        Console.WriteLine($"  synthetic build: {buildMs:F0} ms, managed memory ≈ {bytes / 1024.0 / 1024.0:F1} MB ({(double)bytes / n:F1} B/entry), arena {v.ArenaLength:N0} chars, pinyin entries {v.Pinyin?.Count:N0}");
        // PLAN §9.1: 2M entries < 280MB working set including runtime → ~140 B/entry budget for the index itself.
        Perf("memory per entry", (double)bytes / n, 140, "B");

        var fi = new FileIndex();
        fi.SetVolume(v);
        var settings = new AppSettings();
        var hist = HistoryStore.Load(Path.Combine(Path.GetTempPath(), "cl-perf-hist-" + Guid.NewGuid().ToString("N") + ".json"));
        var engine = new SearchEngine(new ISearchProvider[] { new IndexSearchProvider(fi, () => settings.Search) }, hist, () => ExclusionMatcher.Empty, () => settings);

        // Phase breakdown (diagnostics)
        foreach (var q in new[] { "readme", "config json", "a" })
        {
            var filter = new ScanFilter { Type = TypeFilter.Any, Pinyin = true };
            var anchor = QueryParser.Parse(q, null).Keywords[^1];
            double scanMs = 0, scanNoPyMs = 0;
            int cands = 0;
            for (int i = 0; i < 23; i++)
            {
                var sw0 = Stopwatch.StartNew();
                var c = ArenaScanner.Scan(fi.Volumes, anchor, filter, 512, CancellationToken.None, out _);
                if (i >= 3) scanMs += sw0.Elapsed.TotalMilliseconds;
                cands = c.Count;
                sw0.Restart();
                ArenaScanner.Scan(fi.Volumes, anchor, new ScanFilter { Type = TypeFilter.Any, Pinyin = false }, 512, CancellationToken.None, out _);
                if (i >= 3) scanNoPyMs += sw0.Elapsed.TotalMilliseconds;
            }
            var prov = new IndexSearchProvider(fi, () => settings.Search);
            var pq = QueryParser.Parse(q, null);
            double provMs = 0;
            for (int i = 0; i < 23; i++)
            {
                var sw0 = Stopwatch.StartNew();
                prov.Search(QueryParser.Parse("zz" + i, null), 50, CancellationToken.None); // reset narrow cache
                sw0.Restart();
                prov.Search(pq, 50, CancellationToken.None);
                if (i >= 3) provMs += sw0.Elapsed.TotalMilliseconds;
            }
            Console.WriteLine($"  profile {q,-12} scan {scanMs / 20:F2} ms (no pinyin {scanNoPyMs / 20:F2}) candidates {cands}, provider total {provMs / 20:F2} ms");
        }

        double worstP95 = 0;
        foreach (var q in new[] { "readme", "report final", "config json", "invoice2023", "bg", "xyzzy-no-hit", "a", "photo 12" })
        {
            var (p50, p95, max, count) = Measure(engine, q);
            Console.WriteLine($"  query {q,-16} p50 {p50,7:F2} ms  p95 {p95,7:F2} ms  max {max,7:F2} ms  results {count}");
            if (q != "a") worstP95 = Math.Max(worstP95, p95);
        }
        double scale = n / 2_000_000.0;
        Perf("search p95 (worst query, excl. 1-char)", worstP95, Math.Max(10, 10 * scale));
        var (ap50, ap95, _, _) = Measure(engine, "a", 20);
        Perf("search p95 single char 'a'", ap95, Math.Max(25, 25 * scale));

        // Persistence
        var file = Path.Combine(Path.GetTempPath(), "cl-perf-" + Guid.NewGuid().ToString("N") + ".bin");
        var sw = Stopwatch.StartNew();
        IndexPersistence.Save(file, "fp", new[] { v });
        double saveMs = sw.Elapsed.TotalMilliseconds;
        long size = new FileInfo(file).Length;
        sw.Restart();
        var loaded = IndexPersistence.Load(file);
        double loadMs = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"  index.bin {size / 1024.0 / 1024.0:F1} MB, save {saveMs:F0} ms, load {loadMs:F0} ms");
        Check(loaded != null && loaded.Volumes[0].LiveCount == v.LiveCount, "perf: reload count matches");
        Perf("cache load to searchable", loadMs, Math.Max(1000, 1000 * scale));
        File.Delete(file);

        // Prune cost (exclusion addition)
        var m = ExclusionMatcher.Create(new ExclusionSettings { Rules = new() { new ExclusionRule(ExclusionKind.Name, "backup", ExclusionTarget.DirectoriesOnly) } });
        sw.Restart();
        v.Lock.EnterWriteLock();
        int removed;
        try { removed = v.Prune(m); } finally { v.Lock.ExitWriteLock(); }
        double pruneMs = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"  prune 'backup' dirs: removed {removed:N0} in {pruneMs:F0} ms");
        Perf("exclusion prune (add rule)", pruneMs, Math.Max(1500, 1500 * scale));
    }

    private static void CrawlBenchmark(string dir)
    {
        var m = ExclusionMatcher.Create(new ExclusionSettings());
        var sw = Stopwatch.StartNew();
        var v = new DirectoryCrawler(m).Crawl(dir, null, CancellationToken.None);
        Console.WriteLine($"  crawled {v.LiveCount:N0} entries in {sw.ElapsedMilliseconds:N0} ms ({v.LiveCount / Math.Max(0.001, sw.Elapsed.TotalSeconds):N0}/s)");
        if (ElevationHelper.IsSelfElevated && dir.Length <= 3 && UsnIndexSource.IsSupported(dir))
        {
            foreach (var method in new[] { null, "layout", "mft", "record", "enum" })
            {
                UsnIndexSource.TestMethod = method;
                var src = new UsnIndexSource(dir);
                sw.Restart();
                VolumeIndex u;
                try { u = src.BuildAsync(m, null, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (Exception ex) { Console.WriteLine($"  USN build ({method}) failed: {ex.Message}"); continue; }
                Console.WriteLine($"  USN/MFT build [{src.LastMethod}] {u.LiveCount:N0} entries in {sw.ElapsedMilliseconds:N0} ms{(src.LastFallbackReason != null ? " (fallback: " + src.LastFallbackReason + ")" : "")}");
                if (method == null) Perf("MFT build per 1M entries", sw.Elapsed.TotalMilliseconds / Math.Max(1, u.LiveCount / 1_000_000.0), 3000);
                DiffIndexes(v, u);
            }
            UsnIndexSource.TestMethod = null;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newLink, string existing, IntPtr security);

    /// <summary>
    /// USN applier with hard links: synthetic journal records (key = FRN) against real files and links in a temp
    /// folder, since the applier asks the disk whether a reported name was added or removed.
    /// </summary>
    private static void HardLinkTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cl-hardlink-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "a"));
        Directory.CreateDirectory(Path.Combine(dir, "b"));
        try
        {
            var m = ExclusionMatcher.Create(new ExclusionSettings { Rules = new(), ExcludeHidden = false, ExcludeSystem = false });
            var v = VolumeIndex.CreateEmpty(dir, IndexSourceKind.Usn, 1);
            const ulong A = 10, B = 11, F = 0x0003_0000_0000_0064; // file: sequence 3, record 100
            var dirAttr = FileAttributes.Directory;
            void Apply(params IndexChange[] cs) { v.Lock.EnterWriteLock(); try { UsnChangeApplier.Apply(v, cs, m); } finally { v.Lock.ExitWriteLock(); } }
            List<string> Names()
            {
                var r = new List<string>();
                for (int i = 1; i < v.Count; i++) if (!v.IsDeleted(i) && !v.IsDirectory(i)) r.Add(Path.GetRelativePath(dir, v.GetFullPath(i)));
                r.Sort(StringComparer.OrdinalIgnoreCase);
                return r;
            }
            Apply(new IndexChange(ChangeKind.Created, A, 1, "a", dirAttr, null, null),
                  new IndexChange(ChangeKind.Created, B, 1, "b", dirAttr, null, null));

            File.WriteAllText(Path.Combine(dir, "a", "f.txt"), "x");
            Apply(new IndexChange(ChangeKind.Created, F, A, "f.txt", FileAttributes.Archive, null, null));
            Check(Names().SequenceEqual(new[] { @"a\f.txt" }), "plain create");

            Check(CreateHardLink(Path.Combine(dir, "b", "g.txt"), Path.Combine(dir, "a", "f.txt"), IntPtr.Zero), "CreateHardLink");
            Apply(new IndexChange(ChangeKind.HardLinkChanged, F, B, "g.txt", FileAttributes.Archive, null, null));
            Check(Names().SequenceEqual(new[] { @"a\f.txt", @"b\g.txt" }), "link added → second entry");
            Apply(new IndexChange(ChangeKind.HardLinkChanged, F, B, "g.txt", FileAttributes.Archive, null, null));
            Check(Names().Count == 2, "repeated link record is idempotent");

            File.Move(Path.Combine(dir, "b", "g.txt"), Path.Combine(dir, "b", "h.txt"));
            Apply(new IndexChange(ChangeKind.Renamed, F, B, "h.txt", FileAttributes.Archive, null, null));
            Check(Names().SequenceEqual(new[] { @"a\f.txt", @"b\h.txt" }), "rename of the second link renames that link only");

            File.Move(Path.Combine(dir, "a", "f.txt"), Path.Combine(dir, "a", "f2.txt"));
            Apply(new IndexChange(ChangeKind.Renamed, F, A, "f2.txt", FileAttributes.Archive, null, null));
            Check(Names().SequenceEqual(new[] { @"a\f2.txt", @"b\h.txt" }), "rename of the first link");

            Apply(new IndexChange(ChangeKind.AttributesChanged, F, A, "f2.txt", FileAttributes.Hidden, null, null));
            bool allHidden = true;
            for (int i = 1; i < v.Count; i++) if (!v.IsDeleted(i) && !v.IsDirectory(i)) allHidden &= (v.Flags[i] & IndexEntryFlags.Hidden) != 0;
            Check(allHidden, "attribute change applies to every link");

            // Compaction keeps link keys and the link map.
            v = v.Compact();
            Check(Names().SequenceEqual(new[] { @"a\f2.txt", @"b\h.txt" }) && v.LinkedBases != null && v.LinkedBases.ContainsKey(F), "compaction keeps links");

            File.Delete(Path.Combine(dir, "a", "f2.txt"));
            Apply(new IndexChange(ChangeKind.HardLinkChanged, F, A, "f2.txt", FileAttributes.Archive, null, null));
            Check(Names().SequenceEqual(new[] { @"b\h.txt" }), "link removed (first name) → other link stays");

            Check(CreateHardLink(Path.Combine(dir, "a", "back.txt"), Path.Combine(dir, "b", "h.txt"), IntPtr.Zero), "CreateHardLink 2");
            Apply(new IndexChange(ChangeKind.HardLinkChanged, F, A, "back.txt", FileAttributes.Archive, null, null));
            Check(Names().SequenceEqual(new[] { @"a\back.txt", @"b\h.txt" }), "link added again");

            File.Delete(Path.Combine(dir, "a", "back.txt"));
            File.Delete(Path.Combine(dir, "b", "h.txt"));
            Apply(new IndexChange(ChangeKind.HardLinkChanged, F, A, "back.txt", FileAttributes.Archive, null, null),
                  new IndexChange(ChangeKind.Deleted, F, B, "h.txt", FileAttributes.Archive, null, null));
            Check(Names().Count == 0, "last link deleted → all gone");
            Check(v.LinkedBases == null || !v.LinkedBases.ContainsKey(F), "link map entry dropped");

            // Key encoding
            Check(LinkKey.Base(LinkKey.Extra(F, 5)) == F && LinkKey.IsExtra(LinkKey.Extra(F, 5)) && !LinkKey.IsExtra(F), "LinkKey round trip");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>Crawler vs USN: which paths only one side has, grouped by their first three path levels.</summary>
    private static void DiffIndexes(VolumeIndex crawl, VolumeIndex usn)
    {
        static HashSet<string> Paths(VolumeIndex v)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < v.Count; i++) if (!v.IsDeleted(i)) set.Add(v.GetFullPath(i));
            return set;
        }
        var a = Paths(crawl);
        var b = Paths(usn);
        void Report(string title, HashSet<string> x, HashSet<string> y)
        {
            var only = x.Where(p => !y.Contains(p)).ToList();
            Console.WriteLine($"  only in {title}: {only.Count:N0}");
            foreach (var g in only.GroupBy(p => string.Join('\\', p.Split('\\').Take(4))).OrderByDescending(g => g.Count()).Take(12))
                Console.WriteLine($"    {g.Count(),8:N0}  {g.Key}   e.g. {g.First()}");
        }
        Report("crawler", a, b);
        Report("USN", b, a);
    }
}
