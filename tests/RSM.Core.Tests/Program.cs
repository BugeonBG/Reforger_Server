using System.Text.Json.Nodes;
using RSM.Core;

// Minimal test runner: each test is a method; a failed Check prints and counts.
// Developer aid: "--explain <log file>" prints how each line of a real server log is explained.
if (args.Length == 2 && args[0] == "--explain")
{
    foreach (var line in File.ReadLines(args[1]))
        if (LogExplainer.Explain(line) is { } e)
            Console.WriteLine($"{e.SeverityText,-6} | {e.Title,-22} | {(line.Length > 110 ? line[..110] : line)}");
    return 0;
}
if (args.Length == 2 && args[0] == "--diagnose")
{
    var sum = LogExplainer.Summarize(File.ReadAllLines(args[1]));
    Console.WriteLine(sum.Headline);
    sum.Facts.ForEach(f => Console.WriteLine("  ✓ " + f));
    foreach (var p in sum.Problems)
        Console.WriteLine($"  [{p.Explanation.SeverityText}] {p.Explanation.Title} ×{p.Count}: {p.Example}");
    Console.WriteLine($"  (게임 기본 경고 {sum.HarmlessCount}개 제외)");
    return 0;
}

var tests = new Tests();
var failed = await tests.RunAll();
Console.WriteLine(failed == 0 ? "all tests passed" : $"{failed} check(s) failed");
return failed == 0 ? 0 : 1;

