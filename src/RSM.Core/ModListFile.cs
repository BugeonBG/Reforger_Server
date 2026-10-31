using System.Text;
using System.Text.Json.Nodes;

namespace RSM.Core;

/// <summary>A portable copy of the mod list, for moving the server to another folder or PC.
///
/// .json — what the program reads back exactly:
///   { "format": "rsm-mod-list", "formatVersion": 1, "savedAt": "...", "mods": [ { modId, name, version, enabled } ] }
///
/// .txt  — readable in Notepad, one mod per line, and also read back:
///   # comment lines start with #
///   5B02128D896F7DE8 | WCS_Stryker | 최신 | 활성
/// </summary>
public static class ModListFile
{
    public const string Format = "rsm-mod-list";
    public const string DialogFilterSave = "모드 목록 JSON (*.json)|*.json|텍스트 파일 (*.txt)|*.txt";
    public const string DialogFilterOpen = "모드 목록 (*.json;*.txt)|*.json;*.txt|모든 파일 (*.*)|*.*";

    private const string Latest = "최신";
    private const string On = "활성";
    private const string Off = "비활성";

    public static string DefaultFileName(DateTime now) => $"모드목록_{now:yyyy-MM-dd}.json";

    public static void Save(string path, IEnumerable<ModEntry> mods, DateTime now)
    {
        var list = mods.ToList();
        var text = string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase)
            ? ToText(list, now)
            : ToJson(list, now);

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        // BOM so Notepad on older Windows opens the Korean text correctly; .NET reads it either way
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public static string ToJson(IReadOnlyList<ModEntry> mods, DateTime now)
    {
        var arr = new JsonArray();
        foreach (var m in mods)
        {
            arr.Add(new JsonObject
            {
                ["modId"] = m.ModId,
                ["name"] = m.Name,
                ["version"] = m.Version,
                ["enabled"] = m.IsEnabled,
            });
        }
        var root = new JsonObject
        {
            ["format"] = Format,
            ["formatVersion"] = 1,
            ["savedAt"] = now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["note"] = "리포저 서버 관리자의 모드 목록입니다. '모드' 화면의 '목록 불러오기'로 다시 불러올 수 있습니다. version이 비어 있으면 최신 버전을 씁니다.",
            ["count"] = mods.Count,
            ["mods"] = arr,
        };
        return ServerConfigJson.Serialize(root) + Environment.NewLine;
    }

    public static string ToText(IReadOnlyList<ModEntry> mods, DateTime now)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 리포저 서버 관리자 모드 목록");
        sb.AppendLine($"# 저장한 때: {now:yyyy-MM-dd HH:mm}   모드 {mods.Count}개 (활성 {mods.Count(m => m.IsEnabled)}개)");
        sb.AppendLine("# '모드' 화면의 '목록 불러오기'로 이 파일을 다시 불러올 수 있습니다.");
        sb.AppendLine("# 모드 ID | 이름 | 버전 | 상태");
        sb.AppendLine("#");
        foreach (var m in mods)
        {
            var name = m.Name.Replace("|", "/").Replace("\r", " ").Replace("\n", " ").Trim();
            sb.AppendLine($"{m.ModId} | {name} | {(m.Version.Length > 0 ? m.Version : Latest)} | {(m.IsEnabled ? On : Off)}");
        }
        return sb.ToString();
    }

    public static List<ModEntry> Load(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    /// <summary>Reads this program's .json or .txt list. Anything else that names mods (a config.json,
    /// the in-game Mod Manager JSON, links, bare ids) is accepted too.</summary>
    public static List<ModEntry> Parse(string text)
    {
        text = text.TrimStart('﻿');
        var trimmed = text.TrimStart();

        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
        {
            var fromText = ParseText(text);
            if (fromText.Count > 0)
                return fromText;
        }
        return ModListParser.Parse(text);
    }

    private static List<ModEntry> ParseText(string text)
    {
        var result = new List<ModEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var parts = line.Split('|').Select(p => p.Trim()).ToArray();
            var id = parts[0].ToUpperInvariant();
            if (!ModEntry.IsValidId(id))
                return new List<ModEntry>(); // not our format; let the general parser try
            var name = parts.Length > 1 ? parts[1] : "";
            var version = parts.Length > 2 && parts[2] != Latest ? parts[2] : "";
            var enabled = !(parts.Length > 3 && parts[3] == Off);
            if (result.All(r => r.ModId != id))
                result.Add(new ModEntry(id, name, version, enabled));
        }
        return result;
    }
}
