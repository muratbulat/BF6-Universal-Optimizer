using System.Security.Cryptography;
using System.Text.Json;
using FlaUI.Core.Definitions;

namespace BF6Optimizer.UiTests;

public static class Pages
{
    public static readonly string[] All =
    [
        "nav.dashboard", "nav.hardware", "nav.optimizer", "nav.advanced", "nav.status", "nav.profiles", "nav.backup", "nav.servers",
        "nav.health", "nav.monitor", "nav.memory", "nav.cpu", "nav.gpu", "nav.display", "nav.windows", "nav.storage", "nav.network",
        "nav.processes", "nav.bios", "nav.sessions", "nav.safe", "nav.reports", "nav.settings",
    ];
}

public class ShellTests
{
    [Fact]
    public void Starts_inside_the_screen_and_closes_cleanly()
    {
        using var app = new AppHarness();
        var r = app.Main.BoundingRectangle;
        var work = System.Windows.Forms.SystemInformation.VirtualScreen;
        Assert.True(work.Contains(r.Left + 8, r.Top + 8) && r.Width >= 760 && r.Height >= 460, $"Window {r}");
        Assert.Empty(app.LogErrors());
        Assert.Equal(0, app.Close());
    }

    [Theory]
    [InlineData("en")]
    [InlineData("tr")]
    public void Every_page_opens_without_errors(string language)
    {
        using var app = new AppHarness(language);
        foreach (var page in Pages.All) app.Nav(page);
        Assert.Empty(app.LogErrors());
    }

    [Fact]
    public void Welcome_shows_once()
    {
        using (var first = new AppHarness(settingsJson: """{ "schemaVersion": 1, "language": "en" }"""))
        {
            first.Click("welcome.start");
            first.Close();
            first.Start();
            Assert.True(first.Find("welcome.start") is null || first.Find("welcome.start")!.IsOffscreen);
        }
    }

    [Fact]
    public void Sidebar_collapses_on_a_narrow_window_and_toggle_works()
    {
        using var app = new AppHarness();
        app.Resize(800, 560);
        var compact = app.Get("nav.dashboard").BoundingRectangle.Width;
        Assert.True(compact < 60, $"Compact nav width {compact}");
        app.Click("nav.toggle");
        Thread.Sleep(300);
        Assert.True(app.Get("nav.dashboard").BoundingRectangle.Width > 150);
    }

    [Fact]
    public void Group_header_collapses_its_items()
    {
        using var app = new AppHarness();
        app.Click("nav.group.system");
        Thread.Sleep(200);
        Assert.Null(app.Find("nav.health"));
        app.Click("nav.group.system");
        Assert.NotNull(app.Get("nav.health"));
    }

    [Fact]
    public void Window_size_is_restored_after_restart()
    {
        using var app = new AppHarness();
        app.Resize(1000, 640);
        var before = app.Main.BoundingRectangle;
        app.Close();
        app.Start();
        var after = app.Main.BoundingRectangle;
        Assert.InRange(after.Width, before.Width - 2, before.Width + 2);
        Assert.InRange(after.Height, before.Height - 2, before.Height + 2);
    }

    [Fact]
    public void Off_screen_saved_window_opens_on_a_visible_display()
    {
        using var app = new AppHarness(settingsJson: """{ "schemaVersion": 1, "language": "en", "welcomeCompleted": true, "window": { "x": 90000, "y": 90000, "width": 1000, "height": 700 } }""");
        var r = app.Main.BoundingRectangle;
        Assert.True(System.Windows.Forms.SystemInformation.VirtualScreen.Contains(r.Left + 20, r.Top + 20), $"Window {r}");
    }

    [Fact]
    public void Language_change_applies_and_persists()
    {
        using var app = new AppHarness();
        app.Nav("nav.settings");
        app.Pick("field.Languages", 0);
        app.WaitUntil(() => app.Get("nav.settings").Name == "Ayarlar", "Turkish nav");
        app.Close();
        app.Start();
        Assert.Equal("Ayarlar", app.Get("nav.settings").Name);
    }

    [Fact]
    public void Theme_switch_works_and_persists()
    {
        using var app = new AppHarness();
        app.Nav("nav.settings");
        app.Pick("field.Themes", 1);
        app.Shot("theme_light_settings");
        app.Nav("nav.dashboard");
        app.Shot("theme_light_dashboard");
        app.Close();
        Assert.Contains("Light", File.ReadAllText(Path.Combine(app.DataDir, "Settings", "settings.json")));
        app.Start();
    }

