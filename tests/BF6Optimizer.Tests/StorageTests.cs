using System.Text;
using BF6Optimizer.Core.Apply;
using BF6Optimizer.Core.Backup;
using BF6Optimizer.Core.Common;
using BF6Optimizer.Core.Configuration;
using BF6Optimizer.Core.Diagnostics;
using BF6Optimizer.Core.Planning;
using BF6Optimizer.Core.Profiles;
using BF6Optimizer.Core.Verification;
using BF6Optimizer.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace BF6Optimizer.Tests;

/// <summary>Backups as v1.4.0 really writes them: UTF-8 BOM, CRLF, one-element arrays flattened to objects.</summary>
public class LegacyCompatibilityTests : IDisposable
{
    private const string Key = "Machine_ABC";
    private readonly TempDir _dir = new();
    private readonly AppPaths _paths;
    private readonly string _original;

    public LegacyCompatibilityTests()
    {
        _paths = new AppPaths(_dir.File("app"));
        _paths.EnsureCreated();
        _original = _dir.File("game", "PROFSAVE_profile");
        Directory.CreateDirectory(Path.GetDirectoryName(_original)!);
        File.WriteAllText(_original, "current\n");
    }

    public void Dispose() => _dir.Dispose();

    private FileBackupStore Store(params string[] external) =>
        new(_paths, () => Key, () => external, new SystemClock(), NullLogger<FileBackupStore>.Instance);

