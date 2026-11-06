namespace RSM.Core;

// Mod updates: mod makers publish new versions often. The program asks the Workshop for each mod's newest
// version (at start-up and every N minutes) and
//  • moves pinned versions ("1.2.3" in the list) up to the newest and saves config.json;
//  • for the running server, compares with the versions it loaded at start and restarts it per the chosen
//    policy (the server only downloads mods when it starts; mods without a version get the newest then).
public sealed partial class MainViewModel
{
    private readonly Dictionary<string, string> _latestKnown = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _runningVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _olderOnWorkshopNoted = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _backgroundCts = new();
    private Task? _runningVersionsTask;
    private Task? _loop;
    private DateTime? _lastModCheckUtc;
    private bool _restartPending;
    private bool _restarting;

    public AsyncRelayCommand CheckModUpdatesCommand { get; }

    public IReadOnlyList<Choice<int>> ModCheckIntervals { get; } = new[]
    {
        new Choice<int>(10, "10분마다"), new Choice<int>(30, "30분마다"), new Choice<int>(60, "1시간마다"), new Choice<int>(180, "3시간마다"),
    };

    public IReadOnlyList<Choice<ModRestartPolicy>> ModRestartPolicies { get; } = new[]
    {
        new Choice<ModRestartPolicy>(ModRestartPolicy.WhenEmpty, "접속자가 없을 때 재시작"),
        new Choice<ModRestartPolicy>(ModRestartPolicy.Now, "바로 재시작"),
        new Choice<ModRestartPolicy>(ModRestartPolicy.NotifyOnly, "알리기만 (직접 재시작)"),
    };

    private bool _autoUpdateMods = true;
    public bool AutoUpdateMods { get => _autoUpdateMods; set => Set(ref _autoUpdateMods, value); }

    private int _modCheckMinutes = 30;
    public int ModCheckMinutes { get => _modCheckMinutes; set => Set(ref _modCheckMinutes, Math.Max(1, value)); }

    private ModRestartPolicy _modRestartPolicy = ModRestartPolicy.WhenEmpty;
    public ModRestartPolicy ModRestartPolicy { get => _modRestartPolicy; set => Set(ref _modRestartPolicy, value); }

    private bool _isCheckingMods;
    public bool IsCheckingMods { get => _isCheckingMods; private set => Set(ref _isCheckingMods, value); }

    private string _modUpdateStatusText = "아직 확인하지 않음";
    public string ModUpdateStatusText { get => _modUpdateStatusText; private set => Set(ref _modUpdateStatusText, value); }

    public int ModsWithUpdates => Mods.Count(m => m.HasUpdate);

    /// <summary>How many players are on the server (null = unknown). Tests replace it.</summary>
    public Func<int?> CurrentPlayers { get; set; }

    /// <summary>A restart is waiting for the last player to leave.</summary>
    public bool ModRestartPending => _restartPending;

