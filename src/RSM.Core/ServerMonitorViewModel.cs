using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RSM.Core;

public sealed class PlayerRow
{
    public PlayerRow(string name, TimeSpan duration, string faction = "")
    {
        Name = name.Length > 0 ? name : "(이름 없음)";
        TimeText = ServerMonitorViewModel.FormatDuration(duration);
        Faction = faction;
    }

    public string Name { get; }
    public string TimeText { get; }

    /// <summary>Side the player picked ("US", "USSR", "FIA"); empty until they pick one.</summary>
    public string Faction { get; }
    public bool HasFaction => Faction.Length > 0;
}

/// <summary>The "서버 현황" page: players, CPU, memory, network, uptime, every 2 seconds,
/// with the last 5 minutes kept for the small graphs.</summary>
public sealed partial class ServerMonitorViewModel : ObservableObject
{
    public const int HistoryLength = 150; // 150 × 2 s = 5 minutes
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly SystemStatsReader _stats;
    private readonly A2sClient _a2s;
    private readonly Func<int?> _serverPid;
    private readonly Func<int> _a2sPort;
    private readonly Func<DateTime?> _startedAtUtc;

    private CpuTimes? _lastCpu;
    private (long Sent, long Received)? _lastNet;
    private ProcessSample? _lastProc;
    private int? _lastProcPid;
    private DateTime _lastSampleUtc;
    private double? _serverFps;
    private int _queryTick;
    private CancellationTokenSource? _loop;

    private readonly List<double> _cpuHist = new(), _ramHist = new(), _netHist = new(), _playerHist = new();
    private readonly List<double> _netRawHist = new();

    public ServerMonitorViewModel(SystemStatsReader stats, A2sClient a2s, Func<int?> serverPid, Func<int> a2sPort, Func<DateTime?> startedAtUtc)
    {
        _stats = stats;
        _a2s = a2s;
        _serverPid = serverPid;
        _a2sPort = a2sPort;
        _startedAtUtc = startedAtUtc;
    }

    //============================================================== displayed values
    private bool _isServerRunning;
    private string _playersText = "—";
    private string _playersSub = "서버 꺼짐";
    private string _serverCpuText = "—";
    private string _systemCpuText = "";
    private string _serverRamText = "—";
    private string _systemRamText = "";
    private string _netUpText = "—";
    private string _netDownText = "—";
    private string _fpsText = "—";
    private string _uptimeText = "—";
    private string _serverNameText = "—";
    private string _mapText = "—";
    private string _threadsText = "—";
    private double _serverCpuPercent, _systemCpuPercent, _systemRamPercent, _playersPercent;

    public bool IsServerRunning { get => _isServerRunning; private set => Set(ref _isServerRunning, value); }
    public string PlayersText { get => _playersText; private set => Set(ref _playersText, value); }

    /// <summary>Players connected, from the last A2S answer; null when the server is off or not answering.</summary>
    public int? PlayerCount { get; private set; }
    public string PlayersSub { get => _playersSub; private set => Set(ref _playersSub, value); }
    public string ServerCpuText { get => _serverCpuText; private set => Set(ref _serverCpuText, value); }
    public string SystemCpuText { get => _systemCpuText; private set => Set(ref _systemCpuText, value); }
    public string ServerRamText { get => _serverRamText; private set => Set(ref _serverRamText, value); }
    public string SystemRamText { get => _systemRamText; private set => Set(ref _systemRamText, value); }
    public string NetUpText { get => _netUpText; private set => Set(ref _netUpText, value); }
    public string NetDownText { get => _netDownText; private set => Set(ref _netDownText, value); }
    public string FpsText { get => _fpsText; private set => Set(ref _fpsText, value); }
    public string UptimeText { get => _uptimeText; private set => Set(ref _uptimeText, value); }
    public string ServerNameText { get => _serverNameText; private set => Set(ref _serverNameText, value); }
    public string MapText { get => _mapText; private set => Set(ref _mapText, value); }
    public string ThreadsText { get => _threadsText; private set => Set(ref _threadsText, value); }
    public double ServerCpuPercent { get => _serverCpuPercent; private set => Set(ref _serverCpuPercent, value); }
    public double SystemCpuPercent { get => _systemCpuPercent; private set => Set(ref _systemCpuPercent, value); }
    public double SystemRamPercent { get => _systemRamPercent; private set => Set(ref _systemRamPercent, value); }
    public double PlayersPercent { get => _playersPercent; private set => Set(ref _playersPercent, value); }

