using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RSM.Core;

/// <summary>A mod the server has downloaded to disk.</summary>
public sealed record InstalledMod(string Id, string Name, string Version, string Folder, long Bytes)
{
    public string SizeText => ServerMonitorViewModel.FormatGb(Bytes);

    /// <summary>Scenarios the mod brings (from its meta file), e.g. a map's missions.</summary>
    public IReadOnlyList<ScenarioInfo> Scenarios { get; init; } = Array.Empty<ScenarioInfo>();
}

/// <summary>The server's download folder for Workshop mods. The server keeps each mod in
/// "&lt;addons&gt;\&lt;Name&gt;_&lt;ModId&gt;" (e.g. RHS-StatusQuo_595F2BF2F44836FB) with a "meta" file
/// describing it: {"meta":{"id":…,"name":…,"versions":[{"version":"0.16.5237",…}]}}.</summary>
public static partial class InstalledMods
{
    [GeneratedRegex(@"_(?<id>[0-9A-Fa-f]{16})$")]
    private static partial Regex FolderIdRegex();

    [GeneratedRegex(@"-addonDownloadDir\s+(?:""(?<q>[^""]+)""|(?<p>\S+))", RegexOptions.IgnoreCase)]
    private static partial Regex DownloadDirArgRegex();

    /// <summary>Where the server puts downloaded mods: -addonDownloadDir if given, else &lt;profile&gt;\addons.</summary>
    public static string AddonsDir(string profileDir, string? extraArgs)
    {
        var m = DownloadDirArgRegex().Match(extraArgs ?? "");
        if (m.Success)
            return m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["p"].Value;
        return Path.Combine(profileDir, "addons");
    }

    /// <summary>Every mod folder in <paramref name="addonsDir"/>; folders that are not mods (e.g. "saves") are skipped.</summary>
    public static List<InstalledMod> Scan(string addonsDir)
    {
        var result = new List<InstalledMod>();
        if (!Directory.Exists(addonsDir))
            return result;

        foreach (var dir in Directory.EnumerateDirectories(addonsDir))
        {
            var folderName = Path.GetFileName(dir);
            var m = FolderIdRegex().Match(folderName);
            if (!m.Success)
                continue;
            var id = m.Groups["id"].Value.ToUpperInvariant();
            var name = folderName[..m.Index];
            var version = "";
            var scenarios = new List<ScenarioInfo>();
            try
            {
                var metaPath = Path.Combine(dir, "meta");
                if (File.Exists(metaPath))
                {
                    var meta = JsonNode.Parse(File.ReadAllText(metaPath).TrimStart('﻿'))?["meta"];
                    if (meta?["name"]?.GetValue<string>() is { Length: > 0 } n)
                        name = n;
                    if (meta?["versions"] is JsonArray { Count: > 0 } versions && versions[0]?["version"]?.GetValue<string>() is { } v)
                        version = v;
                    // the newest version's scenario list; field names vary, so look for any scenario id ("{GUID}Missions/x.conf")
                    if (meta?["versions"] is JsonArray { Count: > 0 } vs)
                        CollectScenarios(vs[0], scenarios, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                // unreadable meta: still a mod folder, just without name/version
            }
            result.Add(new InstalledMod(id, name, version, dir, ServerTools.DirectorySize(dir)) { Scenarios = scenarios });
        }
        return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void CollectScenarios(JsonNode? node, List<ScenarioInfo> into, HashSet<string> seen, int depth = 0)
    {
        if (node == null || depth > 12)
            return;
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
                CollectScenarios(item, into, seen, depth + 1);
            return;
        }
        if (node is not JsonObject obj)
            return;
        foreach (var (_, value) in obj)
        {
            if (value is JsonValue v && v.TryGetValue(out string? text) && ScenarioCatalog.ParseListOutput(new[] { text }) is [var sid] && seen.Add(sid))
            {
                var title = obj["name"] is JsonValue n && n.TryGetValue(out string? nm) && nm.Trim().Length > 0 ? nm.Trim()
                          : obj["title"] is JsonValue t && t.TryGetValue(out string? tt) && tt.Trim().Length > 0 ? tt.Trim()
                          : ScenarioCatalog.NameFromId(sid);
                into.Add(new ScenarioInfo(sid, title));
            }
            else
                CollectScenarios(value, into, seen, depth + 1);
        }
    }

    /// <summary>Deletes the mod's folder. Refuses anything that is not a "&lt;Name&gt;_&lt;16 hex&gt;" folder
    /// directly inside the addons folder, so a wrong path can never take other files with it.</summary>
    public static void Delete(InstalledMod mod, string addonsDir)
    {
        var folder = Path.GetFullPath(mod.Folder).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetFullPath(addonsDir).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(folder), parent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !FolderIdRegex().IsMatch(Path.GetFileName(folder)))
            throw new InvalidOperationException($"모드 폴더가 아니라서 지우지 않았습니다: {mod.Folder}");
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }
}