    [Fact]
    public void Header_settings_button_opens_settings()
    {
        using var app = new AppHarness();
        app.Click("header.settings");
        Assert.NotNull(app.Get("ui.open_data"));
    }
}

public class LayoutTests
{
    public static TheoryData<int, int> Sizes => new() { { 1280, 720 }, { 1366, 768 }, { 1600, 900 }, { 1920, 1080 }, { 2560, 1400 }, { 800, 500 } };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Every_page_fits_the_viewport_width(int width, int height)
    {
        using var app = new AppHarness("tr");
        app.Resize(width, height);
        var problems = new List<string>();
        foreach (var page in Pages.All)
        {
            app.Nav(page);
            app.Shot($"{width}x{height}_{page}");
            problems.AddRange(app.LayoutProblems().Select(p => $"{page}: {p}"));
        }
        Assert.Empty(problems);
    }

    [Fact]
    public void Server_tabs_fit_at_small_size()
    {
        using var app = new AppHarness("tr");
        app.Resize(1000, 620);
        app.Nav("nav.servers");
        var problems = new List<string>();
        foreach (var tab in new[] { "tab.regions", "tab.live", "tab.route", "tab.endpoints", "tab.active", "tab.history" })
        {
            app.Tab(tab);
            app.Shot($"1000x620_{tab}");
            problems.AddRange(app.LayoutProblems().Select(p => $"{tab}: {p}"));
        }
        Assert.Empty(problems);
    }

    [Fact]
    public void Dashboard_cards_reflow()
    {
        using var app = new AppHarness();
        app.Resize(1900, 1000);
        var wide = app.Get("dash.score").BoundingRectangle;
        var wideCoverage = app.Get("dash.coverage").BoundingRectangle;
        Assert.InRange(wideCoverage.Top, wide.Top - 2, wide.Top + 2);
        app.Resize(800, 560);
        var narrow = app.Get("dash.score").BoundingRectangle;
        var narrowCoverage = app.Get("dash.coverage").BoundingRectangle;
        Assert.True(narrowCoverage.Top >= narrow.Top);
        Assert.True(app.Main.BoundingRectangle.Contains(narrow.Left + 1, narrow.Top + 1));
    }
}

public class DashboardTests
{
    [Fact]
    public void Full_scan_updates_and_actions_navigate()
    {
        using var app = new AppHarness();
        Assert.Contains("%", app.Text("dash.coverage"));
        app.Click("dash.scan");
        app.WaitIdle();
        app.WaitUntil(() => app.Text("dash.score").Contains("/100"), "score");

        app.Click("dash.optimize");
        Assert.NotNull(app.Get("ui.preview"));
        app.Nav("nav.dashboard");
        app.Click("dash.network");
        Assert.NotNull(app.Get("net.test_all"));
        app.Nav("nav.dashboard");
        app.Click("dash.recs");
        Assert.NotNull(app.Get("field.Filters"));
        app.Nav("nav.dashboard");
        app.Click("dash.quick");
        app.WaitIdle();
    }

    [Fact]
    public void Report_export_writes_a_private_valid_file_and_cancel_writes_nothing()
    {
        using var app = new AppHarness();
        app.Click("dash.export");
        app.Dialog("Cancel");
        Assert.Empty(Directory.GetFiles(app.ExportDir));

        app.Click("dash.export");
        app.Dialog("Save as...");
        app.WaitUntil(() => Directory.GetFiles(app.ExportDir).Length == 1, "html file");
        var html = File.ReadAllText(Directory.GetFiles(app.ExportDir)[0]);
        Assert.Contains("<!DOCTYPE html>", html);
        Assert.DoesNotContain(Environment.UserName + "\\", html);
        Assert.Contains("Saved", app.Text("toast"));
    }

    [Fact]
    public void Json_and_text_reports_from_reports_page()
    {
        using var app = new AppHarness();
        app.Nav("nav.reports");
        app.Click("ui.export_json");
        app.Dialog("Save as...");
        app.WaitUntil(() => Directory.GetFiles(app.ExportDir, "*.json").Length == 1, "json");
        using (JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(app.ExportDir, "*.json")[0]))) { }

        app.Click("ui.export_txt");
        app.Dialog("Save as...");
        app.WaitUntil(() => Directory.GetFiles(app.ExportDir, "*.txt").Length == 1, "txt");

        app.Click("ui.export_diag");
        app.Dialog("Save as...");
        app.WaitUntil(() => Directory.GetFiles(app.ExportDir, "*Diagnostics*").Length == 1, "diag");
    }
}