    public ObservableCollection<PlayerRow> Players { get; } = new();

    /// <summary>Who is on the server, from the server log (names, factions, join times).</summary>
    public PlayerTracker Tracker { get; init; } = new();

    /// <summary>Join attempts that failed or got kicked, with what to tell the player.</summary>
    public ConnectionLog Connections { get; init; } = new();

    /// <summary>Newest first; refreshed with each sample.</summary>
    public ObservableCollection<ConnectionIssue> ConnectionIssues { get; } = new();
    private int _issuesVersion = -1;

    public void ClearConnectionIssues()
    {
        Connections.ClearIssues();
        RefreshConnectionIssues();
    }

    private void RefreshConnectionIssues()
    {
        var v = Connections.Version;
        if (v == _issuesVersion)
            return;
        _issuesVersion = v;
        ConnectionIssues.Clear();
        foreach (var i in Connections.Issues())
            ConnectionIssues.Add(i);
        OnPropertyChanged(nameof(ConnectionIssueCount));
    }

    public int ConnectionIssueCount => ConnectionIssues.Count;

    // replace the rows only when names/factions/minutes changed, so the list doesn't flicker
    private void ShowPlayers(List<PlayerRow> rows)
    {
        if (rows.Count == Players.Count && rows.Zip(Players).All(t => t.First.Name == t.Second.Name && t.First.Faction == t.Second.Faction && t.First.TimeText == t.Second.TimeText))
            return;
        Players.Clear();
        foreach (var r in rows)
            Players.Add(r);
    }

