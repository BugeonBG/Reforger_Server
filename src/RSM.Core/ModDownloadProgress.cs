using System.Globalization;
using System.Text.RegularExpressions;

namespace RSM.Core;

/// <summary>What the server is doing with Workshop mods while it starts.
///
/// The server only downloads mods at start-up, one after the other, and tells little about it:
///   BACKEND : Addon Download started 1337C0DE5DABBEEF - RHS - Content Pack 01
///   BACKEND : Downloading 1337C0DE5DABBEEF version 0.16.5236
///   BACKEND : RHS - Content Pack 01: [=========>______] 46% 2780/6040 MB   (every ~10 s, sometimes minutes apart)
///   BACKEND : Download speed 39773.56 KB/s
///   BACKEND : Required addons are ready to use.
/// A big mod can stay silent for minutes, which looks like a hang. So besides the log this also
/// watches the addons folder grow and the PC's download rate, and says which of them is moving.</summary>
public sealed partial class ModDownloadProgress : ObservableObject
{
    /// <summary>No log progress, no folder growth and no network traffic for this long = probably stuck.</summary>
    public static readonly TimeSpan StallAfter = TimeSpan.FromMinutes(2);

    /// <summary>The finished summary stays on screen this long.</summary>
    public static readonly TimeSpan ShowDoneFor = TimeSpan.FromSeconds(30);

    /// <summary>PC download rate that counts as "the server is receiving something".</summary>
    public const double ActiveNetBytesPerSec = 256 * 1024;

    private sealed class Item
    {
        public string Id = "";
        public string Name = "";
        public string Version = "";
        public bool Started, Done;
        public double Percent;
        public double DoneMb, TotalMb;
    }

    private readonly List<Item> _items = new();
    private Item? _current;
    private DateTime _lastLog, _lastDiskChange, _lastNet, _begun, _finished;
    private long _lastDiskBytes = -1;
    private DateTime _lastDiskSample;
    private double _diskRate, _netRate, _logSpeed;
    private bool _active, _finishedOk;

    //------------------------------------------------------------------ bindable state
    private bool _isVisible;
    private string _headline = "";
    private string _currentName = "";
    private string _detailText = "";
    private string _speedText = "";
    private string _activityText = "";
    private double _fraction;
    private bool _isStalled;
    private bool _isDone;
    private bool _hasCurrent;

    /// <summary>Show the download box: the server is getting mods, or has just finished.</summary>
    public bool IsVisible { get => _isVisible; private set => Set(ref _isVisible, value); }

    /// <summary>"모드 받는 중 2/5", "모드 받을 준비 중", "모드 준비 완료".</summary>
    public string Headline { get => _headline; private set => Set(ref _headline, value); }

    public string CurrentName { get => _currentName; private set => Set(ref _currentName, value); }

    public bool HasCurrent { get => _hasCurrent; private set => Set(ref _hasCurrent, value); }

    /// <summary>"46% · 2.7 / 5.9 GB".</summary>
    public string DetailText { get => _detailText; private set => Set(ref _detailText, value); }

    /// <summary>"38.8 MB/s".</summary>
    public string SpeedText { get => _speedText; private set => Set(ref _speedText, value); }

    /// <summary>Proof of life: what is moving right now, or for how long nothing has.</summary>
    public string ActivityText { get => _activityText; private set => Set(ref _activityText, value); }

    /// <summary>0..1 for the current mod's bar.</summary>
    public double Fraction { get => _fraction; private set => Set(ref _fraction, value); }

    public bool IsStalled { get => _isStalled; private set => Set(ref _isStalled, value); }

    public bool IsDone { get => _isDone; private set => Set(ref _isDone, value); }

    /// <summary>Nothing left to watch or show for this run.</summary>
    public bool IsFinished => !_active && !IsVisible;

    /// <summary>Changes whenever a mod's row text may have changed.</summary>
    public int Version { get; private set; }