    private string WriteLegacy(string root, string folder, string kind, string content, bool singleObject = true)
    {
        var dir = Path.Combine(root, Key, folder);
        var gc = Path.Combine(dir, "GameConfigs");
        Directory.CreateDirectory(gc);
        var backup = Path.Combine(gc, "01_PROFSAVE_profile");
        File.WriteAllText(backup, content);
        var sha = Hashing.Sha256(File.ReadAllBytes(backup));

        var esc = (string s) => s.Replace("\\", "\\\\");
        // The stored Backup path is stale on purpose: packages get moved.
        var entry = $$"""{"Original":"{{esc(_original)}}","Backup":"C:\\old\\place\\GameConfigs\\01_PROFSAVE_profile","SHA256":"{{sha}}"}""";
        var map = singleObject ? entry : "[" + entry + "]";
        var state = $$"""
            {"ScriptVersion":"1.4.0","Created":"2026-01-02T03:04:05.0000000+03:00","Kind":"{{kind}}","MachineKey":"{{Key}}",
             "Registry":[{"Path":"HKCU:\\Software\\Microsoft\\GameBar","Name":"AutoGameModeEnabled","Exists":true,"Value":0},
                         {"Path":"HKLM:\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers","Name":"HwSchMode","Exists":true,"Value":2}],
             "Profiles":{{map}}}
            """;
        File.WriteAllText(Path.Combine(dir, "State.json"), state.ReplaceLineEndings("\r\n"), new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(dir, "GameConfigMap.json"), map, new UTF8Encoding(true));
        return dir;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Legacy_baseline_is_read_without_rewriting(bool singleObject)
    {
        var dir = WriteLegacy(_paths.Backups, "OriginalBaseline", "OriginalBaseline", "original\n", singleObject);
        var stamp = File.GetLastWriteTimeUtc(Path.Combine(dir, "State.json"));

        var point = Store().GetBaseline()!;

        Assert.True(point.IsLegacy);
        Assert.Equal("1.4.0", point.AppVersion);
        Assert.Equal(RestorePointKind.OriginalBaseline, point.Kind);
        Assert.Single(point.Files);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(dir, "State.json")));
        Assert.False(File.Exists(Path.Combine(dir, "Manifest.json")));
    }

    [Fact]
    public void Hklm_entries_are_dropped_from_registry_state()
    {
        WriteLegacy(_paths.Backups, "OriginalBaseline", "OriginalBaseline", "x");
        var reg = Assert.Single(Store().GetBaseline()!.Registry);
        Assert.Equal(@"Software\Microsoft\GameBar", reg.Path);
    }

    [Fact]
    public async Task Legacy_snapshot_restores_with_hash_check()
    {
        WriteLegacy(_paths.Backups, Path.Combine("Snapshots", "20260102_030405_000_abc123"), "PreApplySnapshot", "older\n");
        var store = Store();
        var point = store.List().Single();
        Assert.Equal(RestorePointKind.PreApply, point.Kind);

        var reg = new FakeRegistry();
        var records = new MemoryRecords();
        var engine = new RestoreEngine(new FileProfileStore(), store, reg, new FakeGame(), records, new MemoryReports(), new SystemClock(), NullLogger<RestoreEngine>.Instance);
        var result = await engine.RestoreAsync(point, Key);

        Assert.Equal(OperationOutcome.Success, result.Outcome);
        Assert.Equal("older\n", File.ReadAllText(_original));
        Assert.Equal(0, reg.Read(@"Software\Microsoft\GameBar", "AutoGameModeEnabled").Value);
    }

    [Fact]
    public async Task Existing_legacy_baseline_is_kept_as_the_baseline()
    {
        WriteLegacy(_paths.Backups, "OriginalBaseline", "OriginalBaseline", "original\n");
        var point = await Store().EnsureBaselineAsync([_original], [], default);
        Assert.True(point.IsLegacy);
    }

    [Fact]
    public void External_legacy_folder_is_listed_with_distinct_id()
    {
        var external = _dir.File("package");
        WriteLegacy(Path.Combine(external, "Backups"), "OriginalBaseline", "OriginalBaseline", "o");
        var point = Assert.Single(Store(external).List());
        Assert.StartsWith("ext", point.Id);
        Assert.NotNull(Store(external).Find(point.Id));
    }

    [Fact]
    public void Legacy_success_marker_is_read_and_a_later_clear_hides_it()
    {
        var dir = Path.Combine(_paths.Backups, Key);
        Directory.CreateDirectory(dir);
        var marker = Path.Combine(dir, "LastSuccessfulApply.json");
        File.WriteAllText(marker, """
            {"ScriptVersion":"1.4.0","Timestamp":"2026-01-02T03:04:05+03:00","Snapshot":"abc","Plan":{"Goal":"Competitive"},
             "Profiles":{"Path":"C:\\p\\PROFSAVE_profile","SHA256":"AA","ExpectedSettings":{"GstRender.VSyncMode":"0"}}}
            """, new UTF8Encoding(true));

        var store = new JsonApplyRecordStore(_paths, () => Key, () => []);
        var record = store.Load()!;
        Assert.True(record.IsLegacy);
        Assert.Equal(OptimizationMode.Competitive, record.Mode);
        Assert.Equal("0", record.Files[0].Settings["GstRender.VSyncMode"]);

        store.Clear();
        Assert.Null(store.Load());
        Assert.True(File.Exists(marker));
    }
}

