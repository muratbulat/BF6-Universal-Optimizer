using System.Text.Json;
using System.Text.RegularExpressions;
using BF6Optimizer.Core.Audit;

namespace BF6Optimizer.Tests;

public class LocalizationTests
{
    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "BF6Optimizer.sln"))) d = d.Parent;
        return d!.FullName;
    }

    private static Dictionary<string, string> Table(string lang) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(Root(), "src", "BF6Optimizer.App", "Localization", lang + ".json")))!;

    private static int Holes(string text) => Regex.Matches(text, @"\{\d\}").Select(m => m.Value).Distinct().Count();

    [Fact]
    public void Turkish_and_english_have_identical_keys_and_placeholders()
    {
        var tr = Table("tr");
        var en = Table("en");
        Assert.Empty(tr.Keys.Except(en.Keys));
        Assert.Empty(en.Keys.Except(tr.Keys));
        foreach (var (key, text) in tr) Assert.True(Holes(text) == Holes(en[key]), key);
    }

    [Fact]
    public void Every_key_used_in_source_exists()
    {
        var tr = Table("tr");
        var src = Path.Combine(Root(), "src");
        var pattern = new Regex(@"\{loc:Tr ([\w.]+)|\b(?:L|Loc)\[""([\w.]+)""\]|L\.Format\(""([\w.]+)""|new Msg\(""([\w.]+)""");
        var missing = new List<string>();
        var files = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains("\\obj\\") && !f.Contains("\\bin\\"));
        foreach (var file in files)
            foreach (Match m in pattern.Matches(File.ReadAllText(file)))
            {
                var key = m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value;
                if (key.Contains('.') && !key.EndsWith('.') && !tr.ContainsKey(key)) missing.Add(key);
            }
        Assert.Empty(missing.Distinct());
    }

    [Fact]
    public void Every_check_has_all_texts()
    {
        var tr = Table("tr");
        var healthy = AuditFixtures.Healthy();
        var snapshots = new[]
        {
            healthy,
            new SystemAuditSnapshot(),
            healthy with { Full = false, Storage = null, Background = null, Network = null },
            healthy with
            {
                Memory = healthy.Memory! with { Modules = [AuditFixtures.Module(cfg: 4800, gb: 8), AuditFixtures.Module(cfg: 4800, gb: 16)] },
                Cpu = healthy.Cpu! with { Name = "AMD Ryzen 9 7950X3D", L3CacheGroups = 2 },
                NetworkProbe = new NetworkProbeResult("x", 1, 1, 1, 1, null),
            },
        };
        foreach (var c in snapshots.SelectMany(s => AuditFixtures.Eval(s).Checks))
        {
            Assert.True(tr.ContainsKey(c.TitleKey), c.TitleKey);
            Assert.True(tr.ContainsKey(c.ExpectedKey), c.ExpectedKey);
            Assert.True(tr.ContainsKey(c.AdviceKey), c.AdviceKey);
            if (c.Detected.Key != "raw") Assert.True(tr.ContainsKey(c.Detected.Key), c.Detected.Key);
        }
    }
}

public class CoverageHonestyTests
{
    [Fact]
    public void Quick_scan_does_not_report_full_coverage()
    {
        var s = AuditFixtures.Healthy() with { Full = false, Storage = null, Background = null, Network = null };
        var r = AuditFixtures.Eval(s);
        Assert.True(r.Readiness.CoveragePercent < 100);
        Assert.Contains(r.Checks, c => c.Stem == "full_scan_pending");
    }

    [Fact]
    public void Capacity_uses_installed_modules_not_usable_memory()
    {
        var s = AuditFixtures.Healthy();
        var c = AuditFixtures.Check(AuditFixtures.Eval(s with { Memory = s.Memory! with { TotalPhysical = 31 * AuditFixtures.Gb } }), "ram_capacity");
        Assert.Equal("32 GB", c.Detected.Args[0]);
    }
}
