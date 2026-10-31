using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RSM.Core;

/// <summary>Turns whatever the user pastes into a mod list:
///  - the in-game Mod Manager "JSON" tab (objects separated by commas, with or without [ ])
///  - a whole config.json, or its "game" / "mods" part
///  - plain Workshop ids or Workshop links, one per line</summary>
public static partial class ModListParser
{
    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // a 16-hex-digit id; in a workshop link it may be followed by "-Name" (…/workshop/5965550F24A0C152-WhereAmI)
    [GeneratedRegex(@"(?<![0-9A-Fa-f])([0-9A-Fa-f]{16})(?![0-9A-Fa-f])(?:-([^\s/?#""',]+))?")]
    private static partial Regex ModIdRegex();

    public static List<ModEntry> Parse(string? text)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0)
            return new List<ModEntry>();

        var node = TryJson(text);
        if (node == null && text.StartsWith('{'))
            node = TryJson("[" + text.TrimEnd(',', ' ', '\r', '\n', '\t') + "]");

        if (node != null)
        {
            var arr = FindModArray(node);
            if (arr != null)
                return FromJsonArray(arr);
        }

        // Not JSON: collect anything that looks like a mod id. A workshop link also carries the name.
        var result = new List<ModEntry>();
        foreach (Match m in ModIdRegex().Matches(text))
        {
            var id = m.Groups[1].Value.ToUpperInvariant();
            var name = m.Groups[2].Success ? NameFromSlug(m.Groups[2].Value) : "";
            var existing = result.FirstOrDefault(r => r.ModId == id);
            if (existing == null)
                result.Add(new ModEntry(id, name));
            else if (existing.Name.Length == 0 && name.Length > 0)
                existing.Name = name;
        }
        return result;
    }

    private static string NameFromSlug(string slug)
    {
        try { return Uri.UnescapeDataString(slug).Trim(); }
        catch (UriFormatException) { return slug; }
    }

    public static List<ModEntry> FromJsonArray(JsonArray arr)
    {
        var list = new List<ModEntry>();
        foreach (var item in arr)
        {
            if (item is JsonObject o)
            {
                var id = Str(o, "modId");
                if (id.Length == 0)
                    continue;
                var enabled = !(o["enabled"] is JsonValue ev && ev.TryGetValue(out bool on) && !on);
                list.Add(new ModEntry(id, Str(o, "name"), Str(o, "version"), enabled));
            }
            else if (item is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s))
            {
                list.Add(new ModEntry(s));
            }
        }
        return list;
    }

    public static JsonArray ToJsonArray(IEnumerable<ModEntry> mods)
    {
        var arr = new JsonArray();
        foreach (var m in mods)
        {
            var o = new JsonObject { ["modId"] = m.ModId };
            if (m.Name.Length > 0)
                o["name"] = m.Name;
            if (m.Version.Length > 0)
                o["version"] = m.Version;
            arr.Add(o);
        }
        return arr;
    }

    /// <summary>Adds <paramref name="incoming"/> to <paramref name="target"/>. A mod already in the list
    /// keeps its place; an empty name or version never overwrites a filled one.
    /// Returns (added, updated).</summary>
    public static (int added, int updated) Merge(IList<ModEntry> target, IEnumerable<ModEntry> incoming)
    {
        int added = 0, updated = 0;
        foreach (var m in incoming)
        {
            var existing = target.FirstOrDefault(t => string.Equals(t.ModId, m.ModId, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                target.Add(m.Clone());
                added++;
                continue;
            }

            var changed = false;
            if (m.Name.Length > 0 && m.Name != existing.Name) { existing.Name = m.Name; changed = true; }
            if (m.Version.Length > 0 && m.Version != existing.Version) { existing.Version = m.Version; changed = true; }
            if (changed)
                updated++;
        }
        return (added, updated);
    }

    private static JsonNode? TryJson(string text)
    {
        try { return JsonNode.Parse(text, documentOptions: Options); }
        catch (JsonException) { return null; }
    }

    private static JsonArray? FindModArray(JsonNode node)
    {
        switch (node)
        {
            case JsonArray a:
                return a;
            case JsonObject o when o["modId"] != null:
                return new JsonArray(o.DeepClone());
            case JsonObject o when o["mods"] is JsonArray mods:
                return mods;
            case JsonObject o when o["game"] is JsonObject game && game["mods"] is JsonArray gameMods:
                return gameMods;
            default:
                return null;
        }
    }

    private static string Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out string? s) ? s.Trim() : "";
}
