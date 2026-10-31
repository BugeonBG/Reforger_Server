using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RSM.Core;

/// <summary>Reads and writes the dedicated server's config.json.
/// Writing changes only the keys this program manages; anything else already in the file
/// (settings added by hand, newer server options) is left exactly as it was.</summary>
public static class ServerConfigJson
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // keep Korean server names readable in the file instead of \uXXXX
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonObject Parse(string text)
    {
        var node = JsonNode.Parse(text, documentOptions: ReadOptions);
        return node as JsonObject ?? throw new FormatException("config.json의 최상위가 { } 객체가 아닙니다.");
    }

    public static JsonObject Load(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    public static string Serialize(JsonObject root) => root.ToJsonString(WriteOptions);

    /// <summary>UTF-8 without BOM.</summary>
    public static void Save(string path, JsonObject root)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // write next to it first so a crash never leaves a half-written config behind
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(root) + Environment.NewLine, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    //------------------------------------------------------------------------------------------
    public static ServerSettings Read(JsonObject root)
    {
        var d = new ServerSettings();
        var s = new ServerSettings
        {
            BindAddress = Str(root, "bindAddress", d.BindAddress),
            BindPort = Int(root, "bindPort", d.BindPort),
            PublicAddress = Str(root, "publicAddress", d.PublicAddress),
            PublicPort = Int(root, "publicPort", d.PublicPort),
            A2sPort = Int(Obj(root, "a2s"), "port", d.A2sPort),
        };

        var rcon = Obj(root, "rcon");
        s.RconEnabled = rcon != null;
        s.RconPort = Int(rcon, "port", d.RconPort);
        s.RconPassword = Str(rcon, "password", d.RconPassword);
        s.RconPermission = Str(rcon, "permission", d.RconPermission);

        var game = Obj(root, "game");
        s.ServerName = Str(game, "name", d.ServerName);
        s.Password = Str(game, "password", d.Password);
        s.AdminPassword = Str(game, "passwordAdmin", d.AdminPassword);
        s.ScenarioId = Str(game, "scenarioId", d.ScenarioId);
        s.MaxPlayers = Int(game, "maxPlayers", d.MaxPlayers);
        s.Visible = Bool(game, "visible", d.Visible);
        s.CrossPlatform = Bool(game, "crossPlatform", d.CrossPlatform);

        if (game?["admins"] is JsonArray admins)
            s.Admins = admins.Select(a => a?.ToString().Trim() ?? "").Where(a => a.Length > 0).ToList();

        var gp = Obj(game, "gameProperties");
        s.ServerMaxViewDistance = Int(gp, "serverMaxViewDistance", d.ServerMaxViewDistance);
        s.NetworkViewDistance = Int(gp, "networkViewDistance", d.NetworkViewDistance);
        s.DisableThirdPerson = Bool(gp, "disableThirdPerson", d.DisableThirdPerson);
        s.BattlEye = Bool(gp, "battlEye", d.BattlEye);
        s.FastValidation = Bool(gp, "fastValidation", d.FastValidation);
        s.VonCanTransmitCrossFaction = Bool(gp, "VONCanTransmitCrossFaction", d.VonCanTransmitCrossFaction);

        if (game?["mods"] is JsonArray mods)
            s.Mods = ModListParser.FromJsonArray(mods);

        return s;
    }

    //------------------------------------------------------------------------------------------
    /// <summary>Puts <paramref name="s"/> into <paramref name="root"/>, touching only managed keys.</summary>
    public static void Write(ServerSettings s, JsonObject root)
    {
        root["bindAddress"] = s.BindAddress;
        root["bindPort"] = s.BindPort;
        root["publicAddress"] = s.PublicAddress;
        root["publicPort"] = s.PublicPort;

        var a2s = EnsureObj(root, "a2s");
        a2s["address"] ??= "0.0.0.0";
        a2s["port"] = s.A2sPort;

        if (s.RconEnabled)
        {
            var rcon = EnsureObj(root, "rcon");
            rcon["address"] ??= "0.0.0.0";
            rcon["port"] = s.RconPort;
            rcon["password"] = s.RconPassword;
            rcon["permission"] = s.RconPermission;
        }
        else
        {
            root.Remove("rcon");
        }

        var game = EnsureObj(root, "game");
        game["name"] = s.ServerName;
        game["password"] = s.Password;
        game["passwordAdmin"] = s.AdminPassword;
        game["admins"] = new JsonArray(s.Admins.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
        game["scenarioId"] = s.ScenarioId;
        game["maxPlayers"] = s.MaxPlayers;
        game["visible"] = s.Visible;
        game["crossPlatform"] = s.CrossPlatform;

        var gp = EnsureObj(game, "gameProperties");
        gp["serverMaxViewDistance"] = s.ServerMaxViewDistance;
        gp["networkViewDistance"] = s.NetworkViewDistance;
        gp["disableThirdPerson"] = s.DisableThirdPerson;
        gp["battlEye"] = s.BattlEye;
        gp["fastValidation"] = s.FastValidation;
        gp["VONCanTransmitCrossFaction"] = s.VonCanTransmitCrossFaction;

        // only switched-on mods reach the server; switched-off ones live in the side file (DisabledModsFile)
        game["mods"] = ModListParser.ToJsonArray(s.Mods.Where(m => m.IsEnabled));
    }

    public static JsonObject CreateDefault()
    {
        var root = new JsonObject();
        Write(new ServerSettings(), root);
        return root;
    }

    //------------------------------------------------------------------------------------------
    private static JsonObject? Obj(JsonObject? parent, string key) => parent?[key] as JsonObject;

    private static JsonObject EnsureObj(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject o)
            return o;
        o = new JsonObject();
        parent[key] = o;
        return o;
    }

    private static string Str(JsonObject? o, string key, string fallback) =>
        o?[key] is JsonValue v && v.TryGetValue(out string? str) ? str : fallback;

    private static int Int(JsonObject? o, string key, int fallback)
    {
        if (o?[key] is not JsonValue v)
            return fallback;
        if (v.TryGetValue(out int i))
            return i;
        if (v.TryGetValue(out double dbl))
            return (int)dbl;
        if (v.TryGetValue(out string? str) && int.TryParse(str, out i))
            return i;
        return fallback;
    }

    private static bool Bool(JsonObject? o, string key, bool fallback) =>
        o?[key] is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;
}
