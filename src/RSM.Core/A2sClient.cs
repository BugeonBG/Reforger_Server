using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RSM.Core;

public sealed record A2sInfo(string Name, string Map, int Players, int MaxPlayers, int Bots, string Version);

public sealed record A2sPlayer(string Name, int Score, TimeSpan Duration);

/// <summary>Valve's A2S server query over UDP — the same thing Steam's server browser uses, and what
/// the dedicated server answers on its "a2s" port (17777 by default).
/// https://developer.valvesoftware.com/wiki/Server_queries</summary>
public sealed class A2sClient
{
    private static readonly byte[] Header = { 0xFF, 0xFF, 0xFF, 0xFF };
    private const byte InfoRequest = 0x54;    // 'T'
    private const byte InfoResponse = 0x49;   // 'I'
    private const byte PlayerRequest = 0x55;  // 'U'
    private const byte PlayerResponse = 0x44; // 'D'
    private const byte Challenge = 0x41;      // 'A'

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(1.5);

    public async Task<A2sInfo?> QueryInfoAsync(string host, int port, CancellationToken ct = default)
    {
        var request = Concat(Header, new[] { InfoRequest }, Encoding.ASCII.GetBytes("Source Engine Query\0"));
        var reply = await AskAsync(host, port, request, appendChallenge: (req, ch) => Concat(req, ch), ct);
        return reply != null && reply.Length > 5 && reply[4] == InfoResponse ? ParseInfo(reply) : null;
    }

    public async Task<List<A2sPlayer>?> QueryPlayersAsync(string host, int port, CancellationToken ct = default)
    {
        var request = Concat(Header, new[] { PlayerRequest }, Header); // "no challenge yet" = FF FF FF FF
        var reply = await AskAsync(host, port, request, appendChallenge: (_, ch) => Concat(Header, new[] { PlayerRequest }, ch), ct);
        return reply != null && reply.Length > 5 && reply[4] == PlayerResponse ? ParsePlayers(reply) : null;
    }

    // Sends a request; if the server answers with a challenge, sends it again with the challenge.
    private async Task<byte[]?> AskAsync(string host, int port, byte[] request, Func<byte[], byte[], byte[]> appendChallenge, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var target = new IPEndPoint(await ResolveAsync(host, ct), port);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await udp.SendAsync(request, target, ct);
                var reply = await ReceiveAsync(udp, ct);
                if (reply == null)
                    return null;
                if (reply.Length >= 9 && reply[4] == Challenge)
                {
                    request = appendChallenge(request, reply[5..9]);
                    continue;
                }
                // split (multi-packet) answers start with FE FF FF FF; not worth reassembling for this
                return reply.Length >= 4 && reply[0] == 0xFF && reply[1] == 0xFF && reply[2] == 0xFF && reply[3] == 0xFF ? reply : null;
            }
        }
        catch (SocketException)
        {
            // nothing listening (Windows reports ICMP "port unreachable" this way)
        }
        return null;
    }

    private async Task<byte[]?> ReceiveAsync(UdpClient udp, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            var r = await udp.ReceiveAsync(timeout.Token);
            return r.Buffer;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<IPAddress> ResolveAsync(string host, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host) || host == "0.0.0.0")
            return IPAddress.Loopback;
        if (IPAddress.TryParse(host, out var ip))
            return ip;
        var all = await Dns.GetHostAddressesAsync(host, ct);
        return all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? IPAddress.Loopback;
    }

    //------------------------------------------------------------------------------------------
    public static A2sInfo? ParseInfo(byte[] data)
    {
        try
        {
            var r = new Reader(data, 5);
            r.Byte();                 // protocol
            var name = r.String();
            var map = r.String();
            r.String();               // folder
            r.String();               // game
            r.Short();                // app id
            int players = r.Byte();
            int max = r.Byte();
            int bots = r.Byte();
            r.Byte(); r.Byte(); r.Byte(); r.Byte(); // server type, environment, visibility, VAC
            var version = r.Remaining > 0 ? r.String() : "";
            return new A2sInfo(name, map, players, max, bots, version);
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    public static List<A2sPlayer>? ParsePlayers(byte[] data)
    {
        try
        {
            var r = new Reader(data, 5);
            int count = r.Byte();
            var list = new List<A2sPlayer>(count);
            for (var i = 0; i < count && r.Remaining > 0; i++)
            {
                r.Byte(); // index
                var name = r.String();
                var score = r.Int();
                var seconds = r.Float();
                list.Add(new A2sPlayer(name, score, TimeSpan.FromSeconds(Math.Max(0, seconds))));
            }
            return list;
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private sealed class Reader
    {
        private readonly byte[] _d;
        private int _p;
        public Reader(byte[] d, int start) { _d = d; _p = start; }
        public int Remaining => _d.Length - _p;
        public byte Byte() => _d[_p++];
        public short Short() { var v = BitConverter.ToInt16(Take(2)); return v; }
        public int Int() => BitConverter.ToInt32(Take(4));
        public float Float() => BitConverter.ToSingle(Take(4));
        public string String()
        {
            var end = Array.IndexOf(_d, (byte)0, _p);
            if (end < 0) throw new IndexOutOfRangeException();
            var s = Encoding.UTF8.GetString(_d, _p, end - _p);
            _p = end + 1;
            return s;
        }
        private byte[] Take(int n)
        {
            if (_p + n > _d.Length) throw new IndexOutOfRangeException();
            var b = _d[_p..(_p + n)];
            _p += n;
            return b;
        }
    }
}