public class StoragePathTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Default_is_localappdata_even_when_exe_folder_is_writable()
    {
        var p = AppPaths.Resolve(_dir.Path, _dir.File("BF6Optimizer.exe"), _dir.File("local"));
        Assert.Equal(StorageMode.LocalAppData, p.Mode);
        Assert.Equal(_dir.File("local", "BF6UniversalOptimizer"), p.Root);
    }

    [Fact]
    public void Marker_selects_portable_storage_next_to_exe()
    {
        Assert.True(AppPaths.TryEnablePortable(_dir.Path));
        var p = AppPaths.Resolve(_dir.Path, _dir.File("BF6Optimizer.exe"), _dir.File("local"));
        Assert.Equal(StorageMode.Portable, p.Mode);
        Assert.StartsWith(_dir.Path, p.Root);

        AppPaths.DisablePortable(_dir.Path);
        Assert.Equal(StorageMode.LocalAppData, AppPaths.Resolve(_dir.Path, _dir.File("BF6Optimizer.exe"), _dir.File("local")).Mode);
    }

    [Fact]
    public void Paths_with_spaces_and_required_folders_are_created()
    {
        var p = new AppPaths(_dir.File("with spaces", "data root"));
        p.EnsureCreated();
        foreach (var d in new[] { "Data", "Backups", "Reports", "Logs", "Profiles", "Settings" })
            Assert.True(Directory.Exists(Path.Combine(p.Root, d)));
    }

    [Fact]
    public void Corrupt_settings_fall_back_and_are_kept()
    {
        var p = new AppPaths(_dir.File("x"));
        p.EnsureCreated();
        File.WriteAllText(Path.Combine(p.Settings, "settings.json"), "{broken");
        Assert.Equal("tr", new AppSettingsStore(p).Load().Language);
        Assert.True(File.Exists(Path.Combine(p.Settings, "settings.json.corrupt")));
    }

    [Fact]
    public void Settings_survive_a_reload()
    {
        var p = new AppPaths(_dir.File("x"));
        p.EnsureCreated();
        new AppSettingsStore(p).Save(new AppSettings { Language = "en", Theme = "Light" });
        Assert.Equal("en", new AppSettingsStore(p).Load().Language);
    }
}

public class FileStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FileProfileStore _store = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Atomic_write_replaces_content_and_leaves_no_temp_files()
    {
        var p = _dir.File("f");
        File.WriteAllText(p, "old");
        await _store.WriteAtomicAsync(p, "new"u8.ToArray(), default);
        Assert.Equal("new", File.ReadAllText(p));
        Assert.Single(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public async Task Read_only_target_is_refused()
    {
        var p = _dir.File("f");
        File.WriteAllText(p, "old");
        File.SetAttributes(p, FileAttributes.ReadOnly);
        await Assert.ThrowsAsync<OptimizerException>(() => _store.WriteAtomicAsync(p, [1], default));
        Assert.Equal("old", File.ReadAllText(p));
    }

    [Fact]
    public void Inspect_reports_states()
    {
        var p = _dir.File("f");
        Assert.Equal(FileAccessState.Missing, _store.Inspect(p));
        File.WriteAllText(p, "x");
        Assert.Equal(FileAccessState.Ok, _store.Inspect(p));
        using var hold = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal(FileAccessState.Locked, _store.Inspect(p));
    }
}

public class ScannerTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private ProfileScanner Scanner() => new(new FileProfileStore(), SettingCatalog.LoadEmbedded(), () => [_dir.Path]);

    [Fact]
    public async Task No_folder_gives_empty_list_not_error() =>
        Assert.Empty(await Scanner().ScanAsync(null));

    [Fact]
    public async Task Finds_only_named_profiles_and_flags_steam_cloud()
    {
        var steam = _dir.File("Battlefield 6", "settings", "steam");
        Directory.CreateDirectory(steam);
        File.WriteAllText(Path.Combine(steam, "PROFSAVEbf6mp_profile"), Fixtures.Profile);
        File.WriteAllBytes(Path.Combine(steam, "PROFSAVE"), [0, 1, 2]);
        File.WriteAllText(Path.Combine(steam, "steam_autocloud.vdf"), "");

        var found = Assert.Single(await Scanner().ScanAsync(null));
        Assert.True(found.Compatible);
        Assert.True(found.SteamCloud);
        Assert.Equal("steam", found.Launcher);
    }

    [Fact]
    public async Task Incompatible_profile_is_reported_not_hidden()
    {
        var root = _dir.File("Battlefield 6", "settings");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "PROFSAVE_profile"), "Nothing 1\n");
        var found = Assert.Single(await Scanner().ScanAsync(null));
        Assert.False(found.Compatible);
        Assert.Equal("error.profile_unsupported", found.Problem!.Key);
    }

    [Fact]
    public async Task Manual_folder_is_scanned()
    {
        var manual = _dir.File("elsewhere");
        Directory.CreateDirectory(manual);
        File.WriteAllText(Path.Combine(manual, "PROFSAVE_profile"), Fixtures.Profile);
        Assert.Single(await Scanner().ScanAsync(manual));
    }
}