public class Bf6FlowTests
{
    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void Preview_apply_verify_restore_round_trip()
    {
        using var app = new AppHarness();
        var original = Sha(app.ProfilePath);

        app.Nav("nav.optimizer");
        app.Pick("field.ModeNames", 1);
        app.Click("ui.preview");
        app.WaitUntil(() => app.Find("ui.apply") is not null, "preview");
        Assert.Equal(original, Sha(app.ProfilePath));

        // Declining the dialog changes nothing.
        app.Click("ui.apply");
        app.Dialog("Cancel");
        Assert.Equal(original, Sha(app.ProfilePath));

        app.Click("ui.apply");
        app.Dialog("Apply");
        app.WaitUntil(() => File.ReadAllText(app.ProfilePath).Contains("GstRender.VSyncMode 0"), "write", 20);
        app.WaitUntil(() => app.Text("opt.result").StartsWith("Success"), "apply result", 20);
        var text = File.ReadAllText(app.ProfilePath);
        Assert.Contains("GstAudio.Volume_Master 0.800000", text);
        Assert.Contains("GstRender.NvidiaUpscalingTechnique 1", text);
        Assert.Contains("GstKeyBinding.Fire Mouse0", text);

        app.Nav("nav.status");
        app.Click("status.check");
        app.WaitUntil(() => app.Text("status.headline").StartsWith("All settings match"), "verified status");
        Assert.True(app.Rows("status.grid") > 0);

        // A value changed behind the app's back must show as drift.
        File.WriteAllText(app.ProfilePath, File.ReadAllText(app.ProfilePath).Replace("GstRender.VSyncMode 0", "GstRender.VSyncMode 1"));
        app.Click("status.check");
        app.WaitUntil(() => app.Text("status.headline").StartsWith("Some settings differ"), "drift");

        app.Nav("nav.backup");
        app.Click("ui.restore_baseline");
        app.Dialog("Restore");
        app.WaitUntil(() => Sha(app.ProfilePath) == original, "restored file", 15);
        Assert.StartsWith("Success", app.Text("backup.message"));

        app.Nav("nav.status");
        app.Click("status.check");
        app.WaitUntil(() => app.Text("status.headline").StartsWith("No verified apply"), "record cleared");
    }

    [Fact]
    public void Missing_bf6_config_is_explained_and_apply_is_not_offered()
    {
        using var app = new AppHarness(withProfile: false);
        app.Nav("nav.optimizer");
        app.Click("ui.preview");
        Thread.Sleep(500);
        Assert.Null(app.Find("ui.apply"));
        app.Nav("nav.status");
        app.Click("status.check");
        app.WaitUntil(() => app.Text("status.headline").StartsWith("No verified apply"), "headline");
        Assert.Empty(app.LogErrors());
    }

    [Fact]
    public void Read_only_profile_blocks_apply_without_change()
    {
        using var app = new AppHarness();
        app.Nav("nav.optimizer");
        app.Click("ui.preview");
        app.WaitUntil(() => app.Find("ui.apply") is not null, "preview");
        File.SetAttributes(app.ProfilePath, FileAttributes.ReadOnly);
        try
        {
            app.Click("ui.apply");
            app.Dialog("Apply");
            app.WaitUntil(() => app.Text("opt.result").StartsWith("Could not start"), "blocked result");
            Assert.Contains("read-only", app.Text("opt.result"));
            Assert.Contains("GstRender.VSyncMode 1", File.ReadAllText(app.ProfilePath));
            Assert.Empty(app.LogErrors());
        }
        finally { File.SetAttributes(app.ProfilePath, FileAttributes.Normal); }
    }

    [Fact]
    public void Advanced_settings_create_a_custom_preview()
    {
        using var app = new AppHarness();
        app.Nav("nav.advanced");
        app.Pick("field.FpsModes", 2);
        app.Type("field.FpsText", "5");
        app.WaitUntil(() => app.Find("field.FpsText") is not null, "fps");
        app.Type("field.FpsText", "200");
        app.Click("ui.use_custom");
        app.WaitUntil(() => app.Find("ui.apply") is not null, "custom preview");
    }

