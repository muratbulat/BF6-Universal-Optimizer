namespace BF6Optimizer.UiTests;

/// <summary>Runs only when BF6_DEMO_ROOT is set, so it is skipped in normal test runs.</summary>
public sealed class DemoFactAttribute : FactAttribute
{
    public DemoFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BF6_DEMO_ROOT") is null) Skip = "Needs BF6_DEMO_ROOT.";
    }
}

public class ReadmeScreenshots
{
    [DemoFact]
    public void Capture_demo_screenshots()
    {
        var folder = Environment.GetEnvironmentVariable("BF6_SHOTS")!;
        using var app = new AppHarness();
        app.Resize(1400, 860);

        app.Click("dash.scan");
        app.WaitIdle();
        Thread.Sleep(600);
        app.Shot("dashboard", folder);

        app.Nav("nav.hardware");
        app.Shot("hardware-overview", folder);
        app.Nav("nav.memory");
        app.Shot("memory-analyzer", folder);
        app.Nav("nav.health");
        app.Shot("gaming-health", folder);

        app.Nav("nav.optimizer");
        app.Pick("field.ModeNames", 1);
        app.Click("ui.preview");
        app.WaitUntil(() => app.Find("ui.apply") is not null, "preview");
        Thread.Sleep(400);
        app.Shot("bf6-optimizer", folder);
        app.Click("ui.apply");
        app.Dialog("Apply");
        app.WaitUntil(() => app.Text("opt.result").StartsWith("Success"), "apply", 20);

        app.Nav("nav.backup");
        app.Shot("backup-restore", folder);

        app.Nav("nav.servers");
        app.Pick("net.samples", 0);
        app.Click("net.test_all");
        app.WaitUntil(() => app.Text("net.progress").Contains("regions tested"), "run", 60);
        Thread.Sleep(500);
        app.Shot("battlefield-servers", folder);
        app.Close();
    }
}
