using System.Text.RegularExpressions;

namespace RSM.Core;

/// <summary>A player who could not get in, or got thrown out, and what to tell them.</summary>
public sealed record ConnectionIssue(DateTime TimeUtc, string Who, string Address, KickCause Cause, string Detail)
{
    public string TimeText => TimeUtc.ToLocalTime().ToString("HH:mm:ss");
    public bool HasAddress => Address.Length > 0;

    /// <summary>Ready to paste into Discord/KakaoTalk for that player.</summary>
    public string PlayerMessage =>
        (Who.Length > 0 && !Who.StartsWith('(') ? $"{Who}님, " : "") + Cause.ForPlayer +
        (Detail.Length > 0 ? $"\n(오류 코드: {Detail})" : "");
}

/// <summary>Follows every connection attempt through the server log — authenticating → authenticated →
/// in game → disconnected — and turns failures into <see cref="ConnectionIssue"/>s with plain-Korean
/// explanations for the admin and a message for the player. Thread-safe like <see cref="PlayerTracker"/>.</summary>
public sealed partial class ConnectionLog
{
    private enum Stage { Authenticating, Authenticated, InGame }

    private sealed class Attempt
    {
        public string Identity = "", Address = "", Name = "";
        public Stage Stage;
    }

    public const int MaxIssues = 50;