    //------------------------------------------------------------------ input
    /// <summary>Server started. <paramref name="expected"/>: mods it will probably download (not on disk yet,
    /// or an update is known) — the server may still fetch others, which are added as they start.</summary>
    public void Begin(IEnumerable<(string Id, string Name)> expected, DateTime now)
    {
        _items.Clear();
        foreach (var (id, name) in expected)
            if (_items.All(i => !i.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                _items.Add(new Item { Id = id.ToUpperInvariant(), Name = name.Length > 0 ? name : id });
        _current = null;
        _active = true;
        _finishedOk = false;
        _begun = _lastLog = now;
        _lastDiskChange = _lastNet = DateTime.MinValue;
        _lastDiskBytes = -1;
        _diskRate = _netRate = _logSpeed = 0;
        Version++;
        Refresh(now);
    }

    /// <summary>Server stopped (or crashed) before the mods were ready.</summary>
    public void End(DateTime now)
    {
        _active = false;
        if (!_finishedOk)
            _finished = DateTime.MinValue; // nothing to show afterwards
        Version++;
        Refresh(now);
    }

    /// <summary>A line of server output. Returns true if it was about mod downloads.</summary>
    public bool OnLine(string line, DateTime now)
    {
        if (!_active || string.IsNullOrEmpty(line))
            return false;

        if (StartedRegex().Match(line) is { Success: true } s)
        {
            if (_current != null && !_current.Done)
            {
                _current.Done = true;
                _current.Percent = 100;
            }
            var item = Find(s.Groups["id"].Value);
            if (item == null)
            {
                item = new Item { Id = s.Groups["id"].Value.ToUpperInvariant() };
                _items.Add(item);
            }
            item.Name = s.Groups["name"].Value.Trim();
            item.Started = true;
            item.Done = false;
            item.Percent = 0;
            item.DoneMb = item.TotalMb = 0;
            _current = item;
            _logSpeed = 0;
        }
        else if (VersionRegex().Match(line) is { Success: true } v)
        {
            if (Find(v.Groups["id"].Value) is { } item)
                item.Version = v.Groups["ver"].Value;
        }
        else if (ProgressRegex().Match(line) is { Success: true } p)
        {
            var name = p.Groups["name"].Value.Trim();
            var item = _items.FirstOrDefault(i => i.Started && i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? _current;
            if (item == null)
            {
                item = new Item { Id = "", Name = name, Started = true };
                _items.Add(item);
            }
            var unit = p.Groups["unit"].Value.ToUpperInvariant();
            item.Percent = Math.Clamp(Num(p.Groups["pct"].Value), 0, 100);
            item.DoneMb = ToMb(Num(p.Groups["done"].Value), unit);
            item.TotalMb = ToMb(Num(p.Groups["total"].Value), unit);
            _current = item;
        }
        else if (SpeedRegex().Match(line) is { Success: true } sp)
        {
            _logSpeed = ToMb(Num(sp.Groups["v"].Value), sp.Groups["u"].Value.ToUpperInvariant()) * 1024 * 1024;
        }
        else if (line.Contains("Required addons are ready", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var i in _items.Where(i => i.Started))
            {
                i.Done = true;
                i.Percent = 100;
            }
            _active = false;
            _finishedOk = true;
            _finished = now;
        }
        else
        {
            return false;
        }

        _lastLog = now;
        Version++;
        Refresh(now);
        return true;
    }

    /// <summary>Size of the addons folder, sampled every few seconds while mods are expected.</summary>
    public void OnDiskSample(long bytes, DateTime now)
    {
        if (_lastDiskBytes >= 0 && bytes != _lastDiskBytes)
        {
            var seconds = (now - _lastDiskSample).TotalSeconds;
            if (seconds > 0.2 && bytes > _lastDiskBytes)
                _diskRate = (bytes - _lastDiskBytes) / seconds;
            _lastDiskChange = now;
        }
        else if ((now - _lastDiskChange).TotalSeconds > 6)
        {
            _diskRate = 0;
        }
        _lastDiskBytes = bytes;
        _lastDiskSample = now;
        Refresh(now);
    }

    /// <summary>The PC's download rate (bytes/s).</summary>
    public void OnNetworkRate(double bytesPerSecond, DateTime now)
    {
        _netRate = bytesPerSecond;
        if (bytesPerSecond >= ActiveNetBytesPerSec)
            _lastNet = now;
    }

    /// <summary>Once a second: keeps "N초째" counting and hides the box after a while.</summary>
    public void Tick(DateTime now) => Refresh(now);

    /// <summary>Text for the mod's row on the mods page: "대기 중", "받는 중 46%", "방금 받음"; empty = nothing to say.</summary>
    public string RowText(string modId)
    {
        if (!IsVisible || Find(modId) is not { } item)
            return "";
        if (item.Done)
            return "방금 받음";
        if (item.Started)
            return $"받는 중 {item.Percent:0}%";
        return _active ? "받기 대기 중" : "";
    }

    //------------------------------------------------------------------ view
    private void Refresh(DateTime now)
    {
        var started = _items.Count(i => i.Started);
        var total = _items.Count;
        var showDone = _finishedOk && now - _finished < ShowDoneFor && started > 0;
        IsVisible = (_active && total > 0) || showDone;
        IsDone = showDone && !_active;
        if (!IsVisible)
        {
            IsStalled = false;
            return;
        }

        if (IsDone)
        {
            var mb = _items.Where(i => i.Started).Sum(i => i.TotalMb);
            Headline = "모드 준비 완료";
            HasCurrent = false;
            CurrentName = "";
            DetailText = mb > 0 ? $"{started}개 받음 · {FormatMb(mb)}" : $"{started}개 받음";
            SpeedText = "";
            Fraction = 1;
            ActivityText = "이제 월드를 불러옵니다";
            IsStalled = false;
            return;
        }

        // n-th mod of this run: those finished plus the one in progress
        var position = _items.Count(i => i.Started && (i.Done || i == _current));
        Headline = _current == null ? $"모드 받을 준비 중 · {total}개 예정" : $"모드 받는 중 {position}/{Math.Max(total, position)}";

        HasCurrent = _current != null;
        CurrentName = _current?.Name ?? "";
        if (_current is { } c)
        {
            Fraction = c.Percent / 100.0;
            DetailText = c.TotalMb > 0 ? $"{c.Percent:0}% · {FormatMb(c.DoneMb)} / {FormatMb(c.TotalMb)}" : "크기 확인 중…";
        }
        else
        {
            Fraction = 0;
            DetailText = "서버가 설정을 읽고 워크숍에 연결하는 중";
        }

        var speed = _diskRate > 0 ? _diskRate : _logSpeed;
        SpeedText = speed > 0 ? FormatRate(speed) : "";

        // proof of life
        var lastAny = Max(_lastLog, _lastDiskChange, _lastNet);
        var quiet = now - lastAny;
        IsStalled = quiet >= StallAfter;
        if (now - _lastDiskChange < TimeSpan.FromSeconds(6) && _diskRate > 0)
            ActivityText = $"모드 폴더에 저장 중 · {FormatRate(_diskRate)}";
        else if (now - _lastNet < TimeSpan.FromSeconds(6))
            ActivityText = $"PC가 받는 중 · {FormatRate(_netRate)}";
        else if (quiet < TimeSpan.FromSeconds(15))
            ActivityText = "진행 중";
        else if (!IsStalled)
            ActivityText = $"{Ago(quiet)}째 새 소식 없음 — 큰 모드는 몇 분씩 조용할 수 있어요";
        else
            ActivityText = $"{Ago(quiet)}째 변화 없음 — 멈췄을 수 있어요. 인터넷 연결이나 워크숍 상태를 확인하세요";
    }

    private Item? Find(string id) =>
        id.Length == 0 ? null : _items.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static DateTime Max(params DateTime[] times) => times.Max();

    private static double Num(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double ToMb(double value, string unit) => unit switch
    {
        "KB" => value / 1024,
        "GB" => value * 1024,
        _ => value,
    };

    public static string FormatMb(double mb) =>
        mb >= 1024 ? (mb / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " GB" : mb.ToString("0", CultureInfo.InvariantCulture) + " MB";

    public static string FormatRate(double bytesPerSecond)
    {
        var mb = bytesPerSecond / (1024 * 1024);
        return mb >= 1
            ? mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB/s"
            : (bytesPerSecond / 1024).ToString("0", CultureInfo.InvariantCulture) + " KB/s";
    }

    private static string Ago(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{(int)t.TotalSeconds}초" : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes}분" : $"{(int)t.TotalHours}시간";

    [GeneratedRegex(@"Addon Download started (?<id>[0-9A-Fa-f]{16})\s*-\s*(?<name>.+)$")]
    private static partial Regex StartedRegex();

    [GeneratedRegex(@"Downloading (?<id>[0-9A-Fa-f]{16}) version (?<ver>\S+)")]
    private static partial Regex VersionRegex();

    // "BACKEND      : RHS - Status Quo: [==>_________________] 7% 15/191 MB"
    [GeneratedRegex(@"BACKEND\s*:\s*(?<name>.+?):\s*\[[=>_]+\]\s*(?<pct>\d{1,3})%\s*(?<done>[\d.]+)/(?<total>[\d.]+)\s*(?<unit>[KMG]B)")]
    private static partial Regex ProgressRegex();

    [GeneratedRegex(@"Download speed\s+(?<v>[\d.]+)\s*(?<u>[KMG]B)/s", RegexOptions.IgnoreCase)]
    private static partial Regex SpeedRegex();
}