    [Fact]
    public void Profiles_save_duplicate_rename_export_delete()
    {
        using var app = new AppHarness();
        app.Nav("nav.profiles");
        app.Click("ui.profile_save_current");
        app.Click("ui.duplicate");
        app.Type("field.EditName", "UI Test Profile");
        app.Click("ui.rename");
        app.Click("ui.export");
        app.WaitUntil(() => Directory.GetFiles(app.ExportDir, "*.json").Length == 1, "export");
        Assert.Contains("UI Test Profile", File.ReadAllText(Directory.GetFiles(app.ExportDir)[0]));
        app.Click("ui.profile_use");
        app.Click("ui.delete");
        app.Dialog("Delete");
        Assert.Empty(app.LogErrors());
    }

    [Fact]
    public void Backup_page_handles_empty_and_verify()
    {
        using var app = new AppHarness();
        app.Nav("nav.backup");
        Assert.Contains("No backups", app.Text("backup.message"));
        app.Click("ui.restore_latest");
        Assert.Contains("No successful apply", app.Text("backup.message"));
        app.Click("ui.restore_baseline");
        Assert.Contains("No baseline", app.Text("backup.message"));
        Assert.Empty(app.LogErrors());
    }
}

public class NetworkUiTests
{
    [Fact]
    public void Test_all_with_mock_probes_ranks_and_persists()
    {
        using var app = new AppHarness();
        app.Nav("nav.servers");
        app.Pick("net.samples", 0);
        app.Click("net.test_all");
        app.WaitUntil(() => app.Text("net.progress").Contains("regions tested"), "run", 60);
        Assert.Contains("Lowest measured region probe", app.Text("net.best"));
        app.Shot("servers_results");

        app.Click("net.export");
        app.WaitUntil(() => Directory.GetFiles(app.ExportDir, "BF6_Network*").Length == 1, "export");
        var json = File.ReadAllText(Directory.GetFiles(app.ExportDir, "BF6_Network*")[0]);
        Assert.Contains("RegionalProbeNotBf6Server", json);
        Assert.True(File.Exists(Path.Combine(app.DataDir, "Data", "network-history.json")));
    }

    [Fact]
    public void Stop_cancels_a_running_test()
    {
        using var app = new AppHarness();
        app.Nav("nav.servers");
        app.Pick("net.samples", 2);
        app.Click("net.test_all");
        Thread.Sleep(400);
        app.Click("net.stop");
        app.WaitUntil(() => app.Text("net.progress").StartsWith("Stopped"), "stop");
    }

    [Fact]
    public void Search_filters_the_table()
    {
        using var app = new AppHarness();
        app.Nav("nav.servers");
        var rows = app.Rows("net.grid");
        Assert.Equal(17, rows);
        app.Type("net.search", "Frankfurt");
        app.WaitUntil(() => app.Rows("net.grid") == 1, "filter");
    }

    [Fact]
    public void Live_monitor_starts_and_stops()
    {
        using var app = new AppHarness();
        app.Nav("nav.servers");
        app.Tab("tab.live");
        app.Click("live.start");
        app.WaitUntil(() => app.Text("live.summary").Contains("Average"), "sample", 8);
        app.Click("live.stop");
    }

    [Fact]
    public void Route_runs_stops_at_destination_and_rejects_bad_input()
    {
        using var app = new AppHarness();
        app.Nav("nav.servers");
        app.Tab("tab.route");
        app.Type("route.target", "bad host!");
        app.Click("route.start");
        app.WaitUntil(() => app.Text("route.error").Length > 0, "validation");
        app.Type("route.target", "3.64.0.0");
        app.Click("route.start");
        app.WaitUntil(() => app.Rows("route.grid") == 5, "hops", 15);
    }

    [Fact]
    public void Endpoint_manager_validates_saves_tests_and_deletes()
    {
        using var app = new AppHarness();
        app.Nav("nav.servers");
        app.Tab("tab.endpoints");
        app.Type("ep.name", "My server");
        app.Type("ep.host", "999.1.1.1");
        app.Click("ep.save");
        Assert.Contains("valid IP", app.Text("ep.message"));

        app.Type("ep.host", "11.20.30.40");
        app.Pick("ep.category", 3);
        app.Click("ep.save");
        Assert.Contains("evidence", app.Text("ep.message"));

        app.Pick("ep.category", 0);
        app.Click("ep.save");
        app.WaitUntil(() => app.Rows("ep.grid") == 1, "saved row");

        app.SelectRow("ep.grid", 0);
        Thread.Sleep(200);
        app.Click("ep.test");
        app.WaitUntil(() => app.Text("ep.message").Contains("replies"), "test result", 10);

        app.Click("ep.export");
        app.WaitUntil(() => File.Exists(Path.Combine(app.ExportDir, "bf6-endpoints.json")), "export");

        app.Click("ep.delete");
        app.Dialog("Delete");
        app.WaitUntil(() => app.Rows("ep.grid") == 0, "deleted");
    }

