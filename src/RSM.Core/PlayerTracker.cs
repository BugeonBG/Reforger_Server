using System.Text.RegularExpressions;

namespace RSM.Core;

/// <summary>One player the server log says is on the server.</summary>
public sealed record TrackedPlayer(string Identity, string Name, int? PlayerId, string Faction, DateTime JoinedUtc);

/// <summary>Who is on the server, read from the server's own output.
/// Reforger's A2S player answer has no real names (it sends a single "player" entry), but the log
/// has everything: "Authenticated player: rplIdentity=0x00000000 identityId=… name=ArtRoxF486",
/// "### Updating player: PlayerId=1, Name=…, rplIdentity=0x…", "… has joined faction #AR-Faction_US (US)",
/// and "ServerImpl event: disconnected (identity=0x00000000)" when someone leaves.
/// Lines arrive on the output thread; reads come from the UI thread, so everything is locked.</summary>
public sealed partial class PlayerTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<string, TrackedPlayer> _byIdentity = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTime> _now;
    private int _version;

    public PlayerTracker(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    /// <summary>Goes up on every change, so the screen only rebuilds its list when something happened.</summary>
    public int Version { get { lock (_lock) return _version; } }

    public IReadOnlyList<TrackedPlayer> Snapshot()
    {
        lock (_lock)
            return _byIdentity.Values.OrderBy(p => p.JoinedUtc).ThenBy(p => p.PlayerId ?? int.MaxValue).ToList();
    }

    public void Clear()
    {
        lock (_lock)
        {
            if (_byIdentity.Count == 0)
                return;
            _byIdentity.Clear();
            _version++;
        }
    }

    [GeneratedRegex(@"Authenticated player: rplIdentity=(?<id>0x[0-9a-fA-F]+)\b.*?\bname=(?<name>.+?)\s*$")]
    private static partial Regex AuthenticatedRegex();

    [GeneratedRegex(@"### (?:Updating|Creating) player: PlayerId=(?<pid>\d+), Name=(?<name>[^,]*)(?:, rplIdentity=(?<id>0x[0-9a-fA-F]+))?")]
    private static partial Regex UpdatingRegex();

    [GeneratedRegex(@"BattlEye Server: Adding player identity=(?<id>0x[0-9a-fA-F]+), name='(?<name>.*)'")]
    private static partial Regex BeAddingRegex();

    [GeneratedRegex(@"player (?<name>.+?) \(playerID = (?<pid>\d+)\b.*?\) has joined faction \S+ \((?<faction>[^)]*)\)")]
    private static partial Regex FactionRegex();

    [GeneratedRegex(@"ServerImpl event: disconnected \(identity=(?<id>0x[0-9a-fA-F]+)\)")]
    private static partial Regex DisconnectedRegex();

    [GeneratedRegex(@"BattlEye Server: Removing player identity=(?<id>0x[0-9a-fA-F]+)")]
    private static partial Regex BeRemovingRegex();

    [GeneratedRegex(@"### Disconnecting player: PlayerId=(?<pid>\d+)")]
    private static partial Regex DisconnectingRegex();

    /// <summary>Feed every line the server prints. Returns true when the player list changed.</summary>
    public bool OnServerLog(string line)
    {
        // cheap filter: almost every line is something else
        if (line.IndexOf("player", StringComparison.OrdinalIgnoreCase) < 0 && line.IndexOf("disconnected", StringComparison.Ordinal) < 0)
            return false;

        Match m;
        if ((m = AuthenticatedRegex().Match(line)).Success)
            return Upsert(m.Groups["id"].Value, m.Groups["name"].Value, null);
        if ((m = UpdatingRegex().Match(line)).Success)
        {
            var pid = int.Parse(m.Groups["pid"].Value);
            return m.Groups["id"].Success
                ? Upsert(m.Groups["id"].Value, m.Groups["name"].Value, pid)
                : SetPlayerIdByName(m.Groups["name"].Value, pid);
        }
        if ((m = BeAddingRegex().Match(line)).Success)
            return Upsert(m.Groups["id"].Value, m.Groups["name"].Value, null);
        if ((m = FactionRegex().Match(line)).Success)
            return SetFaction(int.Parse(m.Groups["pid"].Value), m.Groups["name"].Value, m.Groups["faction"].Value);
        if ((m = DisconnectedRegex().Match(line)).Success || (m = BeRemovingRegex().Match(line)).Success)
            return Remove(p => string.Equals(p.Identity, m.Groups["id"].Value, StringComparison.OrdinalIgnoreCase));
        if ((m = DisconnectingRegex().Match(line)).Success)
        {
            var pid = int.Parse(m.Groups["pid"].Value);
            return Remove(p => p.PlayerId == pid);
        }
        return false;
    }

    private bool Upsert(string identity, string name, int? playerId)
    {
        name = name.Trim();
        lock (_lock)
        {
            if (_byIdentity.TryGetValue(identity, out var p))
            {
                var updated = p with
                {
                    Name = name.Length > 0 ? name : p.Name,
                    PlayerId = playerId ?? p.PlayerId,
                };
                if (updated == p)
                    return false;
                _byIdentity[identity] = updated;
            }
            else
            {
                _byIdentity[identity] = new TrackedPlayer(identity, name, playerId, "", _now());
            }
            _version++;
            return true;
        }
    }

    private bool SetPlayerIdByName(string name, int playerId)
    {
        lock (_lock)
        {
            var p = _byIdentity.Values.FirstOrDefault(x => x.Name == name.Trim() && x.PlayerId == null);
            if (p == null)
                return false;
            _byIdentity[p.Identity] = p with { PlayerId = playerId };
            _version++;
            return true;
        }
    }

    private bool SetFaction(int playerId, string name, string faction)
    {
        lock (_lock)
        {
            var p = _byIdentity.Values.FirstOrDefault(x => x.PlayerId == playerId)
                    ?? _byIdentity.Values.FirstOrDefault(x => x.Name == name);
            if (p == null || p.Faction == faction)
                return false;
            _byIdentity[p.Identity] = p with { Faction = faction };
            _version++;
            return true;
        }
    }

    private bool Remove(Func<TrackedPlayer, bool> match)
    {
        lock (_lock)
        {
            var gone = _byIdentity.Values.Where(match).Select(p => p.Identity).ToList();
            foreach (var id in gone)
                _byIdentity.Remove(id);
            if (gone.Count == 0)
                return false;
            _version++;
            return true;
        }
    }
}