    //---------------------------------------------------------------- check
    /// <summary>automatic: start-up / timer — apply without asking. Otherwise (the button) ask first.</summary>
    public async Task CheckModUpdatesAsync(bool automatic)
    {
        if (IsCheckingMods || Mods.Count == 0)
            return;

        // wait for the start-of-server snapshot, so a fresh start is not mistaken for an outdated one
        if (_runningVersionsTask is { IsCompleted: false } snapshot)
            await snapshot;

        IsCheckingMods = true;
        ModUpdateStatusText = "워크숍에서 모드 버전 확인 중…";
        Dictionary<string, string> latest;
        int failed;
        try
        {
            var ids = Mods.Select(m => m.ModId).Where(ModEntry.IsValidId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            (latest, failed) = await FetchLatestVersionsAsync(ids);
        }
        finally
        {
            IsCheckingMods = false;
        }

        _lastModCheckUtc = DateTime.UtcNow;
        var when = DateTime.Now.ToString("HH:mm");
        if (latest.Count == 0 && failed > 0)
        {
            ModUpdateStatusText = $"{when} 확인 실패 — 인터넷 연결이나 워크숍 상태를 확인하세요";
            AppendLog("모드 업데이트를 확인하지 못했습니다 (워크숍에 연결할 수 없음).");
            return;
        }
        foreach (var (id, v) in latest)
            _latestKnown[id] = v;
        foreach (var m in Mods)
            ApplyKnownVersions(m);

        // 1. pinned versions that fell behind → newest. Never downwards: the Workshop page can show an older
        //    number than the one in the list (e.g. right after a game update), and "updating" to it breaks mods.
        var pinned = Mods.Where(m => m.Version.Length > 0 && ModVersion.IsNewer(m.LatestVersion, m.Version)).ToList();
        foreach (var m in Mods.Where(m => m.Version.Length > 0 && ModVersion.IsNewer(m.Version, m.LatestVersion)))
            if (_olderOnWorkshopNoted.Add(m.ModId + "@" + m.LatestVersion))
                AppendLog($"참고: {Label(m)} — 워크숍 페이지의 버전({m.LatestVersion})이 목록의 버전({m.Version})보다 낮아서 그대로 둡니다.");
        var bumped = 0;
        if (pinned.Count > 0)
        {
            var go = automatic
                ? AutoUpdateMods
                : _ui.Confirm($"모드 {pinned.Count}개에 새 버전이 나왔습니다.\n\n• " +
                              string.Join("\n• ", pinned.Take(15).Select(m => $"{Label(m)}  {m.Version} → {m.LatestVersion}")) +
                              (pinned.Count > 15 ? $"\n… 외 {pinned.Count - 15}개" : "") +
                              "\n\n목록의 버전을 새 버전으로 바꾸고 저장할까요?");
            if (go)
                bumped = BumpPinnedVersions(pinned);
        }

        var summary = failed > 0 ? $" · {failed}개는 확인 못 함" : "";

        // 2. the running server has older mods than the Workshop
        if (IsServerRunning)
        {
            var behind = RunningModsBehind();
            if (behind.Count > 0)
            {
                ModUpdateStatusText = $"{when} 확인 · 서버에 새 버전 {behind.Count}개 대기{summary}";
                await HandleRunningServerBehindAsync(behind, automatic);
                return;
            }
        }

        var waiting = Mods.Count(m => m.HasUpdate);
        ModUpdateStatusText = bumped > 0
            ? $"{when} 확인 · {bumped}개 새 버전으로 바꿈{summary}"
            : waiting > 0 ? $"{when} 확인 · 새 버전 {waiting}개 (적용 안 함){summary}"
            : $"{when} 확인 · 모두 최신{summary}";
        if (bumped == 0 && waiting == 0 && !automatic)
            AppendLog("모든 모드가 최신 버전입니다.");
    }

    private int BumpPinnedVersions(List<ModEntry> pinned)
    {
        var wasDirty = IsDirty;
        foreach (var m in pinned)
        {
            AppendLog($"모드 업데이트: {Label(m)} {m.Version} → {m.LatestVersion}");
            m.Version = m.LatestVersion;
        }

        if (wasDirty)
            AppendLog("저장하지 않은 다른 설정이 있어 자동 저장은 하지 않았습니다. 저장을 누르면 새 모드 버전도 함께 저장됩니다.");
        else if (TrySaveConfig(showSuccess: false))
            AppendLog(IsServerRunning ? "새 모드 버전을 저장했습니다. 서버를 다시 시작하면 적용됩니다." : "새 모드 버전을 저장했습니다. 다음에 서버를 시작할 때 받습니다.");
        return pinned.Count;
    }

    private List<ModEntry> RunningModsBehind() =>
        Mods.Where(m => m.IsEnabled && m.LatestVersion.Length > 0 &&
                        _runningVersions.TryGetValue(m.ModId, out var running) && ModVersion.IsNewer(m.LatestVersion, running))
            .ToList();

    private async Task HandleRunningServerBehindAsync(List<ModEntry> behind, bool automatic)
    {
        var list = string.Join(", ", behind.Take(5).Select(m => $"{Label(m)} {_runningVersions[m.ModId]} → {m.LatestVersion}")) +
                   (behind.Count > 5 ? $" 외 {behind.Count - 5}개" : "");
        AppendLog($"서버가 쓰는 모드에 새 버전이 나왔습니다: {list}");
        var players = CurrentPlayers();

        if (!automatic)
        {
            var who = players is { } n ? $"지금 접속자 {n}명" : "접속자 수는 알 수 없음";
            if (_ui.Confirm($"서버가 쓰는 모드 {behind.Count}개에 새 버전이 나왔습니다.\n\n{list}\n\n" +
                            $"서버를 다시 시작해야 적용됩니다 ({who}). 지금 재시작할까요?"))
                await RestartServerForModsAsync();
            else
                StatusText = "모드 새 버전 있음 — 서버를 재시작하면 적용됩니다";
            return;
        }

        switch (ModRestartPolicy)
        {
            case ModRestartPolicy.Now:
                await RestartServerForModsAsync();
                break;
            case ModRestartPolicy.WhenEmpty when players == 0:
                await RestartServerForModsAsync();
                break;
            case ModRestartPolicy.WhenEmpty:
                if (!_restartPending)
                    AppendLog(players is { } p
                        ? $"접속자 {p}명이 있어 기다립니다. 모두 나가면 서버를 재시작해 모드를 업데이트합니다."
                        : "접속자 수를 확인할 수 없어 기다립니다. 접속자가 없는 것이 확인되면 재시작합니다.");
                _restartPending = true;
                StatusText = "모드 새 버전 있음 — 접속자가 나가면 재시작";
                break;
            default:
                StatusText = "모드 새 버전 있음 — 서버를 재시작하면 적용됩니다";
                break;
        }
    }

    //---------------------------------------------------------------- restart
    public async Task RestartServerForModsAsync()
    {
        if (_restarting || !IsServerRunning)
            return;
        _restarting = true;
        _restartPending = false;
        try
        {
            AppendLog("모드 업데이트를 적용하려고 서버를 다시 시작합니다...");
            StopServer();
            for (var i = 0; i < 120 && IsServerRunning; i++)
                await Task.Delay(250);
            if (IsServerRunning)
            {
                AppendLog("서버가 꺼지지 않아 재시작을 취소했습니다.");
                return;
            }
            await Task.Delay(RestartPause);
            if (StartServer(automatic: true))
                AppendLog("서버를 다시 시작했습니다. 서버가 새 모드 버전을 받습니다.");
        }
        finally
        {
            _restarting = false;
        }
    }

    /// <summary>Gap between stop and start so the ports are free again. Tests shorten it.</summary>
    public TimeSpan RestartPause { get; set; } = TimeSpan.FromSeconds(2);

    //---------------------------------------------------------------- timer
    private void StartModUpdateLoop()
    {
        if (_loop != null)
            return;
        _loop = Task.Run(async () =>
        {
            var ct = _backgroundCts.Token;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), ct);
                    var tick = new TaskCompletionSource();
                    // run the check on the UI thread like everything else in the view model
                    Ui(async () =>
                    {
                        try { await OnModTimerAsync(DateTime.UtcNow); }
                        catch (Exception ex) { AppendLog($"모드 업데이트 확인 중 오류: {ex.Message}"); }
                        finally { tick.TrySetResult(); }
                    });
                    await tick.Task;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });
    }

    /// <summary>One timer tick (every minute): a waiting restart, then the regular check when it is due.</summary>
    public async Task OnModTimerAsync(DateTime nowUtc)
    {
        if (_restartPending)
        {
            if (!IsServerRunning)
                _restartPending = false;
            else if (CurrentPlayers() == 0)
            {
                AppendLog("접속자가 모두 나갔습니다.");
                await RestartServerForModsAsync();
                return;
            }
        }

        if (AutoUpdateMods && Mods.Count > 0 && !_restarting &&
            (_lastModCheckUtc is not { } last || nowUtc - last >= TimeSpan.FromMinutes(ModCheckMinutes)))
            await CheckModUpdatesAsync(automatic: true);
    }

    //---------------------------------------------------------------- versions the server runs
    private async Task RecordRunningModVersionsAsync()
    {
        _runningVersions.Clear();
        var enabled = Mods.Where(m => m.IsEnabled && ModEntry.IsValidId(m.ModId)).ToList();
        foreach (var m in enabled.Where(m => m.Version.Length > 0))
            _runningVersions[m.ModId] = m.Version;

        // no version = the server takes the newest at start; ask the Workshop what that is
        var floating = enabled.Where(m => m.Version.Length == 0).Select(m => m.ModId).ToList();
        if (floating.Count > 0)
        {
            var (latest, _) = await FetchLatestVersionsAsync(floating);
            foreach (var (id, v) in latest)
            {
                _runningVersions[id] = v;
                _latestKnown[id] = v;
            }
        }
        if (!IsServerRunning)
            _runningVersions.Clear();
        foreach (var m in Mods)
            ApplyKnownVersions(m);
    }

    private void ForgetRunningModVersions()
    {
        _runningVersions.Clear();
        _restartPending = false;
        foreach (var m in Mods)
            m.RunningVersion = "";
    }

    private void ApplyKnownVersions(ModEntry m)
    {
        if (_latestKnown.TryGetValue(m.ModId, out var latest))
            m.LatestVersion = latest;
        m.RunningVersion = IsServerRunning && _runningVersions.TryGetValue(m.ModId, out var running) ? running : "";
    }

    private async Task<(Dictionary<string, string> latest, int failed)> FetchLatestVersionsAsync(List<string> ids)
    {
        var latest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var failed = 0;
        using var gate = new SemaphoreSlim(4);
        var lookups = ids.Select(async id =>
        {
            await gate.WaitAsync();
            try { return (id, mod: await _workshop.GetFreshAsync(id), ok: true); }
            catch (Exception ex) when (ex is HttpRequestException or FormatException or TaskCanceledException) { return (id, mod: (WorkshopMod?)null, ok: false); }
            finally { gate.Release(); }
        }).ToList();

        foreach (var (id, mod, ok) in await Task.WhenAll(lookups))
        {
            if (!ok)
                failed++;
            else if (mod != null && mod.Version.Length > 0)
                latest[id] = mod.Version;
        }
        return (latest, failed);
    }

    private static string Label(ModEntry m) => m.Name.Length > 0 ? m.Name : m.ModId;
}

