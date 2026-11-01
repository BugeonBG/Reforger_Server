using System.Text.Json;
using System.Text.Json.Nodes;

namespace RSM.Core;

// Scenarios a mod brings, from its Workshop "Scenarios" tab:
// https://reforger.armaplatform.com/workshop/<id>/scenarios lists e.g.
// "Zimnitrita - Game Master" with id "{9D2D546F1E889561}Missions/GM_Zimnitrita.conf".
public static partial class WorkshopPage
{
    /// <summary>Scenario ids and names on a mod's scenarios page. Reads the page's embedded data when it is
    /// there (any object holding a scenario id, with its "name"), otherwise any scenario id in the page text.</summary>
    public static List<ScenarioInfo> ParseScenarios(string html)
    {
        var result = new List<ScenarioInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var m = NextDataRegex().Match(html);
        if (m.Success)
        {
            try
            {
                Walk(JsonNode.Parse(m.Groups[1].Value), result, seen, 0);
            }
            catch (JsonException)
            {
                // fall through to the plain-text scan
            }
        }
        if (result.Count == 0)
            foreach (var id in ScenarioCatalog.ParseListOutput(new[] { System.Net.WebUtility.HtmlDecode(html) }))
                if (seen.Add(id))
                    result.Add(new ScenarioInfo(id, ScenarioCatalog.NameFromId(id)));
        return result;
    }

    private static void Walk(JsonNode? node, List<ScenarioInfo> into, HashSet<string> seen, int depth)
    {
        if (node == null || depth > 25)
            return;
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
                Walk(item, into, seen, depth + 1);
            return;
        }
        if (node is not JsonObject obj)
            return;
        foreach (var (_, value) in obj)
        {
            if (value is JsonValue v && v.TryGetValue(out string? text) && ScenarioCatalog.ParseListOutput(new[] { text }) is [var id] &&
                text.Trim().Length <= id.Length + 2)
            {
                if (seen.Add(id))
                {
                    var name = obj["name"] is JsonValue n && n.TryGetValue(out string? s) && s.Trim().Length > 0 ? s.Trim() : ScenarioCatalog.NameFromId(id);
                    into.Add(new ScenarioInfo(id, name));
                }
            }
            else
                Walk(value, into, seen, depth + 1);
        }
    }
}

public sealed partial class WorkshopClient
{
    /// <summary>The scenarios a mod brings (empty for mods without any).</summary>
    public async Task<List<ScenarioInfo>> GetScenariosAsync(string id, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(WorkshopPage.BaseUrl + id.Trim().ToUpperInvariant() + "/scenarios", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return new List<ScenarioInfo>();
        resp.EnsureSuccessStatusCode();
        return WorkshopPage.ParseScenarios(await resp.Content.ReadAsStringAsync(ct));
    }
}