    [Fact]
    public void Active_connection_explains_when_game_is_closed()
    {
        using var app = new AppHarness();
        app.Nav("nav.servers");
        app.Tab("tab.active");
        app.Click("active.observe");
        Assert.Contains("not running", app.Text("active.status"));
        app.Click("active.save");
    }

    [Fact]
    public void Manual_in_game_ping_validates_and_saves()
    {
        using var app = new AppHarness();
        app.Nav("nav.servers");
        app.Tab("tab.history");
        app.Type("hist.manual", "abc");
        app.Click("hist.manual_save");
        Assert.Contains("1 to 2000", app.Text("hist.message"));
        app.Type("hist.manual", "42");
        app.Click("hist.manual_save");
        Assert.Equal("Saved.", app.Text("hist.message"));
    }

    [Fact]
    public void Thresholds_reject_bad_order()
    {
        using var app = new AppHarness();
        app.Nav("nav.servers");
        app.Type("net.th1", "500");
        app.Click("net.save_thresholds");
        Assert.Contains("ascending", app.Text("toast"));
    }

    [Fact]
    public void Network_page_latency_test_runs()
    {
        using var app = new AppHarness();
        app.Nav("nav.dashboard");
        app.Click("dash.scan");
        app.WaitIdle();
        app.Nav("nav.network");
        app.Type("field.Target", "11.0.0.1");
        app.Click("ui.run_test");
        app.WaitUntil(() => !app.Text("probe.result").StartsWith("Testing") && app.Text("probe.result").Length > 0, "probe", 30);
    }
}

public class SystemPageTests
{
    [Fact]
    public void Hardware_rescan_works()
    {
        using var app = new AppHarness();
        app.Nav("nav.hardware");
        app.Click("ui.rescan");
        Assert.Empty(app.LogErrors());
    }

    [Fact]
    public void Monitor_samples_and_stops_when_leaving()
    {
        using var app = new AppHarness();
        app.Nav("nav.monitor");
        app.Click("ui.start");
        app.WaitUntil(() => app.Text("monitor.extra").Contains("MHz") || app.Text("monitor.extra").Contains("GPU"), "sample", 10);
        app.Click("ui.stop");
        app.Click("ui.start");
        Thread.Sleep(800);
        app.Nav("nav.dashboard");
        Assert.Empty(app.LogErrors());
    }

    [Fact]
    public void Processes_refresh_and_startup_toggle()
    {
        using var app = new AppHarness();
        app.Nav("nav.processes");
        app.WaitUntil(() => app.Rows("proc.grid") > 0, "processes", 10);
        app.Pick("field.Sorts", 1);
        app.Click("ui.refresh");
        app.WaitUntil(() => app.Find("startup.toggle") is not null, "startup rows");
        app.Click("startup.toggle");
        app.Dialog("Disable");
        Assert.Empty(app.LogErrors());
    }

    [Fact]
    public void Safe_center_applies_and_restores_in_memory()
    {
        using var app = new AppHarness();
        app.Nav("nav.safe");
        app.Click("ui.safe_restore");
        app.Dialog("Restore");
        Assert.Contains("No backup", app.Text("safe.message"));
        app.Click("ui.safe_apply");
        app.Dialog("Apply preferences");
        Assert.Contains("verified", app.Text("safe.message"));
        app.Click("ui.safe_restore");
        app.Dialog("Restore");
        Assert.Contains("restored", app.Text("safe.message"));
    }

    [Fact]
    public void Session_starts_and_ends_with_a_report()
    {
        using var app = new AppHarness();
        app.Nav("nav.sessions");
        app.Click("ui.start_session");
        Thread.Sleep(2500);
        app.Click("ui.end_session");
        app.WaitUntil(() => app.Text("session.phase").StartsWith("Session finished"), "session end", 15);
        Assert.Single(Directory.GetFiles(Path.Combine(app.DataDir, "Reports"), "*_session.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void Health_filter_changes_the_list()
    {
        using var app = new AppHarness();
        app.Nav("nav.health");
        app.Pick("field.Filters", 1);
        app.Pick("field.Filters", 2);
        Assert.Empty(app.LogErrors());
    }
}
