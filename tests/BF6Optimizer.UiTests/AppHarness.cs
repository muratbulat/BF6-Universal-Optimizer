using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace BF6Optimizer.UiTests;

/// <summary>
/// Starts the real EXE with test switches: temp data, a fixture BF6 folder, mock network and an in-memory
/// registry. The user's real profiles, registry and network targets are never touched.
/// </summary>
public sealed class AppHarness : IDisposable
{
    public const string Profile =
        "GstAudio.Volume_Master 0.800000\nGstRender.AmbientOcclusion 1\nGstRender.DRSEnabled 1\nGstRender.EffectsQuality 2\n" +
        "GstRender.FilmGrain 1\nGstRender.FrameRateLimit 120.000000\nGstRender.FrameRateLimiterEnable 0\nGstRender.MeshQuality 2\n" +
        "GstRender.NvidiaUpscalingTechnique 1\nGstRender.ResolutionWidth 2560\nGstRender.ShadowQuality 2\nGstRender.TextureQuality 2\n" +
        "GstRender.VSyncMode 1\nGstRender.VolumetricQuality 2\nGstKeyBinding.Fire Mouse0\n";

    private static readonly string Root = FindRoot();
    public static readonly string ArtifactDir = Path.Combine(Root, "tests", "artifacts");