public class ProfileTests
{
    private readonly SettingCatalog _catalog = SettingCatalog.LoadEmbedded();

    private static OptimizationProfile Valid() => new() { Name = "Mine" };

    [Fact]
    public void Built_ins_exist_and_validate()
    {
        Assert.Equal(4, BuiltInProfiles.All.Count);
        Assert.All(BuiltInProfiles.All, p => Assert.Empty(ProfileValidator.Validate(p, _catalog)));
    }

    [Fact]
    public void Export_strips_machine_binding_and_import_gets_a_new_identity()
    {
        var p = Valid();
        p.Machine = new MachineBinding { GpuId = "secret" };
        var json = ProfileValidator.Export(p);
        Assert.DoesNotContain("secret", json);

        var (imported, errors) = ProfileValidator.ParseImport(json, _catalog);
        Assert.Empty(errors);
        Assert.NotEqual(p.Id, imported!.Id);
        Assert.Null(imported.Machine);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"schemaVersion":99,"name":"x"}""")]
    [InlineData("""{"schemaVersion":1,"name":""}""")]
    [InlineData("""{"schemaVersion":1,"name":"x","preferences":{"overrides":{"GstRender.Invented":"1"}}}""")]
    [InlineData("""{"schemaVersion":1,"name":"x","preferences":{"overrides":{"GstRender.ShadowQuality":"9"}}}""")]
    [InlineData("""{"schemaVersion":1,"name":"x","preferences":{"overrides":{"GstRender.NvidiaUpscalingTechnique":"1"}}}""")]
    [InlineData("""{"schemaVersion":1,"name":"x","preferences":{"fpsCapMode":"Manual","manualFpsCap":5}}""")]
    public void Bad_imports_are_rejected_whole(string json) =>
        Assert.Null(ProfileValidator.ParseImport(json, _catalog).Profile);

    [Fact]
    public void Store_round_trip_and_rejects_path_ids()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        paths.EnsureCreated();
        var store = new JsonProfileStore(paths);
        var p = Valid();
        store.Save(p);
        Assert.Equal("Mine", Assert.Single(store.LoadUserProfiles()).Name);
        store.Delete(p.Id);
        Assert.Empty(store.LoadUserProfiles());
        Assert.Throws<ArgumentException>(() => store.Save(new OptimizationProfile { Id = @"..\evil", Name = "x" }));
    }
}

public class DiagnosticsTests
{
    [Fact]
    public void Redactor_removes_user_paths_machine_and_guids()
    {
        var r = new Redactor([(@"C:\Users\murat", "%USERPROFILE%"), ("murat", "<user>"), ("DESKTOP-X1", "<pc>")]);
        var text = r.Apply(@"C:\Users\murat\Docs on DESKTOP-X1 by Murat id 12345678-1234-1234-1234-123456789abc");
        Assert.Equal(@"%USERPROFILE%\Docs on <pc> by <user> id <guid>", text);
    }

    [Fact]
    public void Redactor_handles_json_escaped_paths()
    {
        var r = new Redactor([(@"C:\Users\murat", "%USERPROFILE%")]);
        Assert.Equal("{\"p\":\"%USERPROFILE%\\\\x\"}", r.Apply("{\"p\":\"C:\\\\Users\\\\murat\\\\x\"}"));
    }

    [Fact]
    public void Report_omits_system_identity_and_marks_simulation()
    {
        var hw = Fixtures.Desktop with { Manufacturer = "ACME", Model = "Serial123", IsSimulated = true };
        var json = DiagnosticReportBuilder.Build(hw, null, [], [], new Redactor([]), DateTimeOffset.Now);
        Assert.DoesNotContain("Serial123", json);
        Assert.Contains("\"SimulatedHardware\": true", json);
        Assert.Contains("RTX 4080", json);
    }
}