    /// <summary>Reforger answers A2S player queries with a stand-in name instead of the real one.</summary>
    public static bool IsPlaceholderName(string name) =>
        string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), "player", StringComparison.OrdinalIgnoreCase);

    /// <summary>0..1 values, oldest first, for the small graphs. A new list each sample so bindings refresh.</summary>
    public IReadOnlyList<double> CpuHistory { get; private set; } = Array.Empty<double>();
    public IReadOnlyList<double> RamHistory { get; private set; } = Array.Empty<double>();
    public IReadOnlyList<double> NetHistory { get; private set; } = Array.Empty<double>();
    public IReadOnlyList<double> PlayerHistory { get; private set; } = Array.Empty<double>();

    //============================================================== sampling
    /// <summary>Samples every 2 seconds until <see cref="Stop"/>. Call from the UI thread.</summary>
    public void Start()
    {
        if (_loop != null)
            return;
        _loop = new CancellationTokenSource();
        _ = RunLoopAsync(_loop.Token);
    }

    public void Stop()
    {
        _loop?.Cancel();
        _loop = null;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SampleAsync(DateTime.UtcNow, ct);
                await Task.Delay(Interval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // one bad sample must not stop the page; try again next tick
                try { await Task.Delay(Interval, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    public async Task SampleAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var elapsed = _lastSampleUtc == default ? TimeSpan.Zero : nowUtc - _lastSampleUtc;
        _lastSampleUtc = nowUtc;

        // whole PC: CPU
        var cpu = _stats.ReadCpuTimes();
        if (cpu is { } c && _lastCpu is { } lc && c.Total > lc.Total)
        {
            var busy = 1.0 - (double)(c.Idle - lc.Idle) / (c.Total - lc.Total);
            SystemCpuPercent = Math.Clamp(busy * 100, 0, 100);
            SystemCpuText = $"PC 전체 {SystemCpuPercent:0}%";
        }
        _lastCpu = cpu;

        // whole PC: memory
        var mem = _stats.ReadMemory();
        long totalRam = mem?.TotalBytes ?? 0;
        if (mem is { AvailableBytes: >= 0 } m && m.TotalBytes > 0)
        {
            var used = m.TotalBytes - m.AvailableBytes;
            SystemRamPercent = 100.0 * used / m.TotalBytes;
            SystemRamText = $"PC 전체 {FormatGb(used)} / {FormatGb(m.TotalBytes)} ({SystemRamPercent:0}%)";
        }

        // whole PC: network
        var net = _stats.ReadNetworkTotals();
        double netRate = 0;
        if (net is { } n && _lastNet is { } ln && elapsed.TotalSeconds > 0.2)
        {
            var up = Math.Max(0, n.Sent - ln.Sent) / elapsed.TotalSeconds;
            var down = Math.Max(0, n.Received - ln.Received) / elapsed.TotalSeconds;
            NetUpText = "↑ " + FormatRate(up);
            NetDownText = "↓ " + FormatRate(down);
            netRate = up + down;
        }
        _lastNet = net;

        // the server process
        var pid = _serverPid();
        IsServerRunning = pid != null;
        double serverCpu = 0, serverRamShare = 0;
        if (pid is int id && _stats.ReadProcess(id) is { } ps)
        {
            if (_lastProc is { } lp && _lastProcPid == id && elapsed.TotalSeconds > 0.2)
            {
                serverCpu = Math.Clamp((ps.CpuTime - lp.CpuTime).TotalSeconds / elapsed.TotalSeconds / Environment.ProcessorCount * 100, 0, 100);
                ServerCpuPercent = serverCpu;
                ServerCpuText = $"{serverCpu:0}%";
            }
            else if (_lastProcPid != id)
            {
                ServerCpuText = "측정 중…";
            }
            ServerRamText = FormatGb(ps.WorkingSetBytes);
            serverRamShare = totalRam > 0 ? (double)ps.WorkingSetBytes / totalRam : 0;
            ThreadsText = ps.Threads.ToString(CultureInfo.InvariantCulture);
            _lastProc = ps;
            _lastProcPid = id;
        }
        else
        {
            _lastProc = null;
            _lastProcPid = null;
            ServerCpuPercent = 0;
            ServerCpuText = "—";
            ServerRamText = "—";
            ThreadsText = "—";
        }

        // uptime
        UptimeText = IsServerRunning && _startedAtUtc() is { } started ? FormatDuration(nowUtc - started) : "—";
        if (!IsServerRunning)
            _serverFps = null;
        FpsText = _serverFps is { } fps ? fps.ToString("0", CultureInfo.InvariantCulture) : "—";

        RefreshConnectionIssues();

        // players: the count from the server itself (A2S); names, factions and join times from the server log
        // (Reforger's A2S player answer has no real names: it sends one "player" entry)
        double playerShare = 0;
        if (IsServerRunning)
        {
            var tracked = Tracker.Snapshot();
            var info = await _a2s.QueryInfoAsync("127.0.0.1", _a2sPort(), ct);
            if (info != null)
            {
                PlayerCount = info.Players;
                PlayersText = $"{info.Players} / {info.MaxPlayers}";
                PlayersSub = info.Bots > 0 ? $"봇 {info.Bots}" : "접속 중";
                PlayersPercent = info.MaxPlayers > 0 ? 100.0 * info.Players / info.MaxPlayers : 0;
                playerShare = PlayersPercent / 100;
                ServerNameText = info.Name.Length > 0 ? info.Name : "—";
                MapText = info.Map.Length > 0 ? info.Map : "—";
            }
            else if (tracked.Count > 0)
            {
                PlayerCount = tracked.Count;
                PlayersText = $"{tracked.Count}명";
                PlayersSub = "서버 로그 기준";
                PlayersPercent = 0;
            }
            else
            {
                PlayerCount = null;
                PlayersText = "—";
                PlayersSub = "응답 대기 중 (서버가 다 켜지면 나옵니다)";
                PlayersPercent = 0;
            }

            if (tracked.Count > 0)
            {
                ShowPlayers(tracked.Select(p => new PlayerRow(p.Name, nowUtc - p.JoinedUtc, p.Faction)).ToList());
            }
            else if (info is { Players: > 0 } && _queryTick++ % 3 == 0)
            {
                // nothing in the log (yet): fall back to A2S names, unless they are the "player" placeholder
                var players = await _a2s.QueryPlayersAsync("127.0.0.1", _a2sPort(), ct);
                if (players != null && players.Any(p => !IsPlaceholderName(p.Name)))
                    ShowPlayers(players.OrderByDescending(p => p.Duration).Select(p => new PlayerRow(p.Name, p.Duration)).ToList());
                else
                    ShowPlayers(new List<PlayerRow>());
            }
            else if (info is { Players: 0 })
            {
                ShowPlayers(new List<PlayerRow>());
            }
        }
        else
        {
            PlayerCount = null;
            PlayersText = "—";
            PlayersSub = "서버 꺼짐";
            PlayersPercent = 0;
            ServerNameText = "—";
            MapText = "—";
            ShowPlayers(new List<PlayerRow>());
            Tracker.Clear();
            _queryTick = 0;
        }

        // graphs
        Push(_cpuHist, serverCpu / 100);
        Push(_ramHist, serverRamShare);
        Push(_playerHist, playerShare);
        Push(_netRawHist, netRate);
        var peak = Math.Max(_netRawHist.Max(), 64 * 1024); // scale to the busiest moment, at least 64 KB/s
        _netHist.Clear();
        _netHist.AddRange(_netRawHist.Select(v => v / peak));

        CpuHistory = _cpuHist.ToArray();
        RamHistory = _ramHist.ToArray();
        NetHistory = _netHist.ToArray();
        PlayerHistory = _playerHist.ToArray();
        OnPropertyChanged(nameof(CpuHistory));
        OnPropertyChanged(nameof(RamHistory));
        OnPropertyChanged(nameof(NetHistory));
        OnPropertyChanged(nameof(PlayerHistory));
    }

    [GeneratedRegex(@"\bFPS\s*[:=]\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex FpsRegex();

    /// <summary>Picks the server's FPS out of its console output (printed with -logStats).</summary>
    public void OnServerLog(string line)
    {
        Tracker.OnServerLog(line);
        Connections.OnServerLog(line);
        var m = FpsRegex().Match(line);
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps))
            _serverFps = fps;
    }

    private static void Push(List<double> list, double v)
    {
        list.Add(double.IsFinite(v) ? Math.Clamp(v, 0, 1e12) : 0);
        while (list.Count > HistoryLength)
            list.RemoveAt(0);
    }

    //============================================================== formatting
    public static string FormatGb(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):0.0} GB"
        : bytes >= 1024L * 1024 ? $"{bytes / (1024.0 * 1024):0} MB"
        : $"{Math.Max(1, (bytes + 1023) / 1024)} KB"; // small script-only mods: "0 MB" looked like an empty download

    public static string FormatRate(double bytesPerSecond) =>
        bytesPerSecond >= 1024 * 1024 ? $"{bytesPerSecond / (1024 * 1024):0.0} MB/s"
        : bytesPerSecond >= 1024 ? $"{bytesPerSecond / 1024:0} KB/s"
        : $"{bytesPerSecond:0} B/s";

    public static string FormatDuration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 24) return $"{(int)t.TotalDays}일 {t.Hours}시간";
        if (t.TotalMinutes >= 60) return $"{(int)t.TotalHours}시간 {t.Minutes}분";
        if (t.TotalSeconds >= 60) return $"{t.Minutes}분";
        return $"{t.Seconds}초";
    }
}