    private readonly object _lock = new();
    private readonly Func<DateTime> _now;
    private readonly Dictionary<string, Attempt> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _nameByIp = new();
    private readonly List<ConnectionIssue> _issues = new();
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase); // already explained; skip the disconnect that follows
    private bool _shuttingDown;
    private int _version;

    public ConnectionLog(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    public int Version { get { lock (_lock) return _version; } }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ConnectionIssue> Issues()
    {
        lock (_lock)
            return _issues.AsEnumerable().Reverse().ToList();
    }

    /// <summary>New server run: forget who was connecting (issues stay until <see cref="ClearIssues"/>).</summary>
    public void ServerStarted()
    {
        lock (_lock)
        {
            _attempts.Clear();
            _reported.Clear();
            _shuttingDown = false;
        }
    }

    public void ClearIssues()
    {
        lock (_lock)
        {
            _issues.Clear();
            _version++;
        }
    }

    [GeneratedRegex(@"ServerImpl event: authenticating \(identity=(?<id>0x[0-9a-fA-F]+), address=(?<addr>[^,)\s]+)")]
    private static partial Regex AuthenticatingRegex();

    [GeneratedRegex(@"Authenticated player: rplIdentity=(?<id>0x[0-9a-fA-F]+)\b.*?\bname=(?<name>.+?)\s*$")]
    private static partial Regex AuthenticatedRegex();

    [GeneratedRegex(@"### Updating player: PlayerId=\d+, Name=(?<name>[^,]*), rplIdentity=(?<id>0x[0-9a-fA-F]+)")]
    private static partial Regex InGameRegex();

    [GeneratedRegex(@"ServerImpl event: disconnected \(identity=(?<id>0x[0-9a-fA-F]+)\)(?:,\s*group=(?<g>\d+),\s*reason=(?<r>\d+))?")]
    private static partial Regex DisconnectedRegex();

    [GeneratedRegex(@"(?:authentication (?:timeout|timed out|failed)|connection refused)\b.*?\(identity=(?<id>0x[0-9a-fA-F]+)|identity=(?<id2>0x[0-9a-fA-F]+)\b.*?(?:authentication (?:timeout|timed out|failed)|connection refused)", RegexOptions.IgnoreCase)]
    private static partial Regex AuthFailRegex();

    // Arma-style BattlEye line: "Player #0 Name (1.2.3.4:5) has been kicked by BattlEye: Client not responding"
    [GeneratedRegex(@"Player #\d+ (?<name>.+?)(?: \([^)]*\))?(?: -)? (?:has been )?kicked by BattlEye:\s*(?<why>[^']+)", RegexOptions.IgnoreCase)]
    private static partial Regex BeKickRegex();

    /// <summary>Feed every server line. Returns true when a new issue was recorded.</summary>
    public bool OnServerLog(string line)
    {
        if (line.Contains("Game destroyed", StringComparison.Ordinal))
        {
            lock (_lock) _shuttingDown = true;
            return false;
        }
        if (line.IndexOf("identity", StringComparison.OrdinalIgnoreCase) < 0 && line.IndexOf("kicked", StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        Match m;
        lock (_lock)
        {
            if ((m = AuthenticatingRegex().Match(line)).Success)
            {
                var id = m.Groups["id"].Value;
                _attempts[id] = new Attempt { Identity = id, Address = StripPort(m.Groups["addr"].Value), Stage = Stage.Authenticating };
                return false;
            }
            if ((m = AuthenticatedRegex().Match(line)).Success)
            {
                var a = Get(m.Groups["id"].Value);
                a.Name = m.Groups["name"].Value.Trim();
                a.Stage = Stage.Authenticated;
                if (a.Address.Length > 0)
                    _nameByIp[a.Address] = a.Name;
                return false;
            }
            if ((m = InGameRegex().Match(line)).Success)
            {
                var a = Get(m.Groups["id"].Value);
                if (a.Name.Length == 0) a.Name = m.Groups["name"].Value.Trim();
                a.Stage = Stage.InGame;
                return false;
            }
            if ((m = AuthFailRegex().Match(line)).Success)
            {
                var id = m.Groups["id"].Success ? m.Groups["id"].Value : m.Groups["id2"].Value;
                var a = Get(id);
                _attempts.Remove(id);
                _reported.Add(id);
                return Add(a, KickCauses.AuthFailed, "");
            }
            if ((m = DisconnectedRegex().Match(line)).Success)
            {
                var id = m.Groups["id"].Value;
                if (!_attempts.Remove(id, out var a))
                    a = new Attempt { Identity = id, Stage = Stage.InGame };
                if (_shuttingDown || _reported.Remove(id))
                    return false;

                var cause = m.Groups["g"].Success ? KickCauses.Describe(int.Parse(m.Groups["g"].Value), int.Parse(m.Groups["r"].Value)) : null;
                var code = cause?.CodeText ?? "";
                // a known failure code counts at any stage (e.g. timeout mid-game = crashed out);
                // an unknown code from someone already playing is most likely a normal quit
                if (cause is { IsProblem: true, IsKnown: true })
                    return Add(a, cause, code);
                if (cause is { IsProblem: true } && a.Stage != Stage.InGame)
                    return Add(a, cause, code);
                return a.Stage switch
                {
                    Stage.InGame => false,                         // normal leave
                    Stage.Authenticated => Add(a, KickCauses.DroppedWhileJoining, code),
                    _ => Add(a, KickCauses.AuthFailed, code),
                };
            }
            if ((m = BeKickRegex().Match(line)).Success)
            {
                var name = m.Groups["name"].Value.Trim();
                var a = _attempts.Values.FirstOrDefault(x => x.Name == name) ?? new Attempt { Name = name };
                var why = m.Groups["why"].Value.Trim();
                var be = KickCauses.Describe(3, -1) with { Title = "BattlEye가 내보냄: " + TranslateBe(why) };
                if (a.Identity.Length > 0)
                    _reported.Add(a.Identity);
                return Add(a, be, "BattlEye: " + why);
            }
        }
        return false;
    }

    private Attempt Get(string identity)
    {
        if (!_attempts.TryGetValue(identity, out var a))
            _attempts[identity] = a = new Attempt { Identity = identity };
        return a;
    }

    private bool Add(Attempt a, KickCause cause, string detail)
    {
        var who = a.Name.Length > 0 ? a.Name
            : a.Address.Length > 0 && _nameByIp.TryGetValue(a.Address, out var known) ? $"(아마 {known})"
            : "(이름 모름)";
        _issues.Add(new ConnectionIssue(_now(), who, a.Address, cause, detail));
        if (_issues.Count > MaxIssues)
            _issues.RemoveAt(0);
        _version++;
        return true;
    }

    private static string StripPort(string address)
    {
        var i = address.LastIndexOf(':');
        return i > 0 && address.IndexOf(':') == i ? address[..i] : address; // IPv4:port → IPv4; leave IPv6 alone
    }

    private static string TranslateBe(string why) => why.ToLowerInvariant() switch
    {
        var w when w.Contains("not responding") => "클라이언트 응답 없음",
        var w when w.Contains("global ban") => "BattlEye 전체 차단",
        var w when w.Contains("admin ban") => "관리자 차단",
        var w when w.Contains("corrupted") => "데이터 손상",
        var w when w.Contains("restart") => "게임 재시작 필요",
        var w when w.Contains("query timeout") => "응답 시간 초과",
        _ => why,
    };
}