sealed class Tests
{
    private int _failed;
    private string _current = "";
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "rsm-tests-" + Guid.NewGuid().ToString("N")[..8]);

    private void Check(bool ok, string what)
    {
        if (ok)
            return;
        _failed++;
        Console.WriteLine($"FAIL [{_current}] {what}");
    }

    public async Task<int> RunAll()
    {
        Directory.CreateDirectory(_tmp);
        try
        {
            Run(ConfigRoundTripKeepsUnknownKeys);
            Run(ConfigDefaultsAndRcon);
            Run(ConfigReadsLooseJson);
            Run(ModParserFormats);
            Run(ModMerge);
            Run(Validation);
            Run(CommandLines);
            Run(ScenarioParsing);
            Run(AppSettingsRoundTrip);
            Run(WorkshopPageParsing);
            await RunAsync(WorkshopDependencyWalk);
            await RunAsync(ViewModelDependencies);
            Run(WorkshopListParsing);
            await RunAsync(WorkshopBrowsing);
            await RunAsync(ModSwitching);
            Run(ModListFileFormats);
            await RunAsync(ModListMove);
            await RunAsync(A2sQueries);
            await RunAsync(MonitorSampling);
            await RunAsync(InstallFollowUps);
            Run(BuildIdParsing);
            Run(ConsoleLines);
            Run(PlayersFromLog);
            Run(LogExplanations);
            Run(ConnectionProblems);
            Run(ModFileDeletion);
            Run(ModScenarios);
            await RunAsync(WorkshopScenarios);
            await RunAsync(SelfUpdate);
            await RunAsync(ConnectionProblemsOnStatusPage);
            await RunAsync(PlayerListOnStatusPage);
            await RunAsync(ModUpdates);
            await RunAsync(ModUpdatesNeverDowngrade);
            Run(ModVersionCompare);
            Run(ModSearchAndFilter);
            Run(ModDownloadTracking);
            Run(LogFileTailFollowsRunLog);
            if (!OperatingSystem.IsWindows())
            {
                await RunAsync(ProcessRunnerCapturesOutput);
                await RunAsync(ViewModelEndToEnd);
                await RunAsync(FindServerAndAutoUpdate);
                await RunAsync(ModUpdatesWhileServerRuns);
            }
        }
        finally
        {
            try { Directory.Delete(_tmp, true); } catch { /* best effort */ }
        }
        return _failed;
    }

    private void Run(Action t)
    {
        _current = t.Method.Name;
        try { t(); }
        catch (Exception ex) { _failed++; Console.WriteLine($"FAIL [{_current}] threw {ex}"); }
    }

    private async Task RunAsync(Func<Task> t)
    {
        _current = t.Method.Name;
        try { await t(); }
        catch (Exception ex) { _failed++; Console.WriteLine($"FAIL [{_current}] threw {ex}"); }
    }

    //------------------------------------------------------------------------------------------
    private void ConfigRoundTripKeepsUnknownKeys()
    {
        const string original = """
        {
          "bindPort": 2101,
          "a2s": { "address": "10.0.0.5", "port": 17800 },
          "game": {
            "name": "Old",
            "scenarioId": "{ECC61978EDCC2B5A}Missions/23_Campaign.conf",
            "modsRequiredByDefault": true,
            "gameProperties": { "missionHeader": { "m_iPlayerCount": 40 }, "battlEye": false },
            "mods": [ { "modId": "5965550F24A0C152", "name": "Where Am I", "version": "1.2.0" } ]
          },
          "operating": { "lobbyPlayerSynchronise": true, "playerSaveTime": 120 }
        }
        """;
        var root = ServerConfigJson.Parse(original);
        var s = ServerConfigJson.Read(root);
        Check(s.BindPort == 2101 && s.A2sPort == 17800, "ports read");
        Check(s.ServerName == "Old" && !s.BattlEye, "game read");
        Check(s.Mods.Count == 1 && s.Mods[0].ModId == "5965550F24A0C152" && s.Mods[0].Version == "1.2.0", "mods read");

        s.ServerName = "한국 서버 #1";
        s.Mods.Add(new ModEntry("ABCDEF0123456789", "Test"));
        ServerConfigJson.Write(s, root);
        var text = ServerConfigJson.Serialize(root);

        Check(text.Contains("한국 서버 #1"), "Korean written as-is, not \\u escaped");
        var again = ServerConfigJson.Parse(text);
        Check(again["operating"]?["playerSaveTime"]?.GetValue<int>() == 120, "unknown top-level object kept");
        Check(again["game"]?["modsRequiredByDefault"]?.GetValue<bool>() == true, "unknown game key kept");
        Check(again["game"]?["gameProperties"]?["missionHeader"]?["m_iPlayerCount"]?.GetValue<int>() == 40, "unknown gameProperties key kept");
        Check(again["a2s"]?["address"]?.GetValue<string>() == "10.0.0.5", "existing a2s address kept");
        Check(ServerConfigJson.Read(again).Mods.Count == 2, "mod added");

        var path = Path.Combine(_tmp, "cfg", "server.json");
        ServerConfigJson.Save(path, root);
        var bytes = File.ReadAllBytes(path);
        Check(!(bytes.Length > 2 && bytes[0] == 0xEF && bytes[1] == 0xBB), "no UTF-8 BOM");
        Check(ServerConfigJson.Read(ServerConfigJson.Load(path)).ServerName == "한국 서버 #1", "save/load");
        Check(!File.Exists(path + ".tmp"), "temp file cleaned up");
    }

    private void ConfigDefaultsAndRcon()
    {
        var root = ServerConfigJson.CreateDefault();
        var s = ServerConfigJson.Read(root);
        Check(s.BindPort == 2001 && s.A2sPort == 17777 && s.ScenarioId == ScenarioCatalog.DefaultScenarioId, "defaults");
        Check(root["a2s"]?["address"]?.GetValue<string>() == "0.0.0.0", "a2s address filled");
        Check(root["rcon"] == null, "no rcon by default");

        s.RconEnabled = true;
        s.RconPassword = "secret";
        ServerConfigJson.Write(s, root);
        Check(root["rcon"]?["port"]?.GetValue<int>() == 19999 && root["rcon"]?["password"]?.GetValue<string>() == "secret", "rcon written");
        Check(ServerConfigJson.Read(root).RconEnabled, "rcon read back");

        s.RconEnabled = false;
        ServerConfigJson.Write(s, root);
        Check(root["rcon"] == null, "rcon removed when disabled");

        s.Mods.Add(new ModEntry("ABCDEF0123456789"));
        ServerConfigJson.Write(s, root);
        var mod = root["game"]?["mods"]?[0] as JsonObject;
        Check(mod != null && mod["version"] == null && mod["name"] == null, "empty version/name omitted");
    }

    private void ConfigReadsLooseJson()
    {
        var root = ServerConfigJson.Parse("""
        {
          // comment
          "bindPort": "2050",
          "game": { "maxPlayers": 40.0, "admins": ["765611980000", " ", "abc"], },
        }
        """);
        var s = ServerConfigJson.Read(root);
        Check(s.BindPort == 2050, "port as string");
        Check(s.MaxPlayers == 40, "int written as double");
        Check(s.Admins.SequenceEqual(new[] { "765611980000", "abc" }), "admins trimmed, blanks dropped");

        var threw = false;
        try { ServerConfigJson.Parse("[1,2]"); } catch (FormatException) { threw = true; }
        Check(threw, "array root rejected");
    }

    //------------------------------------------------------------------------------------------
    private void ModParserFormats()
    {
        // Mod Manager JSON tab: objects separated by commas, no brackets, trailing comma
        var mm = ModListParser.Parse("""
            {
                "modId": "5965550F24A0C152",
                "name": "Where Am I",
                "version": "1.2.0"
            },
            {
                "modId": "591AF5BDA9F7CE8B",
                "name": "Capture & Hold",
                "version": "1.0.4"
            },
            """);
        Check(mm.Count == 2 && mm[1].Name == "Capture & Hold" && mm[0].Version == "1.2.0", "mod manager format");

        Check(ModListParser.Parse("[{\"modId\":\"5965550F24A0C152\"}]").Count == 1, "array");
        Check(ModListParser.Parse("{\"modId\":\"5965550F24A0C152\",\"name\":\"x\"}").Single().Name == "x", "single object");
        Check(ModListParser.Parse("{\"game\":{\"mods\":[{\"modId\":\"5965550F24A0C152\"}]}}").Count == 1, "whole config");
        Check(ModListParser.Parse("{\"mods\":[{\"modId\":\"5965550F24A0C152\"}]}").Count == 1, "mods object");

        var plain = ModListParser.Parse("""
            https://reforger.armaplatform.com/workshop/5965550F24A0C152-WhereAmI
            591af5bda9f7ce8b
            5965550F24A0C152
            not a mod 123
            """);
        Check(plain.Count == 2 && plain[1].ModId == "591AF5BDA9F7CE8B", "links + plain ids, upper-cased, deduped");

        Check(ModListParser.Parse("").Count == 0 && ModListParser.Parse(null).Count == 0, "empty");
        Check(ModListParser.Parse("hello world").Count == 0, "garbage");
        Check(ModListParser.Parse("0123456789ABCDEF0").Count == 0, "17 hex digits is not an id");
    }

    private void ModMerge()
    {
        var list = new List<ModEntry> { new("5965550F24A0C152", "Where Am I", "1.0.0") };
        var (added, updated) = ModListParser.Merge(list, new[]
        {
            new ModEntry("5965550F24A0C152", "", "1.2.0"),
            new ModEntry("591AF5BDA9F7CE8B", "C&H"),
            new ModEntry("591af5bda9f7ce8b"),
        });
        Check(added == 1 && updated == 1, $"counts {added}/{updated}");
        Check(list.Count == 2 && list[0].Name == "Where Am I" && list[0].Version == "1.2.0", "empty name does not overwrite");
        Check(list[1].Name == "C&H", "later empty duplicate does not wipe name");
    }

    private void Validation()
    {
        var s = new ServerSettings();
        Check(SettingsValidator.Validate(s).Count == 0, "defaults are valid");

        s.ServerName = " ";
        s.MaxPlayers = 300;
        s.A2sPort = s.BindPort;
        s.AdminPassword = "a b";
        s.RconEnabled = true;
        s.RconPort = 2001;
        s.NetworkViewDistance = 6000;
        s.Mods.Add(new ModEntry("XYZ"));
        s.Mods.Add(new ModEntry("5965550F24A0C152"));
        s.Mods.Add(new ModEntry("5965550F24A0C152"));
        var errors = SettingsValidator.Validate(s);
        foreach (var expected in new[] { "서버 이름", "최대 인원", "A2S 포트가 같습니다", "관리자 비밀번호", "RCON 포트가", "RCON 비밀번호", "네트워크 시야", "모드 ID가 올바르지", "두 번" })
            Check(errors.Any(e => e.Contains(expected)), "error mentions: " + expected);
    }

    private void CommandLines()
    {
        Check(ServerTools.Quote(@"C:\Reforger Server\") == "\"C:\\Reforger Server\"", "trailing backslash dropped");
        Check(ServerTools.Quote(@"C:\") == "\"C:\\.\"", "drive root safe");
        Check(ServerTools.BuildSteamCmdUpdateArgs(@"D:\RS\server\", false) ==
              "+force_install_dir \"D:\\RS\\server\" +login anonymous +app_update 1874900 validate +quit", "steamcmd stable");
        Check(ServerTools.BuildSteamCmdUpdateArgs(@"D:\x", true, validate: false).Contains("+app_update 1890870 +quit"), "steamcmd experimental");

        var args = ServerTools.BuildServerArgs(Path.Combine(_tmp, "c.json"), Path.Combine(_tmp, "p"), 60, " -logStats 60000 ");
        Check(args.StartsWith("-config \"") && args.Contains(" -profile \"") && args.EndsWith(" -maxFPS 60 -logStats 60000"), "server args: " + args);
        Check(!ServerTools.BuildServerArgs("c.json", "p", 0, null).Contains("maxFPS"), "maxFPS 0 omitted");
        Check(ServerTools.BuildServerArgs("c.json", "p", 60, null).EndsWith(" -maxFPS 60 -logStats 10000") &&
              ServerTools.BuildServerArgs("c.json", "p", 60, "").EndsWith("-logStats 10000"), "FPS stats on by default");
        Check(System.Text.RegularExpressions.Regex.Matches(args, "-logStats").Count == 1, "user's own -logStats wins");

        Check(ServerTools.InstallPathWarnings(@"C:\ReforgerServer\steamcmd", @"C:\ReforgerServer\server").Count == 0, "good paths: no warnings");
        var w = ServerTools.InstallPathWarnings(@"C:\Users\a\OneDrive\ドキュメント\server", @"C:\Users\a\OneDrive\ドキュメント\server\");
        Check(w.Count == 3 && w.Any(x => x.Contains("같습니다")) && w.Any(x => x.Contains("OneDrive")) && w.Any(x => x.Contains("영문")), "bad paths: " + string.Join(" / ", w));

        var s = new ServerSettings { RconEnabled = true };
        var fw = ServerTools.BuildFirewallCommand(ServerTools.PortsToOpen(s));
        Check(fw.Contains("protocol=UDP localport=2001,17777,19999") && fw.Contains("delete rule") && fw.StartsWith("/c "), "firewall: " + fw);
    }

    private void ScenarioParsing()
    {
        var ids = ScenarioCatalog.ParseListOutput(new[]
        {
            "12:00:00 SCRIPT : Scenarios:",
            "  {ECC61978EDCC2B5A}Missions/23_Campaign.conf (Conflict - Everon)",
            "  \"{ABCDEF0123456789}Missions/My Mod/Coop_Night.conf\"",
            "  {ECC61978EDCC2B5A}Missions/23_Campaign.conf",
            "  {59AD59368755F41A}Missions/21_GM_Eden.conf,{2BBBE828037C6F4B}Missions/22_GM_Arland.conf",
        });
        Check(ids.Count == 4, "found " + ids.Count + ": " + string.Join(" | ", ids));
        Check(ids.Contains("{ABCDEF0123456789}Missions/My Mod/Coop_Night.conf"), "path with space");
        Check(ScenarioCatalog.NameFromId("{ABCDEF0123456789}Missions/Coop_Night.conf") == "Coop_Night", "name from id");
        Check(ScenarioCatalog.Official.Select(o => o.Id).Distinct().Count() == ScenarioCatalog.Official.Count, "no duplicate official ids");
        Check(ScenarioCatalog.Official.All(o => ScenarioCatalog.ParseListOutput(new[] { o.Id }).Single() == o.Id), "official ids well-formed");
    }

    private void AppSettingsRoundTrip()
    {
        var path = Path.Combine(_tmp, "app", "settings.json");
        var store = new AppSettingsStore(path);
        Check(store.Load().MaxFps == 60, "defaults when missing");
        store.Save(new AppSettings { ServerDir = @"E:\서버", MaxFps = 90 });
        var loaded = store.Load();
        Check(loaded.ServerDir == @"E:\서버" && loaded.MaxFps == 90, "round trip");
        File.WriteAllText(path, "{ broken");
        Check(store.Load().MaxFps == 60, "broken file → defaults");
    }

    //------------------------------------------------------------------------------------------
    private async Task ProcessRunnerCapturesOutput()
    {
        var script = WriteScript("echo.sh", "echo 하나\necho 둘 1>&2\nexit 3\n");
        using var r = new ProcessRunner();
        var lines = new List<string>();
        var exitedEvent = -1;
        r.Output += l => { lock (lines) lines.Add(l); };
        r.Exited += c => exitedEvent = c;
        r.Start(script, "", _tmp);
        var code = await r.WaitForExitAsync();
        Check(code == 3 && exitedEvent == 3, "exit code");
        Check(lines.Contains("하나") && lines.Contains("둘"), "stdout + stderr, UTF-8: [" + string.Join("|", lines) + "] exitedEvent=" + exitedEvent);
        Check(!r.IsRunning, "not running after exit");
    }

    private async Task ViewModelEndToEnd()
    {
        var root = Path.Combine(_tmp, "vm");
        var steamDir = Path.Combine(root, "steamcmd");
        var serverDir = Path.Combine(root, "server");
        var configPath = Path.Combine(serverDir, "Configs", "server.json");
        Directory.CreateDirectory(steamDir);

        // fake SteamCMD: installs a fake server into the +force_install_dir folder
        var fakeServer = """
            #!/bin/sh
            case "$*" in
              *-listScenarios*)
                echo "SCRIPT : {ECC61978EDCC2B5A}Missions/23_Campaign.conf"
                echo "SCRIPT : {ABCDEF0123456789}Missions/Workshop_Coop.conf"
                echo "DEFAULT : Entered online game state."
                sleep 30 ;;
              *)
                echo "server args: $*"
                sleep 30 ;;
            esac
            """;
        File.WriteAllText(Path.Combine(root, "fake_server.sh"), fakeServer.Replace("\r", ""));
        WriteScript(Path.Combine(steamDir, "steamcmd.exe"), $"""
            dir=""
            prev=""
            for a in "$@"; do
              [ "$prev" = "+force_install_dir" ] && dir="$a"
              prev="$a"
            done
            echo "Update state (0x61) downloading"
            mkdir -p "$dir"
            cp "{Path.Combine(root, "fake_server.sh")}" "$dir/ArmaReforgerServer"
            chmod +x "$dir/ArmaReforgerServer"
            echo "Success! App '1874900' fully installed."
            """);

        var store = new AppSettingsStore(Path.Combine(root, "app.json"));
        store.Save(new AppSettings
        {
            SteamCmdDir = steamDir,
            ServerDir = serverDir,
            ConfigPath = configPath,
            ProfileDir = Path.Combine(serverDir, "profile"),
            MaxFps = 60,
        });

        var ui = new FakeUi();
        var vm = new MainViewModel(ui, null, store, FakeWorkshop.Client(new()));
        Check(!vm.ServerInstalled && vm.InstallButtonText == "서버 설치", "starts uninstalled");
        Check(!vm.IsDirty && vm.ConfigPreview.Contains("23_Campaign"), "clean start with preview");
        Check(vm.SelectedScenario?.DisplayName == "컨플릭트 - 에버론", "default scenario selected");

        // install
        await vm.InstallOrUpdateCommand.ExecuteAsync();
        Check(vm.ServerInstalled && vm.InstallButtonText == "서버 업데이트", "installed: " + string.Join("\n", vm.LogLines.TakeLast(5)));
        Check(vm.LogLines.Any(l => l.Contains("설치/업데이트가 끝났습니다")), "install success logged");
        Check(!vm.IsBusy, "not busy after install");

        // edit
        vm.ServerName = "테스트 서버";
        Check(vm.IsDirty && vm.WindowTitle.EndsWith(" *") && vm.ConfigPreview.Contains("테스트 서버"), "dirty + preview updates");
        vm.SelectedScenario = vm.Scenarios.First(s => s.Id.Contains("21_GM_Eden"));
        Check(vm.ScenarioId.Contains("21_GM_Eden"), "picking a scenario sets the id");
        vm.ScenarioId = "{ECC61978EDCC2B5A}Missions/23_Campaign.conf";
        Check(vm.SelectedScenario?.Id == vm.ScenarioId, "typing a known id selects it");
        vm.AdminsText = "76561198000000001\r\n76561198000000002, 76561198000000001";
        Check(vm.ToSettings().Admins.Count == 2, "admins split + deduped");

        // mods
        vm.ModImportText = "{ \"modId\": \"5965550F24A0C152\", \"name\": \"Where Am I\" },";
        vm.ImportModsCommand.Execute(null);
        Check(vm.Mods.Count == 1 && vm.ModImportText == "", "import from text box");
        ui.Clipboard = "591AF5BDA9F7CE8B";
        vm.ImportModsCommand.Execute(null);
        Check(vm.Mods.Count == 2, "import from clipboard when box empty");
        vm.NewModId = "https://reforger.armaplatform.com/workshop/0123456789ABCDEF-Thing";
        vm.NewModName = "Thing";
        vm.AddModCommand.Execute(null);
        Check(vm.Mods.Count == 3 && vm.Mods[2].Name == "Thing" && vm.NewModId == "", "add by link");
        vm.SelectedMod = vm.Mods[2];
        Check(vm.RemoveModCommand.CanExecute(null) && vm.OpenWorkshopCommand.CanExecute(null), "selection enables commands");
        vm.OpenWorkshopCommand.Execute(null);
        Check(ui.Opened.LastOrDefault() == "https://reforger.armaplatform.com/workshop/0123456789ABCDEF", "workshop link");
        vm.RemoveModCommand.Execute(null);
        Check(vm.Mods.Count == 2, "remove");
        vm.CopyModsJsonCommand.Execute(null);
        Check(ui.Clipboard.Contains("\"mods\"") && ModListParser.Parse(ui.Clipboard).Count == 2, "copy json round-trips");
        vm.Mods[0].Version = "9.9.9";
        Check(vm.ConfigPreview.Contains("9.9.9"), "editing a mod in the grid updates preview");

        // invalid settings block saving
        vm.MaxPlayers = 0;
        Check(!vm.TrySaveConfig(true) && ui.Errors.Last().Contains("최대 인원") && !File.Exists(configPath), "validation blocks save");
        vm.MaxPlayers = 40;

        // save
        Check(vm.TrySaveConfig(true) && File.Exists(configPath) && !vm.IsDirty, "saved");
        var saved = ServerConfigJson.Read(ServerConfigJson.Load(configPath));
        Check(saved.ServerName == "테스트 서버" && saved.MaxPlayers == 40 && saved.Mods.Count == 2, "saved content");

        // a hand-added key survives a reload + save from the program
        var json = ServerConfigJson.Load(configPath);
        json["operating"] = new JsonObject { ["playerSaveTime"] = 99 };
        ServerConfigJson.Save(configPath, json);
        var vm2 = new MainViewModel(ui, null, store, FakeWorkshop.Client(new()));
        Check(vm2.ServerName == "테스트 서버" && vm2.Mods.Count == 2 && !vm2.IsDirty, "second instance loads saved config");
        vm2.Password = "pw";
        Check(vm2.TrySaveConfig(false), "save 2");
        Check(ServerConfigJson.Load(configPath)["operating"]?["playerSaveTime"]?.GetValue<int>() == 99, "hand-added key kept");

        // scenario list from the (fake) server, started with the config so mods load; stops once the server goes online
        var before = vm.Scenarios.Count;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await vm.ListScenariosCommand.ExecuteAsync();
        Check(sw.Elapsed < TimeSpan.FromSeconds(20), "list scenarios did not wait for the 30 s sleep");
        Check(vm.LogLines.Any(l => l.Contains("-config ") && l.Contains("-listScenarios")), "listed with the config (mods loaded)");
        Check(vm.Scenarios.Count == before + 1 && vm.Scenarios.Last().DisplayName == "[서버] Workshop_Coop", "workshop scenario added");

        // start / stop
        vm.StartServerCommand.Execute(null);
        Check(vm.IsServerRunning && vm.WindowTitle.Contains("서버 실행 중"), "running");
        Check(!vm.StartServerCommand.CanExecute(null) && vm.StopServerCommand.CanExecute(null) && !vm.InstallOrUpdateCommand.CanExecute(null), "buttons follow state");
        for (var i = 0; i < 50 && !vm.LogLines.Any(l => l.Contains("server args:")); i++)
            await Task.Delay(100);
        var argLine = vm.LogLines.FirstOrDefault(l => l.Contains("server args:")) ?? "";
        Check(argLine.Contains("-config " + configPath) && argLine.Contains("-maxFPS 60"), "server got args: " + argLine);

        vm.StopServerCommand.Execute(null);
        for (var i = 0; i < 50 && vm.IsServerRunning; i++)
            await Task.Delay(100);
        Check(!vm.IsServerRunning && vm.LogLines.Any(l => l.Contains("서버가 종료되었습니다")), "stopped");

        // close: unsaved changes → ask; Cancel keeps the window open
        vm.ServerName = "바뀜";
        ui.YesNoCancelAnswer = null;
        Check(!vm.ConfirmClose(), "cancel keeps window");
        ui.YesNoCancelAnswer = false;
        Check(vm.ConfirmClose(), "no = close without saving");
        vm.Shutdown();

        // firewall
        vm.AddFirewallRuleCommand.Execute(null);
        Check(ui.Elevated.LastOrDefault().exe == "cmd.exe" && ui.Elevated.Last().args.Contains("localport=2001,17777"), "firewall elevated command");
    }


    //------------------------------------------------------------------------------------------
    // Workshop pages, shaped like reforger.armaplatform.com/workshop/<id> (checked 2026-10-29)

    private void WorkshopPageParsing()
    {
        var stryker = WorkshopPage.Parse(FakeWorkshop.Page("5B02128D896F7DE8", "WCS_Stryker", "8.4.0",
            ("5E389BB9F58B79A6", "WCS_SpaceCore", "8.4.0", null),
            ("629B2BA37EFFD577", "WCS_Armaments", "8.4.0", new[] { ("5E389BB9F58B79A6", "WCS_SpaceCore"), ("1111111111111111", "Nested_Lib") })));
        Check(stryker != null && stryker.Name == "WCS_Stryker" && stryker.Version == "8.4.0", "asset read");
        Check(stryker!.Dependencies.Select(d => d.Name).SequenceEqual(new[] { "WCS_SpaceCore", "WCS_Armaments", "Nested_Lib" }), "direct + nested, deduped: " + string.Join(",", stryker.Dependencies.Select(d => d.Name)));
        Check(stryker.Dependencies[0].Version == "8.4.0", "dependency version");

        Check(WorkshopPage.Parse("<html><script id=\"__NEXT_DATA__\" type=\"application/json\">{\"props\":{\"pageProps\":{\"asset\":null}}}</script></html>") == null, "no asset → null");
        var threw = false;
        try { WorkshopPage.Parse("<html>maintenance</html>"); } catch (FormatException) { threw = true; }
        Check(threw, "unknown page → FormatException");

        var link = ModListParser.Parse("https://reforger.armaplatform.com/workshop/5B02128D896F7DE8-WCS_Stryker\n5E389BB9F58B79A6\nhttps://reforger.armaplatform.com/workshop/629B2BA37EFFD577-My%20Mod?x=1");
        Check(link.Count == 3 && link[0].Name == "WCS_Stryker" && link[1].Name == "" && link[2].Name == "My Mod", "names from links: " + string.Join("|", link.Select(l => l.Name)));
    }

    private async Task WorkshopDependencyWalk()
    {
        var site = FakeWorkshop.Site();
        var client = FakeWorkshop.Client(site);
        var r = await client.ResolveAsync(new[] { "5B02128D896F7DE8", "DEADBEEFDEADBEEF" });
        Check(r.Mods.ContainsKey("5B02128D896F7DE8") && r.Mods.ContainsKey("629B2BA37EFFD577"), "dependency pages fetched too");
        Check(r.Required.Select(x => x.Name).SequenceEqual(new[] { "WCS_SpaceCore", "WCS_Armaments", "BGONE" }), "found through the chain: " + string.Join(",", r.Required.Select(x => x.Name)));
        Check(r.Required.Single(x => x.Name == "BGONE").RequiredBy == "WCS_Armaments", "who needs it");
        Check(r.Failed.SequenceEqual(new[] { "DEADBEEFDEADBEEF" }), "unknown id reported as failed");

        var before = site.Requests;
        await client.ResolveAsync(new[] { "5B02128D896F7DE8" });
        Check(site.Requests == before, "second walk served from cache");

        var r2 = await client.ResolveAsync(new[] { "5B02128D896F7DE8", "629B2BA37EFFD577" });
        Check(r2.Required.Single(x => x.Id == "629B2BA37EFFD577").RequiredBy == "WCS_Stryker", "a starting mod another one needs is still reported");
    }

    private async Task ViewModelDependencies()
    {
        var store = new AppSettingsStore(Path.Combine(_tmp, "deps", "app.json"));
        store.Save(new AppSettings { ServerDir = Path.Combine(_tmp, "deps", "server"), ConfigPath = Path.Combine(_tmp, "deps", "server.json") });

        // yes: dependencies are added, with names
        var ui = new FakeUi { ConfirmAnswer = true };
        var vm = new MainViewModel(ui, null, store, FakeWorkshop.Client(FakeWorkshop.Site()));
        vm.NewModId = "https://reforger.armaplatform.com/workshop/5B02128D896F7DE8-WCS_Stryker";
        vm.AddModCommand.Execute(null);
        Check(vm.Mods[0].ModId == "5B02128D896F7DE8" && vm.Mods[0].Name == "WCS_Stryker", "added from link with its name");
        await vm.LastDependencyCheck;
        Check(ui.Confirms.Count == 1 && ui.Confirms[0].Contains("3개") && ui.Confirms[0].Contains("BGONE") && ui.Confirms[0].Contains("WCS_Armaments에 필요"), "asked once, listing all: " + ui.Confirms.FirstOrDefault());
        Check(vm.Mods.Select(m => m.Name).SequenceEqual(new[] { "WCS_Stryker", "WCS_SpaceCore", "WCS_Armaments", "BGONE" }), "dependencies added: " + string.Join(",", vm.Mods.Select(m => m.Name)));
        Check(vm.Mods.All(m => m.Version == ""), "dependencies follow latest version");
        Check(!vm.IsCheckingDependencies, "check finished");

        // check all, nothing missing → just says so
        await vm.CheckDependenciesCommand.ExecuteAsync();
        Check(ui.Confirms.Count == 1 && ui.Infos.Last().Contains("모두 목록에 있습니다"), "manual check, all present");

        // remove one, check again → offers it back
        vm.Mods.RemoveAt(3);
        await vm.CheckDependenciesCommand.ExecuteAsync();
        Check(ui.Confirms.Count == 2 && vm.Mods.Any(m => m.Name == "BGONE"), "manual check re-adds missing");

        // no: nothing added, log says what is missing
        var ui2 = new FakeUi { ConfirmAnswer = false };
        var vm2 = new MainViewModel(ui2, null, store, FakeWorkshop.Client(FakeWorkshop.Site()));
        vm2.ModImportText = "5B02128D896F7DE8";
        vm2.ImportModsCommand.Execute(null);
        await vm2.LastDependencyCheck;
        Check(vm2.Mods.Count == 1 && vm2.Mods[0].Name == "WCS_Stryker", "declined: only the mod, but its name filled from the workshop");
        Check(vm2.LogLines.Any(l => l.Contains("추가하지 않았습니다") && l.Contains("BGONE")), "log lists what is missing");

        // offline: the mod is still added, the log explains
        var ui3 = new FakeUi();
        var vm3 = new MainViewModel(ui3, null, store, FakeWorkshop.Client(new FakeWorkshop.FakeSite { Offline = true }));
        vm3.NewModId = "5B02128D896F7DE8";
        vm3.AddModCommand.Execute(null);
        await vm3.LastDependencyCheck;
        Check(vm3.Mods.Count == 1 && ui3.Confirms.Count == 0 && vm3.LogLines.Any(l => l.Contains("가져오지 못한 모드")), "offline handled");
    }


    private void WorkshopListParsing()
    {
        Check(WorkshopPage.BuildListUrl(null, null, "popularity", 1) == "https://reforger.armaplatform.com/workshop?page=1&sort=popularity", "plain list url");
        Check(WorkshopPage.BuildListUrl(" WCS 차량 ", "VEHICLES", "newest", 3) ==
              "https://reforger.armaplatform.com/workshop?page=3&sort=newest&search=WCS%20%EC%B0%A8%EB%9F%89&tags=VEHICLES", "search + tag url");

        var page = WorkshopPage.ParseList(FakeWorkshop.ListPage(53255, new[]
        {
            FakeWorkshop.Row("5965550F24A0C152", "Where Am I", "ValterB", new[] { "EASY", "GPS", "MISC" }, 201671, 66686, 0.97),
            FakeWorkshop.Row("5B02128D896F7DE8", "WCS_Stryker", "Worst Case Scenario", new[] { "VEHICLES", "WEAPONS" }, 45273000, 40000, 0.9),
        }));
        Check(page.TotalCount == 53255 && page.Items.Count == 2, "count + rows");
        var a = page.Items[0];
        Check(a.Name == "Where Am I" && a.Author == "ValterB" && a.SizeBytes == 201671 && a.Subscribers == 66686 && Math.Abs(a.Rating - 0.97) < 1e-9, "row fields");
        Check(a.ThumbnailUrl == "https://cdn.example/288.jpg", "smallest thumbnail at least 280px: " + a.ThumbnailUrl);

        var r = new WorkshopResult(page.Items[1], false, _ => { }, _ => { });
        Check(r.CategoriesText == "무기 · 차량", "categories in Korean, catalog order: " + r.CategoriesText);
        Check(r.SizeText == "43.2 MB" && r.SubscribersText == "구독 40,000" && r.RatingText == "평점 90%", $"formatting: {r.SizeText} / {r.SubscribersText} / {r.RatingText}");
        Check(new WorkshopResult(page.Items[0], false, _ => { }, _ => { }).CategoriesText == "기타", "only category tags shown");
        Check(WorkshopResult.FormatSize(512 * 1024) == "512 KB" && WorkshopResult.FormatSize(3L * 1024 * 1024 * 1024) == "3 GB", "size units");

        var threw = false;
        try { WorkshopPage.ParseList("<html></html>"); } catch (FormatException) { threw = true; }
        Check(threw, "unknown page → FormatException");
    }

    private async Task WorkshopBrowsing()
    {
        var site = FakeWorkshop.Site();
        FakeWorkshop.AddListing(site, 40);
        var store = new AppSettingsStore(Path.Combine(_tmp, "browse", "app.json"));
        store.Save(new AppSettings { ServerDir = Path.Combine(_tmp, "browse", "server"), ConfigPath = Path.Combine(_tmp, "browse", "server.json") });
        var ui = new FakeUi { ConfirmAnswer = true };
        var vm = new MainViewModel(ui, null, store, FakeWorkshop.Client(site));
        var b = vm.Browser;

        Check(b.Groups.Count == 0 && !b.HasLoaded, "nothing loaded before the page is opened");
        await b.EnsureLoadedAsync();
        Check(b.Groups.Count == 1 && b.Groups[0].Title == "전체 모드" && b.Groups[0].Items.Count == 16 && b.Groups[0].TotalCount == 40, "first open: all mods, one page");
        Check(b.Groups[0].HasMore && b.Groups[0].LoadMoreCommand!.CanExecute(null), "more available");
        await b.Groups[0].LoadMoreCommand!.ExecuteAsync();
        await b.Groups[0].LoadMoreCommand!.ExecuteAsync();
        Check(b.Groups[0].Items.Count == 40 && !b.Groups[0].HasMore, "paged to the end: " + b.Groups[0].Items.Count);
        var before = site.Requests;
        await b.EnsureLoadedAsync();
        Check(site.Requests == before, "opening the page again does not reload");

        // categories: one section each, in catalog order
        b.Categories.First(c => c.Tag == "VEHICLES").IsChecked = true;
        b.Categories.First(c => c.Tag == "WEAPONS").IsChecked = true;
        await b.LastRefresh;
        Check(b.Groups.Select(g => g.Title).SequenceEqual(new[] { "무기", "차량" }), "split by category: " + string.Join(",", b.Groups.Select(g => g.Title)));
        Check(b.Groups.All(g => g.Items.All(i => i.Item.Tags.Contains(g.Tag!))), "each section only has its category");
        Check(b.Groups[0].TotalCount == 25 && b.Groups[1].TotalCount == 14, $"per-category counts {b.Groups[0].TotalCount}/{b.Groups[1].TotalCount}");
        Check(b.AnyCategoryChecked && b.ClearCategoriesCommand.CanExecute(null), "clear enabled");

        // search inside categories
        b.SearchText = "Mod 1";
        await b.SearchCommand.ExecuteAsync();
        Check(b.Groups.All(g => g.Items.All(i => i.Name.Contains("Mod 1"))), "search applied to every section");

        // nothing found
        b.SearchText = "zzz";
        await b.SearchCommand.ExecuteAsync();
        Check(b.Groups.All(g => g.IsEmpty && g.Items.Count == 0), "empty sections say so");

        // back to everything
        b.SearchText = "";
        b.ClearCategoriesCommand.Execute(null);
        await b.LastRefresh;
        Check(b.Groups.Count == 1 && b.Groups[0].Title == "전체 모드" && !b.AnyCategoryChecked, "cleared → all mods again");

        // add from a card → in the list, marked, dependencies offered
        var card = b.Groups[0].Items.First(i => i.Id == "5B02128D896F7DE8");
        card.AddCommand.Execute(null);
        await vm.LastDependencyCheck;
        Check(vm.Mods.Any(m => m.ModId == "5B02128D896F7DE8" && m.Name == "WCS_Stryker"), "added to server mods");
        Check(card.IsAdded && !card.AddCommand.CanExecute(null), "card shows 추가됨");
        Check(vm.Mods.Count == 4 && ui.Confirms.Count == 1, "dependencies offered and added: " + vm.Mods.Count);
        var bgone = b.Groups[0].Items.FirstOrDefault(i => i.Id == "5A1B2C3D4E5F6071");
        Check(bgone == null || bgone.IsAdded, "a dependency that is on screen is marked too");

        // removing it from the mod list un-marks the card
        vm.Mods.Remove(vm.Mods.First(m => m.ModId == "5B02128D896F7DE8"));
        Check(!card.IsAdded && card.AddCommand.CanExecute(null), "un-marked after removal");

        card.OpenCommand.Execute(null);
        Check(ui.Opened.Last() == "https://reforger.armaplatform.com/workshop/5B02128D896F7DE8", "open on workshop");

        // offline: error shown in the section, no crash
        var vm2 = new MainViewModel(new FakeUi(), null, store, FakeWorkshop.Client(new FakeWorkshop.FakeSite { Offline = true }));
        await vm2.Browser.EnsureLoadedAsync();
        Check(vm2.Browser.Groups[0].HasError && vm2.Browser.Groups[0].ErrorText.Contains("인터넷"), "offline message");
    }


    private async Task ModSwitching()
    {
        // config.json gets switched-on mods only
        var st = new ServerSettings();
        st.Mods.Add(new ModEntry("5965550F24A0C152", "A"));
        st.Mods.Add(new ModEntry("591AF5BDA9F7CE8B", "B", "", isEnabled: false));
        var root = new JsonObject();
        ServerConfigJson.Write(st, root);
        Check(ServerConfigJson.Read(root).Mods.Select(m => m.ModId).SequenceEqual(new[] { "5965550F24A0C152" }), "only enabled in config");

        // side file round trip, and removed when nothing is off
        var cfg = Path.Combine(_tmp, "switch", "Configs", "server.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
        DisabledModsFile.Save(cfg, st.Mods);
        Check(File.Exists(Path.Combine(_tmp, "switch", "Configs", "server.disabled-mods.json")), "side file next to config");
        var off = DisabledModsFile.Load(cfg);
        Check(off.Count == 1 && off[0].ModId == "591AF5BDA9F7CE8B" && off[0].Name == "B" && !off[0].IsEnabled, "side file read back as off");
        DisabledModsFile.Save(cfg, new[] { new ModEntry("5965550F24A0C152") });
        Check(!File.Exists(DisabledModsFile.PathFor(cfg)), "side file removed when all on");

        // view model
        var store = new AppSettingsStore(Path.Combine(_tmp, "switch", "app.json"));
        store.Save(new AppSettings { ServerDir = Path.Combine(_tmp, "switch", "server"), ConfigPath = cfg });
        var ui = new FakeUi { ConfirmAnswer = true };
        var vm = new MainViewModel(ui, null, store, FakeWorkshop.Client(FakeWorkshop.Site()));
        vm.ModImportText = "5B02128D896F7DE8";
        vm.ImportModsCommand.Execute(null);
        await vm.LastDependencyCheck;
        Check(vm.Mods.Count == 4 && vm.ModsBadgeText == "4" && vm.ModCountText == "활성 4개 / 전체 4개", "four mods on: " + vm.ModsBadgeText);
        Check(!vm.EnableAllModsCommand.CanExecute(null) && vm.DisableAllModsCommand.CanExecute(null), "bulk buttons follow state");

        var bgone = vm.Mods.Single(m => m.Name == "BGONE");
        bgone.IsEnabled = false;
        Check(vm.ModsBadgeText == "3/4" && vm.ModCountText == "활성 3개 / 전체 4개", "badge counts switched-on: " + vm.ModsBadgeText);
        Check(vm.IsDirty && !vm.ConfigPreview.Contains("5A1B2C3D4E5F6071") && vm.EnableAllModsCommand.CanExecute(null), "switched off: out of preview, dirty");

        // starting would now warn (known from the dependency check)
        var problems = vm.MissingKnownDependencies();
        Check(problems.Count == 1 && problems[0].Contains("WCS_Armaments에 필요한 BGONE") && problems[0].Contains("비활성"), "start warning: " + string.Join(" | ", problems));

        // save → config without it, side file with it; reload keeps it switched off
        Check(vm.TrySaveConfig(false), "saved");
        var saved = ServerConfigJson.Read(ServerConfigJson.Load(cfg));
        Check(saved.Mods.Count == 3 && saved.Mods.All(m => m.ModId != "5A1B2C3D4E5F6071"), "config without the off mod");
        var vm2 = new MainViewModel(new FakeUi(), null, store, FakeWorkshop.Client(FakeWorkshop.Site()));
        Check(vm2.Mods.Count == 4 && !vm2.Mods.Single(m => m.Name == "BGONE").IsEnabled && !vm2.IsDirty, "reloaded with BGONE still off");
        Check(vm2.LogLines.Any(l => l.Contains("비활성 모드 1개")), "load log mentions off mods");

        // manual dependency check offers to switch it back on
        await vm.CheckDependenciesCommand.ExecuteAsync();
        Check(ui.Confirms.Last().Contains("비활성화된 필요 모드가 1개") && ui.Confirms.Last().Contains("같이 활성화할까요?") && bgone.IsEnabled, "check switches needed mod back on");
        Check(vm.MissingKnownDependencies().Count == 0, "no start warning after fixing");

        // all off / all on
        vm.DisableAllModsCommand.Execute(null);
        Check(vm.Mods.All(m => !m.IsEnabled) && vm.ModsBadgeText == "0/4" && !vm.CheckDependenciesCommand.CanExecute(null), "all off");
        Check(vm.TrySaveConfig(false) && ServerConfigJson.Read(ServerConfigJson.Load(cfg)).Mods.Count == 0 && DisabledModsFile.Load(cfg).Count == 4, "all off saved");
        vm.EnableAllModsCommand.Execute(null);
        Check(vm.Mods.All(m => m.IsEnabled) && vm.ModsBadgeText == "4", "all on");
        Check(vm.TrySaveConfig(false) && !File.Exists(DisabledModsFile.PathFor(cfg)), "all on saved, side file gone");

        // copy JSON copies switched-on mods only
        vm.Mods[0].IsEnabled = false;
        vm.CopyModsJsonCommand.Execute(null);
        var fakeUi = (FakeUi)typeof(MainViewModel).GetField("_ui", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(vm)!;
        Check(ModListParser.Parse(fakeUi.Clipboard).Count == 3, "copy only switched-on");
    }


    private void ModListFileFormats()
    {
        var now = new DateTime(2026, 10, 30, 0, 50, 0);
        var mods = new List<ModEntry>
        {
            new("5B02128D896F7DE8", "WCS_Stryker", "", true),
            new("5A1B2C3D4E5F6071", "BGONE | 테스트", "1.0.2", false),
        };

        var txt = ModListFile.ToText(mods, now);
        Check(txt.Contains("5B02128D896F7DE8 | WCS_Stryker | 최신 | 활성") && txt.Contains("5A1B2C3D4E5F6071 | BGONE / 테스트 | 1.0.2 | 비활성"), "text lines:\n" + txt);
        var fromTxt = ModListFile.Parse(txt);
        Check(fromTxt.Count == 2 && fromTxt[0].Version == "" && fromTxt[0].IsEnabled && fromTxt[1].Version == "1.0.2" && !fromTxt[1].IsEnabled && fromTxt[1].Name == "BGONE / 테스트", "text round trip");

        var json = ModListFile.ToJson(mods, now);
        var fromJson = ModListFile.Parse(json);
        Check(json.Contains("\"format\": \"rsm-mod-list\"") && json.Contains("테스트"), "json readable");
        Check(fromJson.Count == 2 && fromJson[1].Name == "BGONE | 테스트" && !fromJson[1].IsEnabled && fromJson[0].IsEnabled, "json round trip keeps active flags");

        // other things that name mods still work
        Check(ModListFile.Parse("{\"game\":{\"mods\":[{\"modId\":\"5965550F24A0C152\",\"name\":\"x\"}]}}").Single().IsEnabled, "config.json accepted");
        Check(ModListFile.Parse("https://reforger.armaplatform.com/workshop/5965550F24A0C152-WhereAmI").Single().Name == "WhereAmI", "links accepted");
        Check(ModListFile.Parse("﻿# only comments\n").Count == 0, "comments only → nothing");
        Check(ModListFile.DefaultFileName(now) == "모드목록_2026-10-30.json", "default file name");

        // file on disk, with BOM for Notepad
        var path = Path.Combine(_tmp, "list", "mods.txt");
        ModListFile.Save(path, mods, now);
        var bytes = File.ReadAllBytes(path);
        Check(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "txt saved with BOM");
        Check(ModListFile.Load(path).Count == 2, "load from disk");
    }

    private async Task ModListMove()
    {
        // PC 1: a list with one mod switched off, saved to a file
        var oldCfg = Path.Combine(_tmp, "pc1", "server", "Configs", "server.json");
        Directory.CreateDirectory(Path.GetDirectoryName(oldCfg)!);
        var store1 = new AppSettingsStore(Path.Combine(_tmp, "pc1", "app.json"));
        store1.Save(new AppSettings { ServerDir = Path.Combine(_tmp, "pc1", "server"), ConfigPath = oldCfg });
        var ui1 = new FakeUi { ConfirmAnswer = true };
        var vm1 = new MainViewModel(ui1, null, store1, FakeWorkshop.Client(FakeWorkshop.Site()));
        Check(!vm1.ExportModListCommand.CanExecute(null), "nothing to save yet");
        vm1.ModImportText = "5B02128D896F7DE8";
        vm1.ImportModsCommand.Execute(null);
        await vm1.LastDependencyCheck;
        vm1.Mods.Single(m => m.Name == "WCS_Armaments").IsEnabled = false;

        foreach (var ext in new[] { ".json", ".txt" })
        {
            var file = Path.Combine(_tmp, "usb", "모드목록" + ext);
            ui1.SaveFileResult = file;
            vm1.ExportModListCommand.Execute(null);
            Check(File.Exists(file), "saved " + ext);
            Check(ui1.LastSaveInitialPath!.EndsWith(".json") && ui1.LastSaveInitialPath.Contains("Configs"), "suggests a dated file next to the config: " + ui1.LastSaveInitialPath);

            // PC 2: fresh program, empty list → load replaces without asking
            var store2 = new AppSettingsStore(Path.Combine(_tmp, "pc2" + ext, "app.json"));
            store2.Save(new AppSettings { ServerDir = Path.Combine(_tmp, "pc2" + ext, "server"), ConfigPath = Path.Combine(_tmp, "pc2" + ext, "server.json") });
            var ui2 = new FakeUi { ConfirmAnswer = true, OpenFileResult = file };
            var vm2 = new MainViewModel(ui2, null, store2, FakeWorkshop.Client(FakeWorkshop.Site()));
            vm2.ImportModListCommand.Execute(null);
            await vm2.LastDependencyCheck;
            Check(vm2.Mods.Count == 4 && vm2.EnabledModCount == 3, $"{ext}: all mods back, 3 active: {vm2.Mods.Count}/{vm2.EnabledModCount}");
            Check(vm2.Mods.Select(m => m.Name).SequenceEqual(vm1.Mods.Select(m => m.Name)), ext + ": same order and names");
            Check(!vm2.Mods.Single(m => m.Name == "WCS_Armaments").IsEnabled, ext + ": inactive mod stays inactive");
            Check(ui2.Infos.Any(i => i.Contains("서버를 시작하면")) && vm2.IsDirty, ext + ": tells to save + start; marked unsaved");
            Check(vm2.TrySaveConfig(false) && ServerConfigJson.Read(ServerConfigJson.Load(Path.Combine(_tmp, "pc2" + ext, "server.json"))).Mods.Count == 3, ext + ": saved config has the 3 active mods");
        }

        // loading into a non-empty list: Cancel = nothing, No = add, Yes = replace
        var ui3 = new FakeUi { OpenFileResult = Path.Combine(_tmp, "usb", "모드목록.json") };
        var store3 = new AppSettingsStore(Path.Combine(_tmp, "pc3", "app.json"));
        store3.Save(new AppSettings { ServerDir = Path.Combine(_tmp, "pc3", "server"), ConfigPath = Path.Combine(_tmp, "pc3", "server.json") });
        var vm3 = new MainViewModel(ui3, null, store3, FakeWorkshop.Client(FakeWorkshop.Site()));
        vm3.Mods.Add(new ModEntry("6A1EC94627CDE7E7", "기존 모드"));
        ui3.YesNoCancelAnswer = null;
        vm3.ImportModListCommand.Execute(null);
        Check(vm3.Mods.Count == 1, "cancel keeps the list");
        ui3.YesNoCancelAnswer = false;
        ui3.ConfirmAnswer = false;
        vm3.ImportModListCommand.Execute(null);
        await vm3.LastDependencyCheck;
        Check(vm3.Mods.Count == 5 && vm3.Mods[0].Name == "기존 모드", "no = add to the list: " + vm3.Mods.Count);
        ui3.YesNoCancelAnswer = true;
        vm3.ImportModListCommand.Execute(null);
        await vm3.LastDependencyCheck;
        Check(vm3.Mods.Count == 4 && vm3.Mods.All(m => m.Name != "기존 모드"), "yes = replace the list");

        // a file with no mods
        var junk = Path.Combine(_tmp, "usb", "junk.txt");
        File.WriteAllText(junk, "hello");
        ui3.OpenFileResult = junk;
        vm3.ImportModListCommand.Execute(null);
        Check(ui3.Errors.Last().Contains("모드를 찾지 못했습니다") && vm3.Mods.Count == 4, "junk file rejected");
    }


    private async Task A2sQueries()
    {
        using var server = new FakeA2sServer("한국 리포저 서버", "Everon", 64, new[] { ("철수", 120f), ("영희", 3725f) });
        var a2s = new A2sClient { Timeout = TimeSpan.FromSeconds(2) };
        var info = await a2s.QueryInfoAsync("127.0.0.1", server.Port);
        Check(info != null && info.Name == "한국 리포저 서버" && info.Map == "Everon" && info.Players == 2 && info.MaxPlayers == 64, "info through challenge: " + info);
        var players = await a2s.QueryPlayersAsync("127.0.0.1", server.Port);
        Check(players != null && players.Count == 2 && players[1].Name == "영희" && (int)players[1].Duration.TotalSeconds == 3725, "players through challenge");
        Check(server.InfoChallenged && server.PlayerChallenged, "both requests went through the challenge step");

        // nothing listening: quick null, no exception
        var closed = new A2sClient { Timeout = TimeSpan.FromMilliseconds(300) };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Check(await closed.QueryInfoAsync("127.0.0.1", FakeA2sServer.FreePort()) == null && sw.Elapsed < TimeSpan.FromSeconds(3), "no server → null");

        Check(A2sClient.ParseInfo(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x49, 17, (byte)'x' }) == null, "truncated info → null");
    }

    private async Task MonitorSampling()
    {
        Check(ServerMonitorViewModel.FormatRate(512) == "512 B/s" && ServerMonitorViewModel.FormatRate(2048) == "2 KB/s" && ServerMonitorViewModel.FormatRate(3.5 * 1024 * 1024) == "3.5 MB/s", "rate format");
        Check(ServerMonitorViewModel.FormatGb(3L * 1024 * 1024 * 1024 + 300L * 1024 * 1024) == "3.3 GB" && ServerMonitorViewModel.FormatGb(512L * 1024 * 1024) == "512 MB", "size format");
        Check(ServerMonitorViewModel.FormatDuration(TimeSpan.FromMinutes(135)) == "2시간 15분" && ServerMonitorViewModel.FormatDuration(TimeSpan.FromSeconds(42)) == "42초" && ServerMonitorViewModel.FormatDuration(TimeSpan.FromHours(50)) == "2일 2시간", "duration format");

        // the real reader works on this machine
        var real = new SystemStatsReader();
        Check(real.ReadCpuTimes() != null && real.ReadMemory() is { TotalBytes: > 0 } && real.ReadNetworkTotals() != null, "real counters readable");
        Check(real.ReadProcess(Environment.ProcessId) is { WorkingSetBytes: > 0 }, "own process readable");
        Check(real.ReadProcess(int.MaxValue - 7) == null, "missing process → null");

        using var server = new FakeA2sServer("RSM 테스트", "Arland", 32, new[] { ("A", 10f), ("B", 600f), ("C", 30f) });
        var stats = new FakeStats();
        int? pid = null;
        DateTime? started = null;
        var vm = new ServerMonitorViewModel(stats, new A2sClient { Timeout = TimeSpan.FromSeconds(2) }, () => pid, () => server.Port, () => started);
        var t0 = new DateTime(2026, 10, 30, 1, 0, 0, DateTimeKind.Utc);

        // server off: PC numbers only
        await vm.SampleAsync(t0);
        stats.Step(idle: 60, total: 100, sent: 2 * 1024 * 1024, recv: 4 * 1024 * 1024);
        await vm.SampleAsync(t0.AddSeconds(2));
        Check(!vm.IsServerRunning && vm.PlayersSub == "서버 꺼짐" && vm.ServerCpuText == "—", "off state");
        Check(vm.SystemCpuText == "PC 전체 40%" && Math.Abs(vm.SystemCpuPercent - 40) < 0.01, "pc cpu: " + vm.SystemCpuText);
        Check(vm.SystemRamText == "PC 전체 12.0 GB / 32.0 GB (38%)", "pc ram: " + vm.SystemRamText);
        Check(vm.NetUpText == "↑ 1.0 MB/s" && vm.NetDownText == "↓ 2.0 MB/s", $"net: {vm.NetUpText} {vm.NetDownText}");

        // server on
        pid = 4242;
        started = t0.AddMinutes(-75);
        stats.Proc = new ProcessSample(TimeSpan.FromSeconds(100), 3L * 1024 * 1024 * 1024 + 300L * 1024 * 1024, 64);
        await vm.SampleAsync(t0.AddSeconds(4));
        Check(vm.IsServerRunning && vm.ServerCpuText == "측정 중…" && vm.ServerRamText == "3.3 GB" && vm.ThreadsText == "64", "first sample of the process");
        Check(vm.PlayersText == "3 / 32" && vm.ServerNameText == "RSM 테스트" && vm.MapText == "Arland" && Math.Abs(vm.PlayersPercent - 9.375) < 0.01, "players from A2S: " + vm.PlayersText);
        Check(vm.Players.Select(p => p.Name).SequenceEqual(new[] { "B", "C", "A" }) && vm.Players[0].TimeText == "10분", "player list, longest first");
        Check(vm.UptimeText == "1시간 15분", "uptime: " + vm.UptimeText);

        // 2 s later the server used 1 s of CPU time → 1/2/cores
        stats.Proc = stats.Proc.Value with { CpuTime = TimeSpan.FromSeconds(101) };
        await vm.SampleAsync(t0.AddSeconds(6));
        var expected = 100.0 / 2 / Environment.ProcessorCount;
        Check(Math.Abs(vm.ServerCpuPercent - expected) < 0.5, $"server cpu {vm.ServerCpuPercent} ≈ {expected}");

        // FPS from -logStats output
        Check(vm.FpsText == "—", "no fps yet");
        vm.OnServerLog("12:00:00 DEFAULT : FPS: 59.8, frame time (avg: 16.7 ms)");
        await vm.SampleAsync(t0.AddSeconds(8));
        Check(vm.FpsText == "60", "fps parsed: " + vm.FpsText);

        // graphs: one value per sample, 0..1, capped at 5 minutes
        Check(vm.CpuHistory.Count == 5 && vm.PlayerHistory.Last() > 0.09 && vm.RamHistory.Last() > 0.1, "histories grow");
        for (var i = 0; i < 200; i++)
            await vm.SampleAsync(t0.AddSeconds(10 + i * 2));
        Check(vm.CpuHistory.Count == ServerMonitorViewModel.HistoryLength && vm.NetHistory.All(v => v >= 0 && v <= 1), "history capped and normalised");

        // server stops
        pid = null;
        await vm.SampleAsync(t0.AddSeconds(500));
        Check(!vm.IsServerRunning && vm.Players.Count == 0 && vm.PlayersText == "—" && vm.UptimeText == "—" && vm.FpsText == "—", "back to off");

        // server running but A2S not answering yet (still loading)
        pid = 4242;
        var silent = new ServerMonitorViewModel(stats, new A2sClient { Timeout = TimeSpan.FromMilliseconds(200) }, () => pid, () => FakeA2sServer.FreePort(), () => started);
        await silent.SampleAsync(t0);
        Check(silent.PlayersText == "—" && silent.PlayersSub.Contains("응답 대기"), "loading server: waiting text");
    }


    private async Task InstallFollowUps()
    {
        // SteamCMD self-update detection (output copied from a real first run)
        var selfUpdate = new[] { "[----] Installing update...", "[----] Cleaning up...", "[----] Update complete, launching..." };
        Check(ServerTools.SteamCmdOnlyUpdatedItself(7, selfUpdate), "self-update recognised");
        Check(!ServerTools.SteamCmdOnlyUpdatedItself(8, new[] { "Error! App '1874900' state is 0x202 after update job." }), "real failure is not a self-update");
        Check(!ServerTools.SteamCmdOnlyUpdatedItself(0, new[] { "Update state (0x61) downloading, progress: 10.00" }), "downloading is not a self-update");

        // folder size
        var sizeDir = Path.Combine(_tmp, "size");
        Directory.CreateDirectory(Path.Combine(sizeDir, "a", "b"));
        File.WriteAllBytes(Path.Combine(sizeDir, "x.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(sizeDir, "a", "b", "y.bin"), new byte[2345]);
        Check(ServerTools.DirectorySize(sizeDir) == 3345 && ServerTools.DirectorySize(Path.Combine(_tmp, "nope")) == 0, "directory size");

        // path rebasing
        var oldDir = Path.Combine(_tmp, "C", "ReforgerServer", "server");
        var newDir = Path.Combine(_tmp, "E", "AR_SERVER");
        Check(MainViewModel.Rebase(Path.Combine(oldDir, "Configs", "server.json"), oldDir, newDir) == Path.Combine(newDir, "Configs", "server.json"), "rebase file");
        Check(MainViewModel.Rebase(oldDir + Path.DirectorySeparatorChar, oldDir, newDir) == newDir, "rebase same dir");
        Check(MainViewModel.Rebase(Path.Combine(_tmp, "elsewhere", "s.json"), oldDir, newDir) == null, "outside → untouched");
        Check(MainViewModel.Rebase(oldDir + "2" + Path.DirectorySeparatorChar + "s.json", oldDir, newDir) == null, "sibling with same prefix → untouched");

        // changing the server folder takes config + profile along
        var store = new AppSettingsStore(Path.Combine(_tmp, "follow", "app.json"));
        store.Save(new AppSettings
        {
            SteamCmdDir = Path.Combine(_tmp, "follow", "steamcmd"),
            ServerDir = oldDir,
            ConfigPath = Path.Combine(oldDir, "Configs", "server.json"),
            ProfileDir = Path.Combine(oldDir, "profile"),
        });
        var ui = new FakeUi();
        var vm = new MainViewModel(ui, null, store, FakeWorkshop.Client(new()));
        vm.ServerName = "따라오나";
        Check(vm.TrySaveConfig(false), "saved at old place");
        vm.ServerDir = newDir;
        Check(vm.ConfigPath == Path.Combine(newDir, "Configs", "server.json") && vm.ProfileDir == Path.Combine(newDir, "profile"), $"followed: {vm.ConfigPath} | {vm.ProfileDir}");
        Check(vm.IsDirty && vm.LogLines.Any(l => l.Contains("서버 폴더를 따라")), "marked unsaved + logged");
        Check(vm.TrySaveConfig(false) && ServerConfigJson.Read(ServerConfigJson.Load(Path.Combine(newDir, "Configs", "server.json"))).ServerName == "따라오나", "settings written at the new place");
        Check(new AppSettingsStore(store.FilePath).Load().ProfileDir == Path.Combine(newDir, "profile"), "remembered");

        // a hand-picked config elsewhere is left alone
        var custom = Path.Combine(_tmp, "mine", "my.json");
        vm.ConfigPath = custom;
        vm.ServerDir = Path.Combine(_tmp, "F", "srv");
        Check(vm.ConfigPath == custom, "custom config path untouched");

        // the rest uses a fake SteamCMD written as a shell script, which Windows can't run
        if (OperatingSystem.IsWindows())
            return;

        // install: SteamCMD updates itself first (exit 7), program runs it again automatically
        var steamDir = Path.Combine(_tmp, "retry", "steamcmd");
        var srvDir = Path.Combine(_tmp, "retry", "server");
        Directory.CreateDirectory(steamDir);
        var marker = Path.Combine(steamDir, "updated.flag");
        WriteScript(Path.Combine(steamDir, "steamcmd.exe"), $"""
            if [ ! -f "{marker}" ]; then
              touch "{marker}"
              echo "[----] Installing update..."
              echo "[----] Update complete, launching..."
              exit 7
            fi
            dir=""
            prev=""
            for a in "$@"; do
              [ "$prev" = "+force_install_dir" ] && dir="$a"
              prev="$a"
            done
            mkdir -p "$dir"
            printf '#!/bin/sh\nsleep 1\n' > "$dir/ArmaReforgerServer"
            chmod +x "$dir/ArmaReforgerServer"
            echo "Success! App '1874900' fully installed."
            """);
        var store2 = new AppSettingsStore(Path.Combine(_tmp, "retry", "app.json"));
        store2.Save(new AppSettings { SteamCmdDir = steamDir, ServerDir = srvDir, ConfigPath = Path.Combine(srvDir, "Configs", "server.json"), ProfileDir = Path.Combine(srvDir, "profile") });
        var vm2 = new MainViewModel(new FakeUi(), null, store2, FakeWorkshop.Client(new()));
        await vm2.InstallOrUpdateCommand.ExecuteAsync();
        Check(vm2.ServerInstalled && vm2.LogLines.Any(l => l.Contains("자기 자신만 업데이트")) && vm2.LogLines.Any(l => l.Contains("설치/업데이트가 끝났습니다")), "retried after self-update:\n" + string.Join("\n", vm2.LogLines.TakeLast(6)));
    }

    private void BuildIdParsing()
    {
        // trimmed real app_info_print output: manifests also have "public", but only branches.public has buildid
        var info = """
            AppID : 1874900, change number : 31234567/0, last change : Thu Oct 29 2026
            "1874900"
            {
                "common" { "name" "Arma Reforger Server" }
                "depots"
                {
                    "1874901"
                    {
                        "manifests"
                        {
                            "public" { "gid" "8361215099969594635" "size" "10153448457" }
                        }
                    }
                    "branches"
                    {
                        "public"
                        {
                            "buildid"		"24991234"
                            "timeupdated"		"1793380000"
                        }
                        "profiling"
                        {
                            "buildid"		"11111111"
                        }
                    }
                }
            }
            """.Split('\n');
        Check(ServerTools.ParseLatestBuildId(info) == 24991234, "latest public build");
        Check(ServerTools.ParseLatestBuildId(new[] { "Loading Steam API...OK", "No app info for AppID 1874900 found" }) == null, "no info → null");
        Check(ServerTools.BuildAppInfoArgs(1874900) == "+login anonymous +app_info_update 1 +app_info_print 1874900 +quit", "app info args");
        Check(ServerTools.AppId(false) == 1874900 && ServerTools.AppId(true) == 1890870, "app ids");

        // the user's layout: E:\AR_SERVER as server folder, steamcmd inside it
        var root = Path.Combine(_tmp, "ids", "AR_SERVER");
        var steam = Path.Combine(root, "steamcmd");
        var real = Path.Combine(steam, "steamapps", "common", ServerTools.SteamFolderName);
        Directory.CreateDirectory(real);
        Check(ServerTools.FindInstalledServer(root, steam) == null, "nothing installed yet");
        File.WriteAllText(ServerTools.ServerExe(real), "");
        Check(ServerTools.FindInstalledServer(root, steam) == real, "found in SteamCMD's library");
        Check(ServerTools.ReadInstalledBuildId(root, steam, 1874900) == null, "no manifest → null");
        // manifest copied from the user's PC
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_1874900.acf"), """
            "AppState"
            {
            	"appid"		"1874900"
            	"installdir"		"Arma Reforger Server"
            	"SizeOnDisk"		"10237359934"
            	"buildid"		"24870635"
            	"TargetBuildID"		"24870635"
            }
            """);
        Check(ServerTools.ReadInstalledBuildId(root, steam, 1874900) == null, "SteamCMD's record is not about another server folder (shared SteamCMD)");
        Check(ServerTools.ReadInstalledBuildId(real, steam, 1874900) == 24870635, "same after switching folder");
        Check(ServerTools.ReadInstalledBuildId(root, steam, 1890870) == null, "experimental not installed");

        Check(ServerTools.InstallPathWarnings(steam, root).Any(w => w.Contains("서버 폴더 안에 SteamCMD")), "steamcmd inside server folder warned");
        Check(!ServerTools.InstallPathWarnings(Path.Combine(root, "steamcmd"), Path.Combine(root, "server")).Any(w => w.Contains("서버 폴더 안에")), "side by side is fine");
    }

    private async Task FindServerAndAutoUpdate()
    {
        // same layout as the user's PC: server ended up in steamcmd\steamapps\common\Arma Reforger Server
        var root = Path.Combine(_tmp, "auto", "AR_SERVER");
        var steam = Path.Combine(root, "steamcmd");
        var real = Path.Combine(steam, "steamapps", "common", ServerTools.SteamFolderName);
        Directory.CreateDirectory(real);
        File.WriteAllText(ServerTools.ServerExe(real), "");
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_1874900.acf"), "\"AppState\"\n{\n\t\"buildid\"\t\t\"100\"\n}\n");
        var latestFile = Path.Combine(steam, "latest.txt");
        File.WriteAllText(latestFile, "101");
        var calls = Path.Combine(steam, "calls.txt");
        // fake SteamCMD: app_info_print answers with the "latest" build; app_update installs it
        WriteScript(Path.Combine(steam, "steamcmd.exe"), $$"""
            echo "$*" >> "{{calls}}"
            case "$*" in
              *app_info_print*)
                echo "Loading Steam API...OK"
                echo '"1874900"'
                echo '{ "depots" { "branches" { "public" { "buildid" "'$(cat "{{latestFile}}")'" } } } }'
                exit 0 ;;
            esac
            dir=""; prev=""
            for a in "$@"; do
              [ "$prev" = "+force_install_dir" ] && dir="$a"
              prev="$a"
            done
            mkdir -p "$dir/steamapps"
            : > "$dir/ArmaReforgerServer"
            printf '"AppState"\n{\n\t"buildid"\t\t"%s"\n}\n' "$(cat "{{latestFile}}")" > "$dir/steamapps/appmanifest_1874900.acf"
            echo "Success! App '1874900' fully installed."
            """);

        var store = new AppSettingsStore(Path.Combine(_tmp, "auto", "app.json"));
        store.Save(new AppSettings
        {
            SteamCmdDir = steam,
            ServerDir = root,
            ConfigPath = Path.Combine(root, "Configs", "server.json"),
            ProfileDir = Path.Combine(root, "profile"),
        });
        var ui = new FakeUi();
        var vm = new MainViewModel(ui, null, store, FakeWorkshop.Client(new()));
        Check(!vm.ServerInstalled && vm.AutoUpdateOnLaunch, "starts: not found at E:\\AR_SERVER, auto update on by default");

        // start-up: offer the found folder (yes), then see 101 > 100 and update without asking
        await vm.OnStartupAsync();
        Check(vm.ServerDir == real && vm.ServerInstalled, $"switched to the installed server: {vm.ServerDir}");
        Check(ui.Confirms.Count == 1 && ui.Confirms[0].Contains("찾은 위치"), "only the folder question was asked: " + string.Join(" / ", ui.Confirms));
        Check(vm.LogLines.Any(l => l.Contains("빌드 100 → 101")) && vm.LogLines.Any(l => l.Contains("자동 업데이트를 시작")), "auto update started:\n" + string.Join("\n", vm.LogLines.TakeLast(8)));
        Check(File.ReadAllText(calls).Contains("+force_install_dir \"" + real) || File.ReadAllText(calls).Contains(real), "updated into the found folder");
        Check(!vm.UpdateAvailable && vm.BuildStatusText == "설치된 빌드 101" && vm.LogLines.Any(l => l.Contains("설치/업데이트가 끝났습니다")), $"now 101: {vm.BuildStatusText}");
        Check(!vm.LogLines.Any(l => l.Contains("Loading Steam API")), "version check output kept out of the console");
        Check(new AppSettingsStore(store.FilePath).Load().ServerDir == real, "new server folder remembered");

        // manual check when already current: no question, says latest
        ui.Confirms.Clear();
        Check(vm.CheckUpdateCommand.CanExecute(null), "check button enabled");
        await vm.CheckUpdateCommand.ExecuteAsync();
        Check(vm.BuildStatusText.Contains("최신입니다") && ui.Confirms.Count == 0 && !vm.UpdateAvailable, $"up to date: {vm.BuildStatusText}");

        // manual check, new build, user says no → nothing installed, button text changes
        File.WriteAllText(latestFile, "102");
        ui.ConfirmAnswer = false;
        var installsBefore = File.ReadAllLines(calls).Count(l => l.Contains("app_update"));
        await vm.CheckUpdateCommand.ExecuteAsync();
        Check(vm.UpdateAvailable && vm.BuildStatusText.Contains("새 빌드 102") && ui.Confirms.Count == 1, $"asked about 102: {vm.BuildStatusText}");
        Check(File.ReadAllLines(calls).Count(l => l.Contains("app_update")) == installsBefore && vm.InstallButtonText == "새 버전으로 업데이트", "declined → no install");

        // next launch with auto update off: only shows the build, no Steam call
        var vm2 = new MainViewModel(new FakeUi(), null, store, FakeWorkshop.Client(new()));
        vm2.AutoUpdateOnLaunch = false;
        var callsBefore = File.ReadAllLines(calls).Length;
        await vm2.OnStartupAsync();
        Check(vm2.ServerInstalled && vm2.BuildStatusText == "설치된 빌드 101" && File.ReadAllLines(calls).Length == callsBefore, $"auto off: {vm2.BuildStatusText}");
        Check(!new AppSettingsStore(store.FilePath).Load().AutoUpdateOnLaunch, "toggle remembered");

        // Steam unreachable: friendly message, nothing else
        WriteScript(Path.Combine(steam, "steamcmd.exe"), "echo 'FAILED (No Connection)'\nexit 1\n");
        await vm2.CheckUpdateCommand.ExecuteAsync();
        Check(vm2.StatusText == "업데이트 확인 실패" && !vm2.IsCheckingUpdate && !vm2.IsBusy, "offline handled");
    }

    private void ConsoleLines()
    {
        // lines as they arrive in the console: our "[HH:mm:ss] " prefix + the server's own output (from the user's log)
        const string err = "[13:19:24] 13:19:24.494   BACKEND   (E): Unable to load addon 595F2BF2F44836FB (RHS - Status Quo)";
        const string warn = "[13:19:30] 13:19:30.600      BACKEND   (W): Addon 'X' failed checksum, retrying";
        const string online = "[13:19:32] 13:19:32.947 DEFAULT      : Entered online game state.";
        const string code = "[13:19:32] 13:19:32.609 BACKEND      : Direct Join Code: 0268219882";
        const string save = "[13:29:34] 13:29:34.006 DEFAULT      : [PERSISTENCE] Save completed successfully.";
        const string noise = "[13:19:25] 13:19:25.440    ENTITY       : Create entity @\"ENTITY:2\" ('Eden', GenericTerrainEntity)";
        const string ours = "[13:19:20] 설정을 저장했습니다: E:\\AR_SERVER\\Configs\\server.json";
        const string oursErr = "[13:19:20] 업데이트 확인 중 오류: timeout";

        Check(LogLine.Classify(err) == LogKind.Error && LogLine.Classify(oursErr) == LogKind.Error, "errors");
        Check(LogLine.Classify(warn) == LogKind.Warning, "warning");
        Check(LogLine.Classify(online) == LogKind.Highlight && LogLine.Classify(code) == LogKind.Highlight && LogLine.Classify(save) == LogKind.Highlight, "milestones");
        Check(LogLine.Classify(noise) == LogKind.Normal, "loading chatter is normal");
        Check(LogLine.LooksLikeServerLine(noise) && !LogLine.LooksLikeServerLine(ours) && !LogLine.LooksLikeServerLine(""), "server vs program lines");

        var all = new[] { err, warn, online, code, save, noise, ours, oursErr };
        string[] Pick(LogLevelFilter f, string? q = null) => all.Where(l => LogLine.Matches(l, f, q)).ToArray();
        Check(Pick(LogLevelFilter.All).Length == 8, "all");
        Check(Pick(LogLevelFilter.ErrorsOnly).SequenceEqual(new[] { err, oursErr }), "errors only");
        Check(Pick(LogLevelFilter.WarningsAndErrors).SequenceEqual(new[] { err, warn, oursErr }), "warnings + errors");
        Check(!Pick(LogLevelFilter.Important).Contains(noise) && Pick(LogLevelFilter.Important).Length == 7, "important hides only loading chatter");
        Check(Pick(LogLevelFilter.All, "join code").SequenceEqual(new[] { code }), "search, any case");
        Check(Pick(LogLevelFilter.ErrorsOnly, "unable").SequenceEqual(new[] { err }) && Pick(LogLevelFilter.ErrorsOnly, "join").Length == 0, "search + level");

        // what actually reaches the console from the server's stdout: no engine clock, just module + text (user's screenshot)
        const string stdoutNoise = "[19:26:42] SCRIPT    (W): @\"scripts/Game/UI/Backend/CareerBackend.c,159\": 'GetDSSession' is obsolete";
        const string stdoutLoad = "[19:26:43] BACKEND      : Loading dedicated server config.";
        const string dl = "[19:27:59]  BACKEND      : RHS - Status Quo: [=================>__] 85% 163/191 MB";
        const string dlStart = "[19:26:46] BACKEND      : Addon Download started 595F2BF2F44836FB - RHS - Status Quo";
        const string admins = "[13:19:30]      BACKEND   (W): !!! JsonApi Array name=\"admins\" found in JSON - which missing adequate script declaration, items will be skipped!";
        Check(LogLine.LooksLikeServerLine(stdoutNoise) && LogLine.LooksLikeServerLine(stdoutLoad) && LogLine.LooksLikeServerLine(dl), "stdout lines recognised as server lines");
        Check(!LogLine.LooksLikeServerLine("[19:26:40] > ArmaReforgerServer.exe -config x") && !LogLine.LooksLikeServerLine("[19:26:40] 모드 업데이트: RHS 1 → 2"), "program lines are not");
        Check(LogLine.Classify(stdoutNoise) == LogKind.Noise && LogLine.Classify(admins) == LogKind.Noise && LogLine.Classify(noise.Replace("Create entity", "x")) == LogKind.Normal, "stock-game chatter is noise");
        Check(LogLine.Classify("[13:19:24]    ENTITY    (E): GenericEntity component Hierarchy cannot be combined with component Hierarchy") == LogKind.Noise, "vanilla hierarchy error is noise");
        Check(LogLine.Classify(dl) == LogKind.Highlight && LogLine.Classify(dlStart) == LogKind.Highlight, "downloads highlighted");
        Check(LogLine.DownloadProgress(dl) == "RHS - Status Quo 85% (163/191 MB)" && LogLine.DownloadProgress(stdoutLoad) == null, $"progress: {LogLine.DownloadProgress(dl)}");
        Check(LogLine.JoinCode(code) == "0268219882" && LogLine.JoinCode(save) == null, "join code");
        var stdout = new[] { stdoutNoise, stdoutLoad, dl, ours };
        Check(stdout.Where(l => LogLine.Matches(l, LogLevelFilter.Important, null)).SequenceEqual(new[] { dl, ours }), "important hides stdout chatter + noise");
        Check(!LogLine.Matches(stdoutNoise, LogLevelFilter.WarningsAndErrors, null), "noise not counted as a warning");

        // view model: filter state, radio properties, copy text
        var vm = new MainViewModel(new FakeUi(), null, new AppSettingsStore(Path.Combine(_tmp, "console", "app.json")), FakeWorkshop.Client(new()));
        vm.LogLines.Clear();
        foreach (var l in all) vm.LogLines.Add(l);
        Check(vm.LogShowAll && !vm.LogShowErrors, "starts with everything");
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.LogShowErrors = true;
        Check(vm.LogLevel == LogLevelFilter.ErrorsOnly && !vm.LogShowAll && changed.Contains(nameof(vm.LogShowAll)) && changed.Contains(nameof(vm.LogShowErrors)), "radio → level");
        vm.LogShowAll = false; // unchecking by the group must not change anything
        Check(vm.LogLevel == LogLevelFilter.ErrorsOnly, "uncheck ignored");
        Check(vm.VisibleLogText() == err + Environment.NewLine + oursErr, "copy = visible lines");
        vm.LogShowAll = true;
        vm.LogSearch = "save";
        Check(vm.VisibleLogText() == save, "copy follows search");

        // text size bounds + window layout remembered
        vm.ConsoleFontSize = 99;
        Check(vm.ConsoleFontSize == MainViewModel.MaxConsoleFont, "font size capped");
        vm.ConsoleFontSize = 14;
        vm.WindowWidth = 1600; vm.WindowHeight = 950; vm.WindowMaximized = true; vm.ConsoleHeight = 420;
        vm.Shutdown();
        var again = new MainViewModel(new FakeUi(), null, new AppSettingsStore(Path.Combine(_tmp, "console", "app.json")), FakeWorkshop.Client(new()));
        Check(again.WindowWidth == 1600 && again.WindowHeight == 950 && again.WindowMaximized && again.ConsoleHeight == 420 && again.ConsoleFontSize == 14, "layout remembered");
        var fresh = new AppSettings();
        Check(fresh.WindowWidth == 0 && fresh.ConsoleHeight == 300 && fresh.ConsoleFontSize == 13, "defaults");
    }

    // the user's console.log (2026-10-30 19:34–19:37), two players joining a Game Master server
    private static readonly string[] JoinLog =
    {
        "19:34:49.321 RPL          : ServerImpl event: authenticating (identity=0x00000000, address=122.36.186.118:52097, slots=1/32)",
        "19:34:49.730 BACKEND      : Authenticated player: rplIdentity=0x00000000 identityId=09f3e505-96b1-4f77-a169-f168f14f888c name=ArtRoxF486",
        "19:34:49.740 RPL          : ServerImpl event: connected (identity=0x00000000)",
        "19:34:49.763  DEFAULT      : BattlEye Server: Adding player identity=0x00000000, name='ArtRoxF486'",
        "19:34:49.763  DEFAULT      : BattlEye Server: 'Player #0 ArtRoxF486 (122.36.186.118:52097) connected'",
        "19:34:59.906 RPL          : ServerImpl event: authenticating (identity=0x00000001, address=61.75.108.183:59725, slots=2/32)",
        "19:35:00.263 BACKEND      : Authenticated player: rplIdentity=0x00000001 identityId=8dad34ea-4b0c-4c67-a856-8c0229634098 name=KCAL",
        "19:35:00.296  DEFAULT      : BattlEye Server: Adding player identity=0x00000001, name='KCAL'",
        "19:35:01.146 NETWORK      : Player connected: connectionID=0",
        "19:35:01.146 NETWORK      : ### Connecting player: connectionID=0, Name=",
        "19:35:01.196   NETWORK      : ### Creating player: PlayerId=1, Name=ArtRoxF486",
        "19:35:01.332   NETWORK      : ### Updating player: PlayerId=1, Name=ArtRoxF486, rplIdentity=0x00000000, IdentityId=09f3e505-96b1-4f77-a169-f168f14f888c",
        "19:35:01.332   NETWORK      : Players connected: 1 / 1",
        "19:35:10.830   NETWORK      : ### Creating player: PlayerId=2, Name=KCAL",
        "19:35:12.596   NETWORK      : ### Updating player: PlayerId=2, Name=KCAL, rplIdentity=0x00000001, IdentityId=8dad34ea-4b0c-4c67-a856-8c0229634098",
        "19:36:10.029   SCRIPT       : INFO: Editor EDIT: Player ArtRoxF486 (playerID = 1 | UUID = 09f3e505-96b1-4f77-a169-f168f14f888c) opened the editor.",
        "19:36:49.275   SCRIPT       : INFO: Faction: player ArtRoxF486 (playerID = 1 | UUID = 09f3e505-96b1-4f77-a169-f168f14f888c) has joined faction #AR-Faction_US (US)",
        "19:37:05.629   SCRIPT       : INFO: Faction: player KCAL (playerID = 2 | UUID = 8dad34ea-4b0c-4c67-a856-8c0229634098) has joined faction #AR-Faction_US (US)",
    };

    private void PlayersFromLog()
    {
        var clock = new DateTime(2026, 10, 30, 10, 34, 0, DateTimeKind.Utc);
        var t = new PlayerTracker(() => clock);
        foreach (var line in JoinLog)
        {
            if (line.Contains("name=KCAL")) clock = clock.AddSeconds(11);
            t.OnServerLog(line);
        }
        var list = t.Snapshot();
        Check(list.Select(p => p.Name).SequenceEqual(new[] { "ArtRoxF486", "KCAL" }), "both players, in join order: " + string.Join(",", list.Select(p => p.Name)));
        Check(list[0].PlayerId == 1 && list[1].PlayerId == 2 && list.All(p => p.Faction == "US"), "player ids + faction");
        Check(list[1].JoinedUtc - list[0].JoinedUtc == TimeSpan.FromSeconds(11), "join times");

        // someone who only tried to connect (authenticating → timeout) is not a player
        var v = t.Version;
        t.OnServerLog("19:40:00.000 RPL          : ServerImpl event: authenticating (identity=0x00000004, address=91.97.255.92:65433)");
        Check(t.Snapshot().Count == 2 && t.Version == v, "connection attempt ignored");

        // leaving: the RPL line (format from Bohemia's tracker)
        t.OnServerLog("19:41:00.000 RPL          : ServerImpl event: disconnected (identity=0x00000000), group=1, reason=9");
        Check(t.Snapshot().Select(p => p.Name).SequenceEqual(new[] { "KCAL" }) && t.Version > v, "ArtRoxF486 left");
        t.OnServerLog("19:42:00.000 NETWORK : ### Disconnecting player: PlayerId=2, Name=KCAL");
        Check(t.Snapshot().Count == 0, "KCAL left (other format)");

        // names with spaces/korean, BattlEye only
        t.OnServerLog("BattlEye Server: Adding player identity=0x00000007, name='한국 사람 1'");
        Check(t.Snapshot().Single().Name == "한국 사람 1", "name with spaces");
        t.OnServerLog("BattlEye Server: Removing player identity=0x00000007");
        Check(t.Snapshot().Count == 0, "BE remove");

        // the same line twice changes nothing; a busy server's other lines are skipped cheaply
        t.OnServerLog(JoinLog[1]);
        v = t.Version;
        t.OnServerLog(JoinLog[1]);
        Check(t.Version == v && !t.OnServerLog("19:35:01.210 ENTITY : SpawnEntityPrefab @\"{A1AE}Prefabs/Characters/Core/DefaultPlayerControllerMP_Factions.et\""), "no-op lines");
        t.Clear();
        Check(t.Snapshot().Count == 0, "cleared");
        Check(ServerMonitorViewModel.IsPlaceholderName("player") && ServerMonitorViewModel.IsPlaceholderName(" Player ") && !ServerMonitorViewModel.IsPlaceholderName("KCAL"), "placeholder");
    }

    private async Task PlayerListOnStatusPage()
    {
        // Reforger's real A2S answer: the right count, but one "player" stand-in instead of names
        using var server = new FakeA2sServer("MGM server test", "GM_Eden", 32, new[] { ("player", 0f) });
        var stats = new FakeStats();
        var t0 = new DateTime(2026, 10, 30, 10, 38, 0, DateTimeKind.Utc);
        var now = t0.AddMinutes(-4);
        var vm = new ServerMonitorViewModel(stats, new A2sClient(), () => 4242, () => server.Port, () => t0.AddMinutes(-7))
        {
            Tracker = new PlayerTracker(() => now),
        };

        await vm.SampleAsync(t0);
        Check(vm.Players.Count == 0, "placeholder name is not shown as a player: " + string.Join(",", vm.Players.Select(p => p.Name)));

        foreach (var line in JoinLog)
        {
            if (line.Contains("name=KCAL")) now = t0.AddSeconds(-30);
            vm.OnServerLog(line);
        }
        await vm.SampleAsync(t0.AddSeconds(2));
        Check(vm.Players.Select(p => p.Name).SequenceEqual(new[] { "ArtRoxF486", "KCAL" }), "real names from the log: " + string.Join(",", vm.Players.Select(p => p.Name)));
        Check(vm.Players[0].TimeText == "4분" && vm.Players[1].TimeText == "32초" && vm.Players.All(p => p.Faction == "US" && p.HasFaction), $"times + faction: {vm.Players[0].TimeText} {vm.Players[1].TimeText}");

        var first = vm.Players[0];
        await vm.SampleAsync(t0.AddSeconds(2));
        Check(ReferenceEquals(vm.Players[0], first), "no rebuild when nothing changed");

        vm.OnServerLog("RPL          : ServerImpl event: disconnected (identity=0x00000001), group=1, reason=9");
        await vm.SampleAsync(t0.AddSeconds(4));
        Check(vm.Players.Select(p => p.Name).SequenceEqual(new[] { "ArtRoxF486" }), "KCAL left");
    }

    private void LogExplanations()
    {
        LogExplanation E(string line) => LogExplainer.Explain(line) ?? throw new Exception("no explanation: " + line);

        // lines from the user's console (stdout form) and console.log (with engine clock)
        var gizmo = E("[19:31:36]   RESOURCES (E): Wrong GUID/name for resource @\"{FF35BE9795BA3797}system/wbdata/Gizmo3D/sphere.xob\" in property \"Object\"");
        Check(gizmo.Severity == LogSeverity.Harmless && gizmo.SeverityText == "무시해도 됨" && gizmo.Module.StartsWith("RESOURCES"), $"red but harmless: {gizmo.Title}");
        Check(E("[19:26:42] SCRIPT    (W): @\"scripts/Game/UI/Backend/CareerBackend.c,159\": 'GetDSSession' is obsolete").Title.Contains("옛 함수"), "obsolete");
        Check(E("19:31:45.768 DEFAULT      : Entered online game state.") is { Severity: LogSeverity.Good, Title: "서버 온라인" }, "online");
        var code = E("[19:34:49] BACKEND      : Direct Join Code: 0723358926");
        Check(code.Title == "다이렉트 접속 코드" && code.Module.Contains("Bohemia 온라인 서비스"), "join code + module");
        Check(E("19:34:49.730 BACKEND      : Authenticated player: rplIdentity=0x00000000 identityId=09f3 name=ArtRoxF486").Title == "플레이어 인증 성공", "player");
        Check(E("[19:27:59]  BACKEND      : RHS - Status Quo: [=================>__] 85% 163/191 MB").Title == "모드 받는 중", "download");
        Check(E("19:36:49.275   SCRIPT       : INFO: Faction: player ArtRoxF486 (playerID = 1 | UUID = x) has joined faction #AR-Faction_US (US)").Title == "진영 선택", "faction");
        Check(E("19:31:36.151  SCRIPT       : Module: Game; loaded 6131x files; 12079x classes").Title == "게임 스크립트 준비 완료", "scripts");

        // real problems get a "할 일"
        var port = E("NETWORK   (E): Failed to bind socket, address already in use 0.0.0.0:2001");
        Check(port.Severity == LogSeverity.Problem && port.HasAction && port.Action.Contains("포트"), "port in use");
        var json = E("BACKEND   (E): JSON is not valid: #/game/maxPlayers must be <= 128");
        Check(json.Severity == LogSeverity.Problem && json.Title.Contains("config.json"), "bad config");
        Check(E("BACKEND   (E): Addon 1337C0DE5DABBEEF download failed").Title == "모드 다운로드 실패", "download failed");
        // the user's new server (2026-10-31): a fragment fails and is retried — not a failure
        foreach (var l in new[] { "[08:24:36] DEFAULT   (E): error bad headers: date", "[08:24:36] DEFAULT   (E): CURL error:",
                                  "[08:24:36] BACKEND   (E): Fragmentizer: Download error (fragment 19/21) uHttpCode: 0 eApiCode: 0x90",
                                  "[08:24:36] BACKEND   (E): Fragmentizer: Retrying download from fragment 19/21" })
            Check(E(l) is { Severity: LogSeverity.Caution } x && x.Title.Contains("자동으로 다시 받음"), "fragment retry: " + l);
        var input = "[08:39:20]   INPUT     (E): InputManager: unknown key 'gamepad0:up'";
        Check(E(input).Severity == LogSeverity.Harmless && LogLine.Classify(input) == LogKind.Noise, "gamepad keys on a dedicated server: harmless");
        Check(E("RPL          : ServerImpl event: authentication timeout (identity=0x00000004)").Severity == LogSeverity.Caution, "auth timeout");

        // unknown lines: generic, by level and module
        var unknownErr = E("[20:00:00] PHYSICS   (E): Something strange happened");
        Check(unknownErr.Severity == LogSeverity.Caution && unknownErr.Meaning.StartsWith("물리") && unknownErr.HasAction, $"unknown error: {unknownErr.Meaning}");
        Check(E("[20:00:00] WORLD        : UpdateEntities").Severity == LogSeverity.Info, "unknown info");
        Check(LogExplainer.Explain("[20:00:00] 설정을 저장했습니다: E:\\x.json") == null && LogExplainer.Explain("") == null, "RSM's own lines: null");

        // diagnosis of a run: only the latest start counts
        var lines = new List<string>
        {
            "[13:00:00] > E:\\AR\\ArmaReforgerServer.exe -config x",
            "[13:00:01] BACKEND   (E): JSON is not valid: old run",
            "[19:26:40] > E:\\AR\\ArmaReforgerServer.exe -config x",
            "[19:26:43] INIT         : Creating game instance(ArmaReforgerScripted), version 1.8.0.13 built 2026-08-21 14:30:18 UTC.",
            "[19:26:43] BACKEND      : JSON is Valid",
            "[19:26:46] BACKEND      : Addon Download started 595F2BF2F44836FB - RHS - Status Quo",
        };
        var mid = LogExplainer.Summarize(lines);
        Check(mid.Overall == LogSeverity.Info && mid.Headline.Contains("모드를 받는 중") && !mid.HasProblems, $"downloading: {mid.Headline}");
        lines.AddRange(new[]
        {
            "[19:31:34] BACKEND      : Required addons are ready to use.",
            "[19:31:36]   RESOURCES (E): Wrong GUID/name for resource @\"{FF35}x.xob\"",
            "[19:31:36] SCRIPT    (W): 'x' is obsolete",
            "[19:31:45] BACKEND      : Server registered with address: 123.212.105.69:2001",
            "[19:31:45] BACKEND      : Direct Join Code: 0723358926",
            "[19:31:45] DEFAULT      : Entered online game state.",
            "[19:34:49] BACKEND      : Authenticated player: rplIdentity=0x00000000 identityId=a name=ArtRoxF486",
            "[19:40:00] RPL          : ServerImpl event: authentication timeout (identity=0x00000004)",
        });
        var ok = LogExplainer.Summarize(lines);
        Check(ok.Overall == LogSeverity.Good && ok.HarmlessCount == 2, $"online, noise counted: {ok.Headline} / {ok.HarmlessCount}");
        Check(ok.Facts.Contains("게임 버전 1.8.0.13") && ok.Facts.Any(f => f.Contains("RHS - Status Quo")) && ok.Facts.Contains("다이렉트 접속 코드 0723358926") && ok.Facts.Any(f => f.Contains("123.212.105.69:2001")), string.Join(" / ", ok.Facts));
        Check(ok.Problems.Count == 1 && ok.Problems[0].Explanation.Title == "플레이어 접속 실패" && !ok.Problems.Any(p => p.Example.Contains("old run")), "only this run's problems");
        lines.Add("[19:41:00] NETWORK   (E): Failed to bind socket, address already in use");
        lines.Add("[19:41:01] NETWORK   (E): Failed to bind socket, address already in use");
        var bad = LogExplainer.Summarize(lines);
        Check(bad.Overall == LogSeverity.Problem && bad.Problems[0].Explanation.Severity == LogSeverity.Problem && bad.Problems[0].Count == 2, $"problem first, grouped: {bad.Headline}");
        Check(LogExplainer.Summarize(Array.Empty<string>()).Headline.Contains("아직"), "empty");

        // view model: selecting a line explains it; clearing it shows the diagnosis
        var vm = new MainViewModel(new FakeUi(), null, new AppSettingsStore(Path.Combine(_tmp, "explain", "app.json")), FakeWorkshop.Client(new()));
        foreach (var l in lines) vm.LogLines.Add(l);
        vm.ShowLogHelp = true;
        Check(vm.LogSummary.Overall == LogSeverity.Problem, "opening the panel diagnoses");
        vm.SelectedLogLine = lines[5];
        Check(vm.HasSelectedLogLine && vm.SelectedLogExplanation?.Title == "모드 다운로드 시작", "selected line explained");
        vm.SelectedLogLine = "[19:00:00] 서버 설치/업데이트가 끝났습니다.";
        Check(vm.SelectedLogExplanation?.Title == "RSM 메시지", "own message");
        vm.SelectedLogLine = null;
        Check(!vm.HasSelectedLogLine && vm.SelectedLogExplanation == null, "back to diagnosis");
    }

    private void ConnectionProblems()
    {
        var clock = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
        var log = new ConnectionLog(() => clock);
        void Feed(params string[] lines) { foreach (var l in lines) log.OnServerLog(l); }
        string Auth(string id, string ip) => $"RPL          : ServerImpl event: authenticating (identity={id}, address={ip}:52097, slots=1/32)";
        string Ok(string id, string name) => $"BACKEND      : Authenticated player: rplIdentity={id} identityId=09f3e505 name={name}";
        string InGame(int pid, string name, string id) => $"NETWORK      : ### Updating player: PlayerId={pid}, Name={name}, rplIdentity={id}, IdentityId=09f3e505";
        string Gone(string id, int g, int r) => $"RPL          : ServerImpl event: disconnected (identity={id}), group={g}, reason={r}";

        // 1. joins, plays, leaves normally → nothing to report
        Feed(Auth("0x00000000", "122.36.186.118"), Ok("0x00000000", "ArtRoxF486"), InGame(1, "ArtRoxF486", "0x00000000"), Gone("0x00000000", 1, 9));
        Check(log.Issues().Count == 0, "normal leave is not a problem");
        // unknown code from someone playing: most likely a normal quit
        Feed(Auth("0x00000009", "1.1.1.1"), Ok("0x00000009", "Quitter"), InGame(9, "Quitter", "0x00000009"), Gone("0x00000009", 2, 7));
        Check(log.Issues().Count == 0, "unknown code mid-game → not flagged");

        // 2. authenticated, timed out while loading in
        Feed(Auth("0x00000001", "61.75.108.183"), Ok("0x00000001", "KCAL"), Gone("0x00000001", 1, 4));
        var timeout = log.Issues()[0];
        Check(timeout.Who == "KCAL" && timeout.Cause.Code == "TIMEOUT" && timeout.Address == "61.75.108.183", $"timeout: {timeout.Who} {timeout.Cause.Code} {timeout.Address}");
        Check(timeout.PlayerMessage.StartsWith("KCAL님, ") && timeout.PlayerMessage.Contains("group=1 'REPLICATION', reason=4 'TIMEOUT'"), timeout.PlayerMessage);

        // 3. mod files differ — even for someone already in game
        Feed(Auth("0x00000002", "5.5.5.5"), Ok("0x00000002", "모드꼬임"), InGame(2, "모드꼬임", "0x00000002"), Gone("0x00000002", 4, 1));
        var rdb = log.Issues()[0];
        Check(rdb.Cause.Title == "모드 파일이 서버와 다름" && rdb.Cause.Severity == LogSeverity.Problem && rdb.PlayerMessage.Contains("addons") && rdb.PlayerMessage.Contains("무결성 검사"), "RDB mismatch + fix steps");

        // 4. never got past authentication: name unknown, but the same address played before
        Feed(Auth("0x00000003", "122.36.186.118"), Gone("0x00000003", 2, 5));
        var anon = log.Issues()[0];
        Check(anon.Who == "(아마 ArtRoxF486)" && !anon.PlayerMessage.StartsWith("(") && anon.Detail.Contains("group=2"), $"guessed from address: {anon.Who} / {anon.Detail}");
        Feed(Auth("0x00000004", "9.9.9.9"), Gone("0x00000004", 1, 9));
        Check(log.Issues()[0].Who == "(이름 모름)" && log.Issues()[0].Cause.Title == "인증 실패", "unknown person, auth stage");

        // 5. authenticated then dropped before the game with the normal close code → dropped while joining
        Feed(Auth("0x00000005", "8.8.8.8"), Ok("0x00000005", "취소함"), Gone("0x00000005", 1, 9));
        Check(log.Issues()[0].Who == "취소함" && log.Issues()[0].Cause.Title == "입장 중 끊김", "dropped while joining");

        // 6. explicit authentication timeout line, then the disconnect: one entry, not two
        var n = log.Issues().Count;
        Feed(Auth("0x00000006", "7.7.7.7"), "RPL          : ServerImpl event: authentication timeout (identity=0x00000006)", Gone("0x00000006", 1, 4));
        Check(log.Issues().Count == n + 1 && log.Issues()[0].Cause.Title == "인증 실패", "auth timeout counted once");

        // 7. BattlEye kick, then the disconnect: one entry, reason in Korean
        Feed(Auth("0x00000007", "6.6.6.6"), Ok("0x00000007", "BE문제"), InGame(7, "BE문제", "0x00000007"),
             "DEFAULT      : BattlEye Server: 'Player #6 BE문제 (6.6.6.6:2304) has been kicked by BattlEye: Client not responding'", Gone("0x00000007", 3, 1));
        Check(log.Issues().Count == n + 2 && log.Issues()[0].Cause.Title == "BattlEye가 내보냄: 클라이언트 응답 없음" && log.Issues()[0].Detail == "BattlEye: Client not responding", "BattlEye kick once");

        // 8. server shutting down: everyone drops, nobody is a problem
        n = log.Issues().Count;
        Feed(Auth("0x00000008", "4.4.4.4"), Ok("0x00000008", "끝"), "ENGINE       : Game destroyed.", Gone("0x00000008", 1, 9));
        Check(log.Issues().Count == n, "shutdown ignored");
        log.ServerStarted();
        Check(log.Issues().Count == n, "issues kept across restarts");
        log.ClearIssues();
        Check(log.Issues().Count == 0, "cleared");

        // the codes players see, as table
        Check(KickCauses.Describe(1, 11).CodeText == "group=1 'REPLICATION', reason=11 'SERVER_UNREACHABLE'" && KickCauses.Describe(3, 3).Code == "GAME_RESTART_REQUIRED" && KickCauses.Describe(4, 2).Code == "SCRIPT_MISMATCH", "code names");
        Check(KickCauses.Describe(6, 0).IsProblem == false && KickCauses.Describe(1, 9).IsProblem == false && KickCauses.Describe(9, 9).Title.Contains("group=9"), "kick/close/unknown");

        // console explanation of the same line
        var ex = LogExplainer.Explain("[21:00:00] RPL          : ServerImpl event: disconnected (identity=0x00000001), group=4, reason=1")!;
        Check(ex.Title == "플레이어 연결 끊김: 모드 파일이 서버와 다름" && ex.Severity == LogSeverity.Problem && ex.HasAction && ex.Meaning.Contains("RDB_MISMATCH"), ex.Title);
        Check(LogExplainer.Explain("RPL : ServerImpl event: disconnected (identity=0x00000001), group=1, reason=9")!.Severity == LogSeverity.Info, "normal close explained as info");
    }

    private async Task ConnectionProblemsOnStatusPage()
    {
        var vm = new ServerMonitorViewModel(new FakeStats(), new A2sClient { Timeout = TimeSpan.FromMilliseconds(100) }, () => 4242, () => FakeA2sServer.FreePort(), () => DateTime.UtcNow);
        await vm.SampleAsync(DateTime.UtcNow);
        Check(vm.ConnectionIssueCount == 0, "none yet");
        vm.OnServerLog("RPL : ServerImpl event: authenticating (identity=0x00000001, address=61.75.108.183:59725, slots=2/32)");
        vm.OnServerLog("BACKEND : Authenticated player: rplIdentity=0x00000001 identityId=8dad name=KCAL");
        vm.OnServerLog("RPL : ServerImpl event: disconnected (identity=0x00000001), group=4, reason=2");
        await vm.SampleAsync(DateTime.UtcNow);
        Check(vm.ConnectionIssueCount == 1 && vm.ConnectionIssues[0].Who == "KCAL" && vm.ConnectionIssues[0].Cause.Code == "SCRIPT_MISMATCH" && vm.ConnectionIssues[0].Cause.SeverityKey == "problem", "shown on the status page");
        vm.ClearConnectionIssues();
        Check(vm.ConnectionIssueCount == 0, "cleared from the page");
    }

    // the server's download folder, as on the user's PC (profile\addons\RHS-StatusQuo_595F2BF2F44836FB …)
    private static string MakeAddon(string addons, string folder, string id, string name, string version, int bytes)
    {
        var dir = Path.Combine(addons, folder);
        Directory.CreateDirectory(Path.Combine(dir, "temp"));
        File.WriteAllBytes(Path.Combine(dir, "data.pak"), new byte[bytes]);
        File.WriteAllText(Path.Combine(dir, "meta"), "\uFEFF{\"meta\":{\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"versions\":[{\"version\":\"" + version + "\",\"gameVersion\":\"\"}]}}");
        return dir;
    }

    private static string ScenariosPage(params (string id, string name)[] scenarios)
    {
        var arr = new JsonArray();
        foreach (var (id, name) in scenarios)
            arr.Add(new JsonObject { ["name"] = name, ["gameId"] = id, ["playerCount"] = 128, ["gameMode"] = "GameMaster" });
        var data = new JsonObject { ["props"] = new JsonObject { ["pageProps"] = new JsonObject { ["asset"] = new JsonObject { ["id"] = "x", ["scenarios"] = arr } } } };
        return $"<html><body><script id=\"__NEXT_DATA__\" type=\"application/json\">{data.ToJsonString()}</script></body></html>";
    }

    private async Task WorkshopScenarios()
    {
        const string zim = "597697D81A1EA202", gmZim = "{9D2D546F1E889561}Missions/GM_Zimnitrita.conf";
        // parsing: the scenario tab's data (seen 2026-11-01: "Zimnitrita - Game Master", {9D2D546F1E889561}Missions/GM_Zimnitrita.conf)
        var parsed = WorkshopPage.ParseScenarios(ScenariosPage((gmZim, "Zimnitrita - Game Master")));
        Check(parsed.Count == 1 && parsed[0].Id == gmZim && parsed[0].DisplayName == "Zimnitrita - Game Master", "scenario tab parsed");
        // no embedded data: ids in the page text still count
        var plain = WorkshopPage.ParseScenarios("<div>Scenario ID</div><div>{9D2D546F1E889561}Missions/GM_Zimnitrita.conf</div>");
        Check(plain.Count == 1 && plain[0].Id == gmZim && plain[0].DisplayName == "GM_Zimnitrita", "plain text fallback");
        Check(WorkshopPage.ParseScenarios(ScenariosPage()).Count == 0, "mod without scenarios");

        var site = new FakeWorkshop.FakeSite();
        site.Pages[zim + "/scenarios"] = ScenariosPage((gmZim, "Zimnitrita - Game Master"), ("{9D2D546F1E889562}Missions/Conflict_Zimnitrita.conf", "Zimnitrita - Conflict"));
        site.Pages[RhsId + "/scenarios"] = ScenariosPage();
        var root = Path.Combine(_tmp, "wsscen");
        var store = new AppSettingsStore(Path.Combine(root, "app.json"));
        store.Save(new AppSettings { SteamCmdDir = Path.Combine(root, "steamcmd"), ServerDir = Path.Combine(root, "server"),
                                     ConfigPath = Path.Combine(root, "server", "Configs", "server.json"), ProfileDir = Path.Combine(root, "server", "profile") });
        var vm = new MainViewModel(new FakeUi(), null, store, FakeWorkshop.Client(site));
        vm.Mods.Add(new ModEntry(RhsId, "RHS - Status Quo"));
        vm.Mods.Add(new ModEntry(zim, "Zimnitrita"));          // not downloaded yet: the Workshop still knows its scenarios
        await vm.WaitForScenarioLookupsAsync();
        await vm.WaitForScenarioLookupsAsync();
        Check(vm.Scenarios[0].DisplayName == "[Zimnitrita] Zimnitrita - Game Master" && vm.Scenarios[1].Id.Contains("Conflict_Zimnitrita"),
              "added mod's scenarios at the top: " + string.Join(" | ", vm.Scenarios.Take(3).Select(x => x.DisplayName)));
        vm.SelectedScenario = vm.Scenarios[0];
        Check(vm.ScenarioId == gmZim, "selectable");
        Check(vm.TrySaveConfig(false), "saved");

        // next start, offline: remembered list still offered, saved choice shown by name
        var offline = new MainViewModel(new FakeUi(), null, store, FakeWorkshop.Client(new FakeWorkshop.FakeSite { Offline = true }));
        Check(offline.SelectedScenario?.DisplayName == "[Zimnitrita] Zimnitrita - Game Master" && offline.Scenarios.Count(x => x.Id.Contains("Zimnitrita")) == 2,
              $"from the cache: {offline.SelectedScenario?.DisplayName}");
        var failed = await offline.RefreshModScenariosAsync(all: true);
        Check(failed == 2 && offline.Scenarios.Count(x => x.Id.Contains("Zimnitrita")) == 2, "offline refresh keeps what it knew");

        // the button: Workshop answer, no server needed
        var ui = new FakeUi();
        var vm3 = new MainViewModel(ui, null, store, FakeWorkshop.Client(site));
        await vm3.ListScenariosCommand.ExecuteAsync();
        Check(vm3.StatusText.Contains("모드 시나리오 2개") && !vm3.LogLines.Any(l => l.Contains("-listScenarios")), vm3.StatusText);
        foreach (var v in new[] { vm, offline, vm3 }) v.Shutdown();
    }

    private void ModScenarios()
    {
        var root = Path.Combine(_tmp, "modscen");
        var profile = Path.Combine(root, "server", "profile");
        var addons = Path.Combine(profile, "addons");
        var map = MakeAddon(addons, "Zimnitrita_597697D81A1EA202", "597697D81A1EA202", "Zimnitrita", "1.2.3", 2000);
        // a map mod's meta with its scenarios (field names as the Workshop uses them; RSM only relies on the id shape)
        File.WriteAllText(Path.Combine(map, "meta"), """
            {"meta":{"id":"597697D81A1EA202","name":"Zimnitrita","versions":[{"version":"1.2.3","scenarios":[
              {"gameId":"{1A2B3C4D5E6F7081}Missions/Zimnitrita_Conflict.conf","name":"Conflict - Zimnitrita","playerCount":64},
              {"gameId":"{1A2B3C4D5E6F7082}Missions/Zimnitrita_GM.conf","name":"Game Master - Zimnitrita"}]},
              {"version":"1.2.2","scenarios":[{"gameId":"{9999999999999999}Missions/Old.conf","name":"old"}]}]}}
            """);
        MakeAddon(addons, "RHS-StatusQuo_595F2BF2F44836FB", RhsId, "RHS - Status Quo", "0.16.5237", 1000); // no scenarios

        var found = InstalledMods.Scan(addons).Single(m => m.Name == "Zimnitrita").Scenarios;
        Check(found.Select(x => x.Id).SequenceEqual(new[] { "{1A2B3C4D5E6F7081}Missions/Zimnitrita_Conflict.conf", "{1A2B3C4D5E6F7082}Missions/Zimnitrita_GM.conf" }) &&
              found[0].DisplayName == "Conflict - Zimnitrita", "scenarios of the newest version: " + string.Join(", ", found.Select(x => x.DisplayName)));

        var config = Path.Combine(root, "server", "Configs", "server.json");
        var store = new AppSettingsStore(Path.Combine(root, "app.json"));
        store.Save(new AppSettings { SteamCmdDir = Path.Combine(root, "steamcmd"), ServerDir = Path.Combine(root, "server"), ConfigPath = config, ProfileDir = profile });
        // the old server's config already pointed at the map's conflict scenario
        var cfg = ServerConfigJson.CreateDefault();
        var settings = new ServerSettings { ServerName = "x", ScenarioId = "{1A2B3C4D5E6F7081}Missions/Zimnitrita_Conflict.conf" };
        settings.Mods.Add(new ModEntry("597697D81A1EA202", "Zimnitrita"));
        settings.Mods.Add(new ModEntry(RhsId, "RHS - Status Quo"));
        ServerConfigJson.Write(settings, cfg);
        ServerConfigJson.Save(config, cfg);

        var vm = new MainViewModel(new FakeUi(), null, store, FakeWorkshop.Client(new()));
        Check(vm.Scenarios[0].DisplayName == "[Zimnitrita] Conflict - Zimnitrita" && vm.Scenarios[1].DisplayName == "[Zimnitrita] Game Master - Zimnitrita",
              "mod scenarios first, named: " + vm.Scenarios[0].DisplayName);
        Check(vm.SelectedScenario?.DisplayName == "[Zimnitrita] Conflict - Zimnitrita" && !vm.IsDirty, $"saved choice shown by name: {vm.SelectedScenario?.DisplayName}");
        Check(vm.Scenarios.Count(x => x.Id.Contains("Zimnitrita_Conflict")) == 1 && !vm.Scenarios.Any(x => x.Id.Contains("Old.conf")), "no duplicates, old version ignored");

        // switching the map off takes its scenarios away (except the one still chosen)
        vm.Mods[0].IsEnabled = false;
        Check(!vm.Scenarios.Any(x => x.Id.Contains("Zimnitrita_GM")) && vm.Scenarios.Any(x => x.Id.Contains("Zimnitrita_Conflict")), "off → offered no more, chosen kept");
        vm.Mods[0].IsEnabled = true;
        Check(vm.Scenarios.Any(x => x.Id.Contains("Zimnitrita_GM")), "on again → back");
        // picking one sets the config value
        vm.SelectedScenario = vm.Scenarios.First(x => x.Id.Contains("Zimnitrita_GM"));
        Check(vm.ScenarioId == "{1A2B3C4D5E6F7082}Missions/Zimnitrita_GM.conf" && vm.IsDirty, "picked");
        vm.Shutdown();
    }

    private void ModFileDeletion()
    {
        var root = Path.Combine(_tmp, "modfiles");
        var profile = Path.Combine(root, "server", "profile");
        var addons = Path.Combine(profile, "addons");
        var sq = MakeAddon(addons, "RHS-StatusQuo_595F2BF2F44836FB", RhsId, "RHS - Status Quo", "0.16.5237", 3000);
        var cp1 = MakeAddon(addons, "RHS-ContentPack01_1337C0DE5DABBEEF", "1337C0DE5DABBEEF", "RHS - Content Pack 01", "0.16.5236", 5000);
        var old = MakeAddon(addons, "OldMod_6000000000000001", "6000000000000001", "Old Mod", "1.0.0", 2000);
        Directory.CreateDirectory(Path.Combine(addons, "saves"));   // not a mod
        File.WriteAllText(Path.Combine(addons, "saves", "keep.txt"), "x");

        // scanning
        Check(InstalledMods.AddonsDir(profile, "") == addons && InstalledMods.AddonsDir(profile, "-logStats 1 -addonDownloadDir \"D:\\Mods X\"") == "D:\\Mods X", "download folder (default / -addonDownloadDir)");
        var found = InstalledMods.Scan(addons);
        Check(found.Count == 3 && found.All(f => f.Id.Length == 16), "3 mod folders, 'saves' skipped: " + string.Join(",", found.Select(f => f.Name)));
        var sqInfo = found.Single(f => f.Id == RhsId);
        Check(sqInfo.Name == "RHS - Status Quo" && sqInfo.Version == "0.16.5237" && sqInfo.Bytes > 3000, $"meta read: {sqInfo.Name} {sqInfo.Version} {sqInfo.Bytes}");
        Check(InstalledMods.Scan(Path.Combine(root, "nope")).Count == 0, "no folder → empty");
        Check(ServerMonitorViewModel.FormatGb(180_000) == "176 KB" && ServerMonitorViewModel.FormatGb(5 * 1024 * 1024) == "5 MB" && ServerMonitorViewModel.FormatGb(10) == "1 KB", "small mods show KB, not 0 MB");

        // the delete guard: never anything outside "<addons>\Name_<16 hex>"
        var threw = false;
        try { InstalledMods.Delete(sqInfo with { Folder = Path.Combine(root, "server") }, addons); } catch (InvalidOperationException) { threw = true; }
        Check(threw && Directory.Exists(Path.Combine(root, "server")), "refuses a non-mod folder");
        threw = false;
        try { InstalledMods.Delete(sqInfo with { Folder = Path.Combine(addons, "saves") }, addons); } catch (InvalidOperationException) { threw = true; }
        Check(threw && File.Exists(Path.Combine(addons, "saves", "keep.txt")), "refuses 'saves'");

        // view model
        var store = new AppSettingsStore(Path.Combine(root, "app.json"));
        store.Save(new AppSettings { SteamCmdDir = Path.Combine(root, "steamcmd"), ServerDir = Path.Combine(root, "server"),
                                     ConfigPath = Path.Combine(root, "server", "Configs", "server.json"), ProfileDir = profile });
        var ui = new FakeUi();
        var vm = new MainViewModel(ui, null, store, FakeWorkshop.Client(new()));
        vm.Mods.Add(new ModEntry(RhsId, "RHS - Status Quo"));
        vm.Mods.Add(new ModEntry("1337C0DE5DABBEEF", "RHS - Content Pack 01"));
        vm.Mods.Add(new ModEntry("BADC0DEDABBEDA5E", "RHS - Content Pack 02"));   // not downloaded yet
        Check(vm.TrySaveConfig(false), "saved");
        vm.RefreshInstalledMods();
        var rowSq = vm.Mods[0];
        Check(rowSq.IsInstalled && rowSq.InstalledText.StartsWith("0.16.5237 · ") && vm.Mods[2].InstalledText == "안 받음", $"rows: {rowSq.InstalledText} / {vm.Mods[2].InstalledText}");
        Check(vm.ModFilesText.StartsWith("받은 모드 파일 3개") && vm.OrphanModFiles.Count == 1 && vm.OrphanModFiles[0].Name == "Old Mod", vm.ModFilesText + " / " + vm.OrphanModFilesText);
        Check(!vm.IsDirty, "showing files does not mark the config unsaved");

        // delete: "no" keeps everything
        ui.ConfirmAnswer = false;
        vm.DeleteModCommand.Execute(vm.Mods[1]);
        Check(vm.Mods.Count == 3 && Directory.Exists(cp1) && ui.Confirms[^1].Contains("RHS - Content Pack 01") && ui.Confirms[^1].Contains("지웁니다"), "declined");

        // dependency warning (known from an earlier check: Status Quo needs Content Pack 01)
        vm.Mods.Clear();
        var site = FakeWorkshop.Site();
        site.Pages[RhsId] = FakeWorkshop.Page(RhsId, "RHS - Status Quo", "0.16.5237", ("1337C0DE5DABBEEF", "RHS - Content Pack 01", "0.16.5236", null));
        site.Pages["1337C0DE5DABBEEF"] = FakeWorkshop.Page("1337C0DE5DABBEEF", "RHS - Content Pack 01", "0.16.5236");
        var vm2 = new MainViewModel(ui, null, store, FakeWorkshop.Client(site));
        vm2.CheckDependenciesCommand.ExecuteAsync().GetAwaiter().GetResult();
        Check(vm2.Mods.Count == 3, "config reloaded with 3 mods");
        ui.ConfirmAnswer = true;
        vm2.DeleteModCommand.Execute(vm2.Mods.First(m => m.ModId == "1337C0DE5DABBEEF"));
        Check(ui.Confirms[^1].Contains("이 모드가 필요한 모드가 있습니다 — RHS - Status Quo"), "warns about mods that need it:\n" + ui.Confirms[^1]);
        Check(!Directory.Exists(cp1) && vm2.Mods.Count == 2 && !vm2.Mods.Any(m => m.ModId == "1337C0DE5DABBEEF"), "files + row gone");
        Check(!vm2.IsDirty && !ServerConfigJson.Read(ServerConfigJson.Load(vm2.ConfigPath)).Mods.Any(m => m.ModId == "1337C0DE5DABBEEF"), "config saved without it");
        Check(vm2.LogLines.Any(l => l.Contains("모드를 완전히 삭제했습니다: RHS - Content Pack 01")), "logged");

        // never downloaded: list only
        vm2.DeleteModCommand.Execute(vm2.Mods.First(m => m.ModId == "BADC0DEDABBEDA5E"));
        Check(ui.Confirms[^1].Contains("받은 파일은 없습니다") && vm2.Mods.Count == 1, "not downloaded → list only");

        // leftovers: Old Mod is not in the list; Status Quo is and stays
        Check(vm2.CleanOrphanModFilesCommand.CanExecute(null), "cleanup available");
        vm2.CleanOrphanModFilesCommand.Execute(null);
        Check(!Directory.Exists(old) && Directory.Exists(sq) && File.Exists(Path.Combine(addons, "saves", "keep.txt")), "only the leftover was removed");
        Check(vm2.OrphanModFiles.Count == 0 && !vm2.CleanOrphanModFilesCommand.CanExecute(null) && vm2.ModFilesText.StartsWith("받은 모드 파일 1개"), vm2.ModFilesText);
        // an unchecked mod is not a leftover
        vm2.Mods[0].IsEnabled = false;
        vm2.RefreshInstalledMods();
        Check(vm2.OrphanModFiles.Count == 0, "unchecked mod files are kept");
        vm.Shutdown(); vm2.Shutdown();
    }

    private async Task SelfUpdate()
    {
        // parsing
        Check(AppUpdater.ParseVersion("v0.14.0") == new Version(0, 14, 0) && AppUpdater.ParseVersion("0.15") == new Version(0, 15, 0) &&
              AppUpdater.ParseVersion("v1.2.3-beta") == new Version(1, 2, 3) && AppUpdater.ParseVersion("latest") == null, "versions");
        Check(AppUpdater.NormalizeRepository("https://github.com/jeong/RSM.git") == "jeong/RSM" && AppUpdater.NormalizeRepository(" jeong/RSM/ ") == "jeong/RSM" &&
              AppUpdater.NormalizeRepository("RSM") == "", "repository text");

        var gh = new FakeGitHub();
        var http = new HttpClient(gh);
        AppUpdater U(string v) => new(http, "jeong/RSM", AppUpdater.ParseVersion(v)!);

        // checking
        var rel = await U("0.14.0").CheckAsync();
        Check(rel != null && rel.VersionText == "v0.15.0" && rel.Size == gh.Exe.Length && rel.ShaUrl != null && rel.Notes.Contains("자동 업데이트"), "newer release found");
        Check(await U("0.15.0").CheckAsync() == null && await U("0.16.0").CheckAsync() == null, "same/older → nothing");
        gh.WithExe = false;
        Check(await U("0.14.0").CheckAsync() == null, "exe not attached yet → nothing");
        gh.WithExe = true; gh.Prerelease = true;
        Check(await U("0.14.0").CheckAsync() == null, "prerelease ignored");
        gh.Prerelease = false; gh.NoRelease = true;
        Check(await U("0.14.0").CheckAsync() == null, "no release yet");
        gh.NoRelease = false;

        // downloading + checking + swapping (a fake RSM.exe in a temp folder)
        var dir = Path.Combine(_tmp, "selfupdate");
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "RSM.exe");
        File.WriteAllText(exe, "old version");
        var reports = new List<double>();
        var got = await U("0.14.0").DownloadAsync(rel!, exe, new Progress<double>(p => { lock (reports) reports.Add(p); }));
        Check(got == exe + ".new" && File.ReadAllBytes(got).SequenceEqual(gh.Exe), "downloaded");
        gh.ShaOverride = new string('0', 64);
        var bad = false;
        try { await U("0.14.0").DownloadAsync(rel!, exe); } catch (InvalidDataException) { bad = true; }
        Check(bad && !File.Exists(exe + ".new"), "wrong checksum → refused and removed");
        gh.ShaOverride = null;
        got = await U("0.14.0").DownloadAsync(rel!, exe);
        AppUpdater.Swap(exe, got);
        Check(File.ReadAllBytes(exe).SequenceEqual(gh.Exe) && File.ReadAllText(exe + ".old") == "old version" && !File.Exists(got), "swapped, old kept until next start");
        AppUpdater.CleanupLeftovers(exe);
        Check(!File.Exists(exe + ".old") && File.Exists(exe), "leftovers cleaned");

        // view model: start-up finds v0.15.0, user says yes → downloads, swaps, restarts
        File.WriteAllText(exe, "running 0.14.0");
        var store = new AppSettingsStore(Path.Combine(dir, "app.json"));
        store.Save(new AppSettings { SteamCmdDir = Path.Combine(dir, "steamcmd"), ServerDir = Path.Combine(dir, "server"),
                                     ConfigPath = Path.Combine(dir, "server", "Configs", "server.json"), ProfileDir = Path.Combine(dir, "server", "profile"),
                                     AutoUpdateOnLaunch = false, AutoUpdateMods = false });
        var ui = new FakeUi { AppExePath = exe };
        MainViewModel NewVm(FakeUi u, string baked = "jeong/RSM") => new(u, null, store, FakeWorkshop.Client(new()))
        {
            AppVersion = new Version(0, 14, 0),
            BakedUpdateRepository = baked,
            AppUpdaterFactory = repo => new AppUpdater(http, repo, new Version(0, 14, 0)),
        };
        var vm = NewVm(ui);
        Check(vm.AppVersionText == "v0.14.0" && vm.HasBakedRepository && vm.EffectiveUpdateRepository == "jeong/RSM", "version + repo");
        await vm.OnStartupAsync();
        Check(ui.Confirms.Count == 1 && ui.Confirms[0].Contains("v0.15.0") && ui.Confirms[0].Contains("모드 완전 삭제"), "asked with release notes");
        Check(ui.Restarts.SequenceEqual(new[] { (exe, "--updated") }) && File.ReadAllBytes(exe).SequenceEqual(gh.Exe), "replaced and restarting");
        Check(vm.ConfirmClose() && ui.Confirms.Count == 1, "closing for the update asks nothing more");

        // the new one starts: says so and removes RSM.exe.old
        var ui2 = new FakeUi { AppExePath = exe };
        var vm2 = NewVm(ui2);
        vm2.AppUpdaterFactory = repo => new AppUpdater(http, repo, new Version(0, 15, 0));
        vm2.AppVersion = new Version(0, 15, 0);
        await vm2.OnStartupAsync();
        Check(vm2.LogLines.Any(l => l.Contains("v0.15.0(으)로 업데이트되었습니다")) && !File.Exists(exe + ".old") && ui2.Confirms.Count == 0, "after update: logged, cleaned, nothing to ask");
        Check(vm2.AppUpdateStatusText.Contains("최신") && !vm2.AppUpdateAvailable, vm2.AppUpdateStatusText);

        // "not now": the sidebar button waits
        var ui3 = new FakeUi { AppExePath = exe, ConfirmAnswer = false };
        var vm3 = NewVm(ui3);
        await vm3.OnStartupAsync();
        Check(vm3.AppUpdateAvailable && vm3.AppUpdateButtonText == "새 버전 v0.15.0 설치" && ui3.Restarts.Count == 0, "declined → button shown");
        ui3.ConfirmAnswer = true;
        await vm3.InstallAppUpdateCommand.ExecuteAsync();
        Check(ui3.Restarts.Count == 1, "button installs");

        // build.bat build: no repository baked in → uses the one typed in, or says it has none
        var ui4 = new FakeUi { AppExePath = exe };
        var vm4 = NewVm(ui4, baked: "");
        await vm4.OnStartupAsync();
        Check(vm4.AppUpdateStatusText.Contains("저장소가 정해지지 않음") && ui4.Confirms.Count == 0, vm4.AppUpdateStatusText);
        vm4.UpdateRepository = "https://github.com/jeong/RSM";
        Check(vm4.EffectiveUpdateRepository == "jeong/RSM" && new AppSettingsStore(store.FilePath).Load().UpdateRepository == "https://github.com/jeong/RSM", "typed repo used + remembered");
        ui4.ConfirmAnswer = false;
        await vm4.CheckAppUpdateCommand.ExecuteAsync();
        Check(vm4.AppUpdateAvailable, "found via typed repo");

        // automatic check off
        var ui5 = new FakeUi { AppExePath = exe };
        var vm5 = NewVm(ui5);
        vm5.AutoUpdateApp = false;
        var before = gh.Requests;
        await vm5.OnStartupAsync();
        Check(gh.Requests == before && vm5.AppUpdateStatusText.Contains("자동 확인 꺼짐"), "auto check off");
        foreach (var v in new[] { vm, vm2, vm3, vm4, vm5 }) v.Shutdown();
    }

    private const string RhsId = "595F2BF2F44836FB", AceId = "60EAEA0389DB3CC2", CbaId = "5D1880C4AD410C14";

    private (MainViewModel vm, FakeUi ui, FakeWorkshop.FakeSite site, string config) ModUpdateSetup(string name)
    {
        var dir = Path.Combine(_tmp, name);
        var site = new FakeWorkshop.FakeSite();
        site.Pages[RhsId] = FakeWorkshop.Page(RhsId, "RHS - Status Quo", "0.11.0");
        site.Pages[AceId] = FakeWorkshop.Page(AceId, "ACE Carrying", "1.4.2");
        site.Pages[CbaId] = FakeWorkshop.Page(CbaId, "Floating Mod", "2.0.0");
        var config = Path.Combine(dir, "server", "Configs", "server.json");
        var store = new AppSettingsStore(Path.Combine(dir, "app.json"));
        store.Save(new AppSettings
        {
            SteamCmdDir = Path.Combine(dir, "steamcmd"), ServerDir = Path.Combine(dir, "server"),
            ConfigPath = config, ProfileDir = Path.Combine(dir, "server", "profile"),
        });
        var ui = new FakeUi();
        var vm = new MainViewModel(ui, null, store, FakeWorkshop.Client(site));
        // RHS pinned to an old version (as in a game mod-manager export), ACE pinned to the current one, the third floating
        vm.Mods.Add(new ModEntry(RhsId, "RHS - Status Quo", "0.10.4805"));
        vm.Mods.Add(new ModEntry(AceId, "ACE Carrying", "1.4.2"));
        vm.Mods.Add(new ModEntry(CbaId, "Floating Mod", ""));
        Check(vm.TrySaveConfig(false), "saved");
        return (vm, ui, site, config);
    }

    private void ModSearchAndFilter()
    {
        var store = new AppSettingsStore(Path.Combine(_tmp, "modsearch.json"));
        var vm = new MainViewModel(new FakeUi(), null, store);
        var rhs = new ModEntry("595F2BF2F44836FB", "RHS - Status Quo", "0.17.5281");
        var wcs = new ModEntry("64610AFB74AA9842", "WCS_Core", "9.0.1");
        var ace = new ModEntry("65AD7D0D9941A380", "ACE Core Dev", "");
        vm.Mods.Add(rhs); vm.Mods.Add(wcs); vm.Mods.Add(ace);
        // as found on disk (adding to the list looks the files up, and this test has none)
        rhs.Installed = new InstalledMod("595F2BF2F44836FB", "RHS - Status Quo", "0.17.5281", "x", 1);
        ace.Installed = new InstalledMod("65AD7D0D9941A380", "ACE Core Dev", "1.5.41", "y", 1);
        var raised = 0;
        vm.ModFilterChanged += () => raised++;

        Check(!vm.IsModFilterActive && vm.Mods.All(vm.ModMatchesFilter) && vm.ModFilterResultText == "", "no filter: everything");
        vm.ModSearchText = "rhs";
        Check(vm.ModMatchesFilter(rhs) && !vm.ModMatchesFilter(wcs) && raised == 1 && vm.ModFilterResultText == "1개 표시 중", $"name, any case: {vm.ModFilterResultText}");
        vm.ModSearchText = "64610a";
        Check(vm.ModMatchesFilter(wcs) && !vm.ModMatchesFilter(rhs), "part of the ID");
        vm.ModSearchText = "core dev";
        Check(vm.ModMatchesFilter(ace) && !vm.ModMatchesFilter(wcs), "all words must match");
        vm.ModSearchText = "";
        vm.ModListFilter = ModListFilter.Downloaded;
        Check(vm.ModMatchesFilter(rhs) && vm.ModMatchesFilter(ace) && !vm.ModMatchesFilter(wcs), "downloaded only");
        vm.ModListFilter = ModListFilter.NotDownloaded;
        Check(!vm.ModMatchesFilter(rhs) && vm.ModMatchesFilter(wcs), "not downloaded only");
        vm.ModListFilter = ModListFilter.Downloading;
        wcs.DownloadText = "받는 중 12%";
        Check(vm.ModMatchesFilter(wcs) && !vm.ModMatchesFilter(rhs), "downloading only");
        vm.ModSearchText = "zzz";
        Check(vm.ModFilterResultText == "맞는 모드 없음", vm.ModFilterResultText);
        vm.ClearModSearchCommand.Execute(null);
        Check(!vm.IsModFilterActive && vm.ModListFilter == ModListFilter.All, "cleared");
    }

    private void ModVersionCompare()
    {
        Check(ModVersion.IsNewer("9.0.1", "8.4.1") && ModVersion.IsNewer("1.0.33", "1.0.32") && ModVersion.IsNewer("1.10.0", "1.9.9"), "newer");
        Check(!ModVersion.IsNewer("8.4.1", "9.0.1") && !ModVersion.IsNewer("1.7.3", "1.9.0") && !ModVersion.IsNewer("1.0", "1.0.0"), "not newer");
        Check(ModVersion.Compare("0.16.5237", "0.16.5237") == 0 && !ModVersion.IsNewer("", "1.0") && !ModVersion.IsNewer("1.0", ""), "equal / empty");
        var m = new ModEntry("64610AFB74AA9842", "WCS_Core", "9.0.1") { LatestVersion = "8.4.1" };
        Check(!m.HasUpdate, "an older number on the Workshop is not an update");
        m.LatestVersion = "9.0.2";
        Check(m.HasUpdate, "a higher one is");
    }

    // the Workshop page showed older numbers than the pinned ones right after a game update;
    // "updating" WCS_Core 9.0.1 → 8.4.1 broke script compilation on the new game version
    private async Task ModUpdatesNeverDowngrade()
    {
        var (vm, ui, site, config) = ModUpdateSetup("moddown");
        site.Pages[RhsId] = FakeWorkshop.Page(RhsId, "RHS - Status Quo", "0.9.0"); // older than the pinned 0.10.4805
        await vm.CheckModUpdatesAsync(automatic: true);
        var rhs = vm.Mods.First(m => m.ModId == RhsId);
        Check(rhs.Version == "0.10.4805" && !rhs.HasUpdate, $"kept the pinned version: {rhs.Version}");
        Check(ServerConfigJson.Read(ServerConfigJson.Load(config)).Mods.First(m => m.ModId == RhsId).Version == "0.10.4805", "file keeps it too");
        Check(!vm.LogLines.Any(l => l.Contains("모드 업데이트: RHS")), "no downgrade logged as an update");
        Check(vm.LogLines.Any(l => l.Contains("워크숍 페이지의 버전(0.9.0)이 목록의 버전(0.10.4805)보다 낮아서")), "explained once");
        var notes = vm.LogLines.Count(l => l.Contains("워크숍 페이지의 버전(0.9.0)"));
        await vm.CheckModUpdatesAsync(automatic: true);
        Check(vm.LogLines.Count(l => l.Contains("워크숍 페이지의 버전(0.9.0)")) == notes, "not repeated every check");
        Check(ui.Confirms.Count == 0, "nothing asked");
    }

    private void ModDownloadTracking()
    {
        var t0 = new DateTime(2026, 11, 6, 8, 0, 0, DateTimeKind.Utc);
        var d = new ModDownloadProgress();
        d.Begin(new[] { ("61110CC4F1FF9C8A", "Placeables for GM byHeine"), ("64610AFB74AA9842", "WCS_Core") }, t0);
        Check(d.IsVisible && d.Headline.Contains("준비 중") && d.Headline.Contains("2개"), $"waiting: {d.Headline}");
        Check(d.RowText("64610AFB74AA9842") == "받기 대기 중" && d.RowText("595F2BF2F44836FB") == "", "rows: queued / not involved");

        d.OnLine("17:16:20.100 BACKEND      : Addon Download started 61110CC4F1FF9C8A - Placeables for GM byHeine", t0.AddSeconds(5));
        d.OnLine("17:16:20.100 BACKEND      : Downloading 61110CC4F1FF9C8A version 3.1.5", t0.AddSeconds(5));
        d.OnLine("17:16:31.000  BACKEND      : Placeables for GM byHeine: [=======>____________] 35% 209/586 MB", t0.AddSeconds(16));
        d.OnLine("17:16:31.000  BACKEND      : Download speed 21415.36 KB/s", t0.AddSeconds(16));
        Check(d.Headline == "모드 받는 중 1/2" && d.CurrentName == "Placeables for GM byHeine", $"first: {d.Headline} {d.CurrentName}");
        Check(Math.Abs(d.Fraction - 0.35) < 0.001 && d.DetailText == "35% · 209 MB / 586 MB" && d.SpeedText == "20.9 MB/s", $"{d.DetailText} {d.SpeedText}");
        Check(d.RowText("61110CC4F1FF9C8A") == "받는 중 35%", d.RowText("61110CC4F1FF9C8A"));

        // a mod nobody expected (dependency) is added as it starts; the previous one is done
        d.OnLine("BACKEND      : Addon Download started 6ACCAEA5F1CBEBC4 - MGM_System_Pack", t0.AddSeconds(40));
        Check(d.Headline == "모드 받는 중 2/3" && d.RowText("61110CC4F1FF9C8A") == "방금 받음", $"second: {d.Headline}");

        // silence: the box says how long, then that it may be stuck; disk growth counts as life
        d.Tick(t0.AddSeconds(70));
        Check(d.ActivityText.Contains("30초째") && !d.IsStalled, d.ActivityText);
        d.OnDiskSample(1000L * 1024 * 1024, t0.AddSeconds(70));
        d.OnDiskSample(1100L * 1024 * 1024, t0.AddSeconds(80));
        Check(d.ActivityText.StartsWith("모드 폴더에 저장 중") && d.ActivityText.Contains("10.0 MB/s"), d.ActivityText);
        d.Tick(t0.AddSeconds(80 + 125));
        Check(d.IsStalled && d.ActivityText.Contains("멈췄을 수 있어요"), d.ActivityText);
        d.OnNetworkRate(5_000_000, t0.AddSeconds(206));
        d.Tick(t0.AddSeconds(206));
        Check(!d.IsStalled && d.ActivityText.StartsWith("PC가 받는 중"), $"network traffic is life: {d.ActivityText}");

        d.OnLine("BACKEND      : Required addons are ready to use.", t0.AddSeconds(210));
        Check(d.IsVisible && d.IsDone && d.Headline == "모드 준비 완료" && d.DetailText.StartsWith("2개 받음"), $"done: {d.DetailText}");
        Check(d.RowText("6ACCAEA5F1CBEBC4") == "방금 받음" && d.RowText("64610AFB74AA9842") == "", "rows after ready");
        d.Tick(t0.AddSeconds(210 + 31));
        Check(!d.IsVisible && d.IsFinished, "summary goes away");

        // nothing to download: nothing shown at all
        var quiet = new ModDownloadProgress();
        quiet.Begin(Array.Empty<(string, string)>(), t0);
        quiet.OnLine("BACKEND      : Required addons are ready to use.", t0.AddSeconds(3));
        Check(!quiet.IsVisible && quiet.IsFinished, "no box when nothing is fetched");

        // server stopped mid-download: box goes away
        var stopped = new ModDownloadProgress();
        stopped.Begin(new[] { ("64610AFB74AA9842", "WCS_Core") }, t0);
        stopped.OnLine("BACKEND      : Addon Download started 64610AFB74AA9842 - WCS_Core", t0.AddSeconds(1));
        stopped.End(t0.AddSeconds(2));
        Check(!stopped.IsVisible && stopped.RowText("64610AFB74AA9842") == "", "stopped");
    }

    private void LogFileTailFollowsRunLog()
    {
        var logs = Path.Combine(_tmp, "tail", "logs");
        var started = new DateTime(2026, 11, 6, 17, 16, 15);
        Directory.CreateDirectory(Path.Combine(logs, "logs_2026-11-06_17-12-58"));
        File.WriteAllText(Path.Combine(logs, "logs_2026-11-06_17-12-58", "console.log"), "old run\n");
        var tail = new LogFileTail();
        Check(tail.ReadNew(logs, started).Count == 0 && tail.Path == null, "an earlier run's log is not ours");

        var dir = Path.Combine(logs, "logs_2026-11-06_17-16-16");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "console.log");
        using (var w = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("17:16:16.001 ENGINE : 시작\r\n17:17:07.000 BACKEND : Addon Download started 61110CC4F1FF9C8A - Placeables\r\n17:17:08 BACK");
            w.Write(bytes);
            w.Flush();
            var first = tail.ReadNew(logs, started);
            Check(first.Count == 2 && first[0].EndsWith("시작") && first[1].Contains("Addon Download started") && tail.Path == file, $"whole lines only: {string.Join("|", first)}");
            w.Write(System.Text.Encoding.UTF8.GetBytes("END : Download speed 1.0 KB/s\r\n"));
            w.Flush();
            var second = tail.ReadNew(logs, started);
            Check(second.Count == 1 && second[0] == "17:17:08 BACKEND : Download speed 1.0 KB/s", $"rest of the line: {string.Join("|", second)}");
            Check(tail.ReadNew(logs, started).Count == 0, "nothing new");
        }
    }

    private async Task ModUpdates()
    {
        var (vm, ui, site, config) = ModUpdateSetup("modupd");
        Check(vm.AutoUpdateMods && vm.ModCheckMinutes == 30 && vm.ModRestartPolicy == ModRestartPolicy.WhenEmpty, "defaults: on, 30 min, restart when empty");
        Check(vm.CheckModUpdatesCommand.CanExecute(null) && vm.ModsWithUpdates == 0, "nothing known yet");

        // start-up check: pinned RHS goes 0.10.4805 → 0.11.0 and is saved, without asking
        await vm.CheckModUpdatesAsync(automatic: true);
        var rhs = vm.Mods.First(m => m.ModId == RhsId);
        Check(rhs.Version == "0.11.0" && rhs.LatestVersion == "0.11.0" && !rhs.HasUpdate, $"RHS bumped: {rhs.Version}");
        Check(ui.Confirms.Count == 0 && !vm.IsDirty, "automatic: no question, saved");
        var saved = ServerConfigJson.Read(ServerConfigJson.Load(config)).Mods;
        Check(saved.First(m => m.ModId == RhsId).Version == "0.11.0" && saved.First(m => m.ModId == CbaId).Version == "", "config.json has the new version, floating stays floating");
        Check(vm.LogLines.Any(l => l.Contains("RHS - Status Quo 0.10.4805 → 0.11.0")), "logged");
        Check(vm.ModUpdateStatusText.Contains("1개 새 버전으로 바꿈"), vm.ModUpdateStatusText);
        Check(vm.Mods.First(m => m.ModId == CbaId).LatestText == "2.0.0", "latest shown for floating mod");

        // the author publishes again: the check must not use the cached page
        site.Pages[RhsId] = FakeWorkshop.Page(RhsId, "RHS - Status Quo", "0.11.1");
        var before = site.Requests;
        // manual check: asks; "no" leaves the list alone but shows the new version
        ui.ConfirmAnswer = false;
        await vm.CheckModUpdatesCommand.ExecuteAsync();
        Check(site.Requests - before == 3, "fresh lookups for every mod");
        Check(ui.Confirms.Count == 1 && ui.Confirms[0].Contains("0.11.0 → 0.11.1"), "asked with versions");
        Check(rhs.Version == "0.11.0" && rhs.HasUpdate && rhs.LatestText == "0.11.1 ↑" && vm.ModsWithUpdates == 1, $"declined: {rhs.LatestText}");
        Check(vm.ModUpdateStatusText.Contains("새 버전 1개"), vm.ModUpdateStatusText);
        // "yes"
        ui.ConfirmAnswer = true;
        await vm.CheckModUpdatesCommand.ExecuteAsync();
        Check(rhs.Version == "0.11.1" && vm.ModsWithUpdates == 0 && !vm.IsDirty, "accepted and saved");

        // unsaved edits elsewhere: versions still move up but nothing is written behind the user's back
        site.Pages[AceId] = FakeWorkshop.Page(AceId, "ACE Carrying", "1.5.0");
        vm.ServerName = "아직 저장 안 함";
        await vm.CheckModUpdatesAsync(automatic: true);
        Check(vm.Mods.First(m => m.ModId == AceId).Version == "1.5.0" && vm.IsDirty, "bumped, left unsaved");
        Check(ServerConfigJson.Read(ServerConfigJson.Load(config)).Mods.First(m => m.ModId == AceId).Version == "1.4.2", "file untouched");
        Check(vm.LogLines.Any(l => l.Contains("자동 저장은 하지 않았습니다")), "told why");
        Check(vm.TrySaveConfig(false), "save");

        // auto update off: the timer does nothing; manual still works
        vm.AutoUpdateMods = false;
        site.Pages[AceId] = FakeWorkshop.Page(AceId, "ACE Carrying", "1.6.0");
        before = site.Requests;
        await vm.OnModTimerAsync(DateTime.UtcNow.AddHours(5));
        Check(site.Requests == before, "auto off → no checks");
        vm.AutoUpdateMods = true;
        await vm.OnModTimerAsync(DateTime.UtcNow);
        Check(site.Requests == before, "not due yet (checked moments ago)");
        await vm.OnModTimerAsync(DateTime.UtcNow.AddMinutes(31));
        Check(vm.Mods.First(m => m.ModId == AceId).Version == "1.6.0", "due → checked and bumped");

        // reloading the config keeps the known latest versions on the rows
        vm.LoadConfigFrom(vm.ConfigPath);
        Check(vm.Mods.First(m => m.ModId == CbaId).LatestVersion == "2.0.0", "known versions survive a reload");

        // settings remembered
        vm.ModCheckMinutes = 60;
        vm.ModRestartPolicy = ModRestartPolicy.NotifyOnly;
        var again = new AppSettingsStore(Path.Combine(_tmp, "modupd", "app.json")).Load();
        Check(again.ModCheckMinutes == 60 && again.ModRestartPolicy == ModRestartPolicy.NotifyOnly && again.AutoUpdateMods, "remembered");

        // offline: friendly status, nothing changed
        var (vm2, _, _, _) = ModUpdateSetup("modupd-off");
        var offline = new MainViewModel(new FakeUi(), null, new AppSettingsStore(Path.Combine(_tmp, "modupd-off", "app.json")),
                                        FakeWorkshop.Client(new FakeWorkshop.FakeSite { Offline = true }));
        await offline.CheckModUpdatesAsync(automatic: true);
        Check(offline.ModUpdateStatusText.Contains("확인 실패") && offline.Mods.First(m => m.ModId == RhsId).Version == "0.10.4805", $"offline: {offline.ModUpdateStatusText}");
        vm2.Shutdown();
        vm.Shutdown();
    }

    private async Task ModUpdatesWhileServerRuns()
    {
        var (vm, ui, site, _) = ModUpdateSetup("modrun");
        vm.RestartPause = TimeSpan.Zero;
        Directory.CreateDirectory(vm.ServerDir);
        var starts = Path.Combine(_tmp, "modrun", "starts.txt");
        // prints what the real server prints while it starts (from the user's console.log)
        WriteScript(ServerTools.ServerExe(vm.ServerDir), $"""
            echo start >> "{starts}"
            echo "BACKEND      : Addon Download started 595F2BF2F44836FB - RHS - Status Quo"
            echo " BACKEND      : RHS - Status Quo: [=================>__] 85% 163/191 MB"
            sleep 0.3
            echo "BACKEND      : Required addons are ready to use."
            echo "BACKEND      : Direct Join Code: 0268219882"
            echo "DEFAULT      : Entered online game state."
            echo "RPL          : ServerImpl event: authenticating (identity=0x00000003, address=5.5.5.5:1, slots=1/32)"
            echo "BACKEND      : Authenticated player: rplIdentity=0x00000003 identityId=x name=Tester"
            echo "RPL          : ServerImpl event: disconnected (identity=0x00000003), group=4, reason=1"
            sleep 60
            """);
        var players = (int?)2;
        vm.CurrentPlayers = () => players;
        int Starts() => File.Exists(starts) ? File.ReadAllLines(starts).Length : 0;
        async Task WaitFor(Func<bool> cond) { for (var i = 0; i < 100 && !cond(); i++) await Task.Delay(50); }

        vm.StartServerCommand.Execute(null);
        await WaitFor(() => Starts() == 1);
        Check(vm.IsServerRunning && Starts() == 1, "server started");
        await WaitFor(() => vm.StatusText.Contains("모드를 받는 중"));
        Check(vm.StatusText == "서버가 모드를 받는 중 — RHS - Status Quo 85% (163/191 MB)", $"download progress in the status line: {vm.StatusText}");
        await WaitFor(() => vm.StatusText.Contains("온라인"));
        Check(vm.JoinCode == "0268219882" && vm.StatusText == "서버 온라인 — 다이렉트 접속 코드 0268219882", $"online: {vm.StatusText}");
        await WaitFor(() => vm.LogLines.Any(l => l.Contains("접속 문제: Tester")));
        Check(vm.LogLines.Any(l => l.Contains("접속 문제: Tester — 모드 파일이 서버와 다름")), "failed join announced in the console");
        await vm.CheckModUpdatesAsync(automatic: true); // waits for the start-up snapshot, then compares
        var floating = vm.Mods.First(m => m.ModId == CbaId);
        Check(floating.RunningVersion == "2.0.0" && !floating.HasUpdate, $"floating mod runs the newest: {floating.RunningVersion}");
        var rhs = vm.Mods.First(m => m.ModId == RhsId);
        Check(rhs.Version == "0.11.0" && rhs.RunningVersion == "0.10.4805" && rhs.HasUpdate == false, "pinned bumped; server still on the old one");

        // RHS bump means the running server is behind; 2 players online → wait
        Check(vm.ModRestartPending && Starts() == 1 && vm.LogLines.Any(l => l.Contains("접속자 2명이 있어 기다립니다")), "waiting for players to leave");
        await vm.OnModTimerAsync(DateTime.UtcNow);
        Check(Starts() == 1, "still players → no restart");
        players = 0;
        await vm.OnModTimerAsync(DateTime.UtcNow);
        await WaitFor(() => Starts() == 2);
        Check(Starts() == 2 && vm.IsServerRunning && !vm.ModRestartPending, "players gone → restarted");
        await vm.CheckModUpdatesAsync(automatic: true);
        Check(rhs.RunningVersion == "0.11.0" && Starts() == 2, $"after restart the server runs the new version: {rhs.RunningVersion}");

        // floating mod updated while running, policy "now": restart right away even with players
        players = 5;
        vm.ModRestartPolicy = ModRestartPolicy.Now;
        site.Pages[CbaId] = FakeWorkshop.Page(CbaId, "Floating Mod", "2.1.0");
        await vm.CheckModUpdatesAsync(automatic: true);
        await WaitFor(() => Starts() == 3);
        Check(Starts() == 3 && floating.Version == "", "restarted now; floating stays floating");

        // notify only: nothing happens on its own
        vm.ModRestartPolicy = ModRestartPolicy.NotifyOnly;
        await Task.Delay(100);
        await vm.CheckModUpdatesAsync(automatic: true);
        site.Pages[CbaId] = FakeWorkshop.Page(CbaId, "Floating Mod", "2.2.0");
        await vm.CheckModUpdatesAsync(automatic: true);
        Check(Starts() == 3 && floating.HasUpdate && floating.LatestText.Contains("2.2.0 ↑ (서버 2.1.0)") && vm.StatusText.Contains("재시작하면 적용"), $"notify only: {floating.LatestText}");

        // the button: asks, with the player count
        ui.ConfirmAnswer = true;
        ui.Confirms.Clear();
        await vm.CheckModUpdatesCommand.ExecuteAsync();
        await WaitFor(() => Starts() == 4);
        Check(ui.Confirms.Any(c => c.Contains("접속자 5명")) && Starts() == 4, "manual: asked, restarted");

        vm.StopServerCommand.Execute(null);
        await WaitFor(() => !vm.IsServerRunning);
        Check(!vm.IsServerRunning && floating.RunningVersion == "" && !floating.HasUpdate, "server off: nothing pending");
        vm.Shutdown();
    }

    private string WriteScript(string nameOrPath, string body)
    {
        var path = Path.IsPathRooted(nameOrPath) ? nameOrPath : Path.Combine(_tmp, nameOrPath);
        File.WriteAllText(path, "#!/bin/sh\n" + body.Replace("\r", ""));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}

sealed class FakeUi : IUiServices
{
    public List<string> Errors { get; } = new();
    public List<string> Infos { get; } = new();
    public List<string> Opened { get; } = new();
    public List<(string exe, string args)> Elevated { get; } = new();
    public string Clipboard { get; set; } = "";
    public bool ConfirmAnswer { get; set; } = true;
    public bool? YesNoCancelAnswer { get; set; } = true;

    public string? PickFolder(string title, string? initialDir) => null;
    public string? OpenFileResult { get; set; }
    public string? SaveFileResult { get; set; }
    public string? LastSaveInitialPath { get; private set; }
    public string? PickOpenFile(string title, string filter, string? initialPath) => OpenFileResult;
    public string? PickSaveFile(string title, string filter, string? initialPath)
    {
        LastSaveInitialPath = initialPath;
        return SaveFileResult;
    }
    public void ShowInfo(string message) => Infos.Add(message);
    public void ShowError(string message) => Errors.Add(message);
    public List<string> Confirms { get; } = new();
    public bool Confirm(string message)
    {
        Confirms.Add(message);
        return ConfirmAnswer;
    }
    public bool? ConfirmYesNoCancel(string message) => YesNoCancelAnswer;
    public string? GetClipboardText() => Clipboard;
    public void SetClipboardText(string text) => Clipboard = text;
    public void Open(string pathOrUrl) => Opened.Add(pathOrUrl);

    public bool RunElevated(string exe, string args)
    {
        Elevated.Add((exe, args));
        return true;
    }

    public string? AppExePath { get; set; }
    public List<(string exe, string args)> Restarts { get; } = new();
    public void RestartApp(string exePath, string args) => Restarts.Add((exePath, args));
}

/// <summary>Answers like api.github.com/repos/{repo}/releases/latest and serves the release files.</summary>
sealed class FakeGitHub : HttpMessageHandler
{
    public string Tag = "v0.15.0";
    public bool Prerelease;
    public bool WithExe = true;
    public bool NoRelease;
    public byte[] Exe = Enumerable.Range(0, 1_200_000).Select(i => (byte)(i * 7)).ToArray();
    public string? ShaOverride;
    public int Requests;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref Requests);
        var url = request.RequestUri!.ToString();
        HttpResponseMessage Ok(HttpContent c) => new(System.Net.HttpStatusCode.OK) { Content = c };
        if (url.EndsWith("/releases/latest"))
        {
            if (NoRelease || !url.Contains("/repos/jeong/RSM/"))
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            var assets = new JsonArray();
            if (WithExe)
            {
                assets.Add(new JsonObject { ["name"] = "RSM.exe", ["size"] = Exe.Length, ["browser_download_url"] = "https://github.com/jeong/RSM/releases/download/x/RSM.exe" });
                assets.Add(new JsonObject { ["name"] = "RSM.exe.sha256", ["size"] = 64, ["browser_download_url"] = "https://github.com/jeong/RSM/releases/download/x/RSM.exe.sha256" });
            }
            var json = new JsonObject
            {
                ["tag_name"] = Tag, ["draft"] = false, ["prerelease"] = Prerelease,
                ["body"] = "- 모드 완전 삭제\n- 자동 업데이트", ["html_url"] = "https://github.com/jeong/RSM/releases/tag/" + Tag, ["assets"] = assets,
            };
            return Task.FromResult(Ok(new StringContent(json.ToJsonString())));
        }
        if (url.EndsWith("/RSM.exe"))
            return Task.FromResult(Ok(new ByteArrayContent(Exe)));
        if (url.EndsWith("/RSM.exe.sha256"))
        {
            var sha = ShaOverride ?? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Exe)).ToLowerInvariant();
            return Task.FromResult(Ok(new StringContent(sha + "  RSM.exe")));
        }
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}

static class FakeWorkshop
{
    public sealed class FakeSite : HttpMessageHandler
    {
        public Dictionary<string, string> Pages { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Offline { get; init; }
        public int Requests;
        public List<JsonObject>? Listing { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            if (Offline)
                throw new HttpRequestException("offline");
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.TrimEnd('/') == "/workshop" && Listing != null)
            {
                var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
                var page = int.Parse(q["page"] ?? "1");
                IEnumerable<JsonObject> hits = Listing;
                if (!string.IsNullOrEmpty(q["search"]))
                    hits = hits.Where(r => r["name"]!.GetValue<string>().Contains(q["search"]!, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(q["tags"]))
                    hits = hits.Where(r => r["tags"]!.AsArray().Any(t => t!["name"]!.GetValue<string>() == q["tags"]));
                var all = hits.ToList();
                var listHtml = ListPage(all.Count, all.Skip((page - 1) * 16).Take(16));
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(listHtml) });
            }
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            var id = parts.Length >= 3 && parts[^1] == "scenarios" ? parts[^2] + "/scenarios" : parts.Last();
            var resp = Pages.TryGetValue(id, out var html)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(html) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            return Task.FromResult(resp);
        }
    }

    public static WorkshopClient Client(FakeSite site) => new(new HttpClient(site));

    /// <summary>Makes the fake site answer list queries from a fixed catalog: the Site() mods plus
    /// "Mod N" fillers, alternating WEAPONS/VEHICLES tags, filtered by search and tag like the real site.</summary>
    public static void AddListing(FakeSite site, int total)
    {
        var rows = new List<JsonObject>
        {
            Row("5B02128D896F7DE8", "WCS_Stryker", "Worst Case Scenario", new[] { "VEHICLES" }, 45273000, 40000, 0.9),
            Row("5A1B2C3D4E5F6071", "BGONE", "someone", new[] { "MISC" }, 1000, 10, 0.5),
        };
        for (var i = rows.Count; i < total; i++)
            rows.Add(Row((0x6000000000000000UL + (ulong)i).ToString("X16"), $"Mod {i}", "author", new[] { i % 3 == 0 ? "VEHICLES" : "WEAPONS" }, 1000 * i, i, 0.8));
        site.Listing = rows;
    }

    public static JsonObject Row(string id, string name, string author, string[] tags, long size, int subs, double rating)
    {
        var tagArr = new JsonArray();
        foreach (var t in tags)
            tagArr.Add(new JsonObject { ["category"] = null, ["name"] = t });
        return new JsonObject
        {
            ["id"] = id, ["name"] = name, ["type"] = "addon", ["summary"] = "Summary of " + name,
            ["averageRating"] = rating, ["ratingCount"] = 3, ["subscriberCount"] = subs,
            ["currentVersionNumber"] = "1.0.0", ["currentVersionSize"] = size,
            ["previews"] = new JsonArray(new JsonObject
            {
                ["url"] = "https://cdn.example/full.jpg", ["width"] = 773, ["height"] = 435,
                ["thumbnails"] = new JsonObject
                {
                    ["image/jpeg"] = new JsonArray(
                        new JsonObject { ["url"] = "https://cdn.example/576.jpg", ["width"] = 576 },
                        new JsonObject { ["url"] = "https://cdn.example/288.jpg", ["width"] = 288 }),
                },
            }),
            ["tags"] = tagArr,
            ["author"] = new JsonObject { ["id"] = "x", ["username"] = author },
        };
    }

    public static string ListPage(int count, IEnumerable<JsonObject> rows)
    {
        var data = new JsonObject
        {
            ["props"] = new JsonObject
            {
                ["pageProps"] = new JsonObject
                {
                    ["search"] = null, ["page"] = 1,
                    ["assets"] = new JsonObject { ["count"] = count, ["rows"] = new JsonArray(rows.Select(r => (JsonNode)r.DeepClone()).ToArray()) },
                },
            },
            ["page"] = "/workshop",
        };
        return "<html><body><script id=\"__NEXT_DATA__\" type=\"application/json\">" + data.ToJsonString() + "</script></body></html>";
    }

    /// <summary>Stryker needs SpaceCore + Armaments; Armaments needs BGONE (only visible on its own page).</summary>
    public static FakeSite Site()
    {
        var s = new FakeSite();
        s.Pages["5B02128D896F7DE8"] = Page("5B02128D896F7DE8", "WCS_Stryker", "8.4.0",
            ("5E389BB9F58B79A6", "WCS_SpaceCore", "8.4.0", null), ("629B2BA37EFFD577", "WCS_Armaments", "8.4.0", null));
        s.Pages["5E389BB9F58B79A6"] = Page("5E389BB9F58B79A6", "WCS_SpaceCore", "8.4.0");
        s.Pages["629B2BA37EFFD577"] = Page("629B2BA37EFFD577", "WCS_Armaments", "8.4.0", ("5A1B2C3D4E5F6071", "BGONE", "1.0.2", null));
        s.Pages["5A1B2C3D4E5F6071"] = Page("5A1B2C3D4E5F6071", "BGONE", "1.0.2");
        return s;
    }

    public static string Page(string id, string name, string version, params (string id, string name, string version, (string id, string name)[]? nested)[] deps)
    {
        var depArr = new JsonArray();
        foreach (var d in deps)
        {
            var nested = new JsonArray();
            foreach (var n in d.nested ?? Array.Empty<(string, string)>())
                nested.Add(new JsonObject { ["asset"] = new JsonObject { ["id"] = n.id, ["name"] = n.name }, ["version"] = "1.0.0", ["dependencies"] = new JsonArray() });
            depArr.Add(new JsonObject
            {
                ["asset"] = new JsonObject { ["id"] = d.id, ["name"] = d.name },
                ["dependencies"] = nested,
                ["version"] = d.version,
                ["blocked"] = false, ["private"] = false, ["published"] = true, ["totalFileSize"] = 12345,
            });
        }
        var data = new JsonObject
        {
            ["props"] = new JsonObject
            {
                ["pageProps"] = new JsonObject
                {
                    ["pathId"] = id,
                    ["asset"] = new JsonObject
                    {
                        ["id"] = id, ["name"] = name, ["type"] = "addon", ["currentVersionNumber"] = version,
                        ["dependencyTree"] = new JsonObject { ["gameVersion"] = "1.2.1.169" },
                        ["dependencies"] = depArr,
                    },
                },
            },
            ["page"] = "/workshop/[id]",
            ["buildId"] = "test",
        };
        return "<!DOCTYPE html><html><body><div id=\"__next\"></div><script id=\"__NEXT_DATA__\" type=\"application/json\">" +
               data.ToJsonString() + "</script></body></html>";
    }
}

sealed class FakeStats : SystemStatsReader
{
    private ulong _idle, _total;
    private long _sent, _recv;
    public ProcessSample? Proc { get; set; }

    public void Step(ulong idle, ulong total, long sent, long recv)
    {
        _idle += idle; _total += total; _sent += sent; _recv += recv;
    }

    public override CpuTimes? ReadCpuTimes() => new CpuTimes(_idle, _total);
    public override MemoryInfo? ReadMemory() => new MemoryInfo(32L * 1024 * 1024 * 1024, 20L * 1024 * 1024 * 1024);
    public override (long Sent, long Received)? ReadNetworkTotals() => (_sent, _recv);
    public override ProcessSample? ReadProcess(int pid) => Proc;
}

/// <summary>Answers A2S_INFO and A2S_PLAYER like a real server, challenge step included.</summary>
sealed class FakeA2sServer : IDisposable
{
    private readonly System.Net.Sockets.UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _challenge = { 0x12, 0x34, 0x56, 0x78 };
    private readonly string _name, _map;
    private readonly int _max;
    private readonly (string name, float seconds)[] _players;

    public bool InfoChallenged { get; private set; }
    public bool PlayerChallenged { get; private set; }
    public int Port { get; }

    public FakeA2sServer(string name, string map, int max, (string, float)[] players)
    {
        _name = name; _map = map; _max = max; _players = players;
        _udp = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        Port = ((System.Net.IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        _ = Task.Run(Serve);
    }

    public static int FreePort()
    {
        using var u = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        return ((System.Net.IPEndPoint)u.Client.LocalEndPoint!).Port;
    }

    private async Task Serve()
    {
        var head = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        while (!_cts.IsCancellationRequested)
        {
            System.Net.Sockets.UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(_cts.Token); } catch { return; }
            var d = r.Buffer;
            byte[] reply;
            if (d[4] == 0x54)
            {
                var hasChallenge = d.Length >= 25 + 4 && d[^4..].SequenceEqual(_challenge);
                if (!hasChallenge) { reply = head.Concat(new byte[] { 0x41 }).Concat(_challenge).ToArray(); InfoChallenged = true; }
                else
                {
                    var body = new List<byte> { 0x49, 17 };
                    void Str(string s) { body.AddRange(System.Text.Encoding.UTF8.GetBytes(s)); body.Add(0); }
                    Str(_name); Str(_map); Str("ArmaReforger"); Str("Arma Reforger");
                    body.AddRange(BitConverter.GetBytes((short)0));
                    body.Add((byte)_players.Length); body.Add((byte)_max); body.Add(0);
                    body.Add((byte)'d'); body.Add((byte)'w'); body.Add(0); body.Add(0);
                    Str("1.6.0");
                    reply = head.Concat(body).ToArray();
                }
            }
            else if (d[4] == 0x55)
            {
                if (!d[5..9].SequenceEqual(_challenge)) { reply = head.Concat(new byte[] { 0x41 }).Concat(_challenge).ToArray(); PlayerChallenged = true; }
                else
                {
                    var body = new List<byte> { 0x44, (byte)_players.Length };
                    byte i = 0;
                    foreach (var (n, sec) in _players)
                    {
                        body.Add(i++);
                        body.AddRange(System.Text.Encoding.UTF8.GetBytes(n)); body.Add(0);
                        body.AddRange(BitConverter.GetBytes(0));
                        body.AddRange(BitConverter.GetBytes(sec));
                    }
                    reply = head.Concat(body).ToArray();
                }
            }
            else continue;
            await _udp.SendAsync(reply, r.RemoteEndPoint);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
    }
}