    private readonly UIA3Automation _automation = new();
    private FlaUI.Core.Application? _app;
    private Process? _process;

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "bf6ui_" + Guid.NewGuid().ToString("N")[..8]);
    public string DataDir => Path.Combine(Dir, "data");
    public string ExportDir => Path.Combine(Dir, "export");
    public string Bf6Root => Environment.GetEnvironmentVariable("BF6_DEMO_ROOT") ?? Path.Combine(Dir, "bf6");
    public string ProfilePath => Path.Combine(Bf6Root, "Battlefield 6", "settings", "steam", "PROFSAVEbf6mp_profile");

    public Window Main { get; private set; } = null!;

    public AppHarness(string language = "en", string? settingsJson = null, bool withProfile = true)
    {
        Directory.CreateDirectory(Path.Combine(DataDir, "Settings"));
        Directory.CreateDirectory(ExportDir);
        Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath)!);
        if (withProfile) File.WriteAllText(ProfilePath, Profile, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(DataDir, "Settings", "settings.json"),
            settingsJson ?? $$"""{ "schemaVersion": 1, "language": "{{language}}", "welcomeCompleted": true }""");
        Start();
    }

    public static string Exe => Environment.GetEnvironmentVariable("BF6_UI_EXE")
        ?? Path.Combine(Root, "src", "BF6Optimizer.App", "bin", "Debug", "net10.0-windows", "win-x64", "BF6Optimizer.exe");

    private static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "BF6Optimizer.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("Solution root not found.");
    }

    public void Start()
    {
        var args = $"--data-dir \"{DataDir}\" --bf6-dir \"{Bf6Root}\" --export-dir \"{ExportDir}\" --mock-network --mock-system" + (Environment.GetEnvironmentVariable("BF6_DEMO_ROOT") is null ? "" : " --simulate-hardware");
        _process = Process.Start(new ProcessStartInfo(Exe, args) { WorkingDirectory = Path.GetDirectoryName(Exe) })!;
        _app = FlaUI.Core.Application.Attach(_process);
        Main = Retry.WhileNull(() => _app.GetMainWindow(_automation, TimeSpan.FromSeconds(2)), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(300)).Result
            ?? throw new TimeoutException("Main window did not appear.");
        WaitUntil(() => Find("nav.dashboard") is not null, "navigation");
        WaitUntil(() => HeaderIdle(), "startup scan", 40);
    }

    private bool HeaderIdle()
    {
        var t = Find("header.status")?.Name ?? "";
        return t.Length > 0 && !t.Contains("Scanning") && !t.Contains("Tarama");
    }

    public void WaitIdle() => WaitUntil(HeaderIdle, "scan end", 90);

    public void WaitUntil(Func<bool> condition, string what, double seconds = 15)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            try { if (condition()) return; }
            catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
            Thread.Sleep(100);
        }
        throw new TimeoutException("Timed out waiting for " + what);
    }

    public AutomationElement? Find(string id) => Main.FindFirstDescendant(c => c.ByAutomationId(id));

    public AutomationElement Get(string id)
    {
        Coverage.Touch(id);
        AutomationElement? e = null;
        WaitUntil(() => (e = Find(id)) is not null, "control " + id);
        return e!;
    }

    public void Click(string id)
    {
        var e = Get(id);
        WaitUntil(() => e.IsEnabled, "enabled " + id, 20);
        e.Focus();
        if (e.Patterns.Invoke.IsSupported) e.Patterns.Invoke.Pattern.Invoke();
        else e.AsButton().Click();
        Thread.Sleep(150);
    }

    public void Nav(string key)
    {
        Click(key);
        WaitUntil(() => Find(key) is not null, "page");
        Thread.Sleep(300);
    }

    public void Tab(string id)
    {
        Get(id).Patterns.SelectionItem.Pattern.Select();
        Thread.Sleep(300);
    }

    public string Text(string id) => Get(id).Name;

    public void Type(string id, string text)
    {
        var e = Get(id);
        e.Patterns.Value.Pattern.SetValue(text);
        Thread.Sleep(80);
    }

    public void Pick(string id, int index)
    {
        Get(id).Patterns.ExpandCollapse.Pattern.Expand();
        Thread.Sleep(150);
        var box = Find(id)!.AsComboBox();
        box.Items[index].Patterns.SelectionItem.Pattern.Select();
        box.Patterns.ExpandCollapse.Pattern.Collapse();
        Thread.Sleep(150);
    }

    /// <summary>Clicks a button in the current modal dialog by its caption.</summary>
    public void Dialog(string button)
    {
        Window? modal = null;
        WaitUntil(() => (modal = Main.ModalWindows.FirstOrDefault()) is not null, "dialog");
        var b = modal!.FindFirstDescendant(c => c.ByName(button).And(c.ByControlType(ControlType.Button)))
            ?? throw new InvalidOperationException("Dialog button not found: " + button);
        Assert.True(modal.BoundingRectangle.Contains(b.BoundingRectangle), "Dialog button is outside its dialog: " + button);
        b.Patterns.Invoke.Pattern.Invoke();
        Thread.Sleep(250);
    }

    /// <summary>Items only exist once the list has been opened, so read the selection while expanded.</summary>
    public string? Selected(string id)
    {
        var box = Get(id).AsComboBox();
        box.Patterns.ExpandCollapse.Pattern.Expand();
        Thread.Sleep(200);
        var text = box.SelectedItem?.Text;
        box.Patterns.ExpandCollapse.Pattern.Collapse();
        Thread.Sleep(150);
        return text;
    }

    public int Rows(string id) => Get(id).Patterns.Grid.Pattern.RowCount.Value;

    public void SelectRow(string id, int index) =>
        Get(id).FindAllChildren(c => c.ByControlType(ControlType.DataItem))[index].Patterns.SelectionItem.Pattern.Select();

    public void Resize(int width, int height)
    {
        Main.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal);
        Main.Patterns.Transform.Pattern.Move(0, 0);
        Main.Patterns.Transform.Pattern.Resize(width, height);
        Thread.Sleep(350);
    }

    public string Shot(string name, string? folder = null)
    {
        var dir = folder ?? Path.Combine(ArtifactDir, "screenshots");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".png");
        Main.Focus();
        Thread.Sleep(150);
        using var image = Capture.Element(Main);
        image.Bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    /// <summary>Layout assertions: buttons and inputs may scroll vertically but never cross the window's sides.</summary>
    public IReadOnlyList<string> LayoutProblems()
    {
        var win = Main.BoundingRectangle;
        var problems = new List<string>();
        var types = Main.FindAllDescendants(c => c.ByControlType(ControlType.Button).Or(c.ByControlType(ControlType.ComboBox)).Or(c.ByControlType(ControlType.Edit)));
        foreach (var e in types)
        {
            if (e.IsOffscreen || InDataGrid(e)) continue;
            var r = e.BoundingRectangle;
            if (r.Width == 0) continue;
            if (r.Right > win.Right + 1 || r.Left < win.Left - 1) problems.Add($"{e.AutomationId}/{e.Name}: x {r.Left}-{r.Right} outside {win.Left}-{win.Right}");
        }
        return problems;
    }

    /// <summary>Wide tables are allowed to scroll sideways inside their own area.</summary>
    private bool InDataGrid(AutomationElement e)
    {
        var walker = _automation.TreeWalkerFactory.GetControlViewWalker();
        var p = e;
        for (int i = 0; i < 6 && p is not null; i++)
        {
            if (p.ControlType == ControlType.DataGrid) return true;
            p = walker.GetParent(p);
        }
        return false;
    }

    public string[] LogErrors()
    {
        var logs = Path.Combine(DataDir, "Logs");
        return !Directory.Exists(logs) ? [] : Directory.GetFiles(logs, "*.log")
            .SelectMany(File.ReadAllLines).Where(l => l.Contains("[ERR]") || l.Contains("[CRT]")).ToArray();
    }

    public int Close()
    {
        _process!.CloseMainWindow();
        Assert.True(_process.WaitForExit(10000), "App did not exit.");
        return _process.ExitCode;
    }

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill();
        }
        catch (InvalidOperationException) { }
        _automation.Dispose();
        Thread.Sleep(200);
        // Baseline backups are read-only by design.
        foreach (var f in Directory.EnumerateFiles(Dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        try { Directory.Delete(Dir, true); }
        catch (IOException) { }
        Coverage.Flush();
    }
}

/// <summary>Records which controls tests actually operated, for the control inventory report.</summary>
public static class Coverage
{
    private static readonly HashSet<string> Seen = [];

    public static void Touch(string id)
    {
        lock (Seen) Seen.Add(id);
    }

    public static void Flush()
    {
        Directory.CreateDirectory(AppHarness.ArtifactDir);
        lock (Seen) File.AppendAllLines(Path.Combine(AppHarness.ArtifactDir, "ui-controls-exercised.txt"), Seen);
    }
}
