using System.Text;
using System.Text.Json.Nodes;

namespace RSM.Core;

/// <summary>Mods that are switched off. The server's config.json only lists switched-on mods (an unknown
/// key there could make the server refuse the file), so the switched-off ones are kept next to it:
/// Configs\server.json → Configs\server.disabled-mods.json. The server never reads this file.</summary>
public static class DisabledModsFile
{
    public static string PathFor(string configPath) => Path.ChangeExtension(configPath, ".disabled-mods.json");

    public static List<ModEntry> Load(string configPath)
    {
        var path = PathFor(configPath);
        if (!File.Exists(path))
            return new List<ModEntry>();
        try
        {
            var mods = ModListParser.Parse(File.ReadAllText(path, Encoding.UTF8));
            foreach (var m in mods)
                m.IsEnabled = false;
            return mods;
        }
        catch (IOException)
        {
            return new List<ModEntry>();
        }
    }

    /// <summary>Writes the switched-off mods, or removes the file when there are none.</summary>
    public static void Save(string configPath, IEnumerable<ModEntry> mods)
    {
        var path = PathFor(configPath);
        var off = mods.Where(m => !m.IsEnabled).ToList();
        if (off.Count == 0)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }
        var root = new JsonObject
        {
            ["comment"] = "리포저 서버 관리자에서 비활성화한 모드 목록입니다. 서버는 이 파일을 읽지 않습니다.",
            ["mods"] = ModListParser.ToJsonArray(off),
        };
        File.WriteAllText(path, ServerConfigJson.Serialize(root) + Environment.NewLine, new UTF8Encoding(false));
    }
}