public sealed partial class MainViewModel
{
    private string _joinCode = "";

    /// <summary>Direct Join Code the server got this run (empty until it is online).</summary>
    public string JoinCode { get => _joinCode; private set => Set(ref _joinCode, value); }

    // status line follows what the server is doing while it starts: mod downloads, then online + join code
    private void OnServerOutput(string line)
    {
        if (LogLine.DownloadProgress(line) is { } progress)
            Ui(() => StatusText = $"서버가 모드를 받는 중 — {progress}");
        else if (line.Contains("Addon Download started", StringComparison.OrdinalIgnoreCase))
            Ui(() => StatusText = "서버가 모드를 받기 시작했습니다");
        else if (line.Contains("Required addons are ready", StringComparison.OrdinalIgnoreCase))
            Ui(() =>
            {
                StatusText = "모드 준비 완료 — 월드를 불러오는 중";
                RefreshInstalledMods();
            });
        else if (LogLine.JoinCode(line) is { Length: > 0 } code)
            Ui(() => JoinCode = code);
        else if (line.Contains("Entered online game state", StringComparison.OrdinalIgnoreCase))
            Ui(() =>
            {
                StatusText = JoinCode.Length > 0 ? $"서버 온라인 — 다이렉트 접속 코드 {JoinCode}" : "서버 온라인";
                AppendLog(StatusText);
            });
    }
}
