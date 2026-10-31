using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RSM.Core;

/// <summary>A Workshop category. The site files every mod under tags; these 16 are the category tags.</summary>
public sealed record WorkshopCategory(string Tag, string Label);

public sealed record WorkshopSort(string Value, string Label)
{
    public override string ToString() => Label;
}

public static class WorkshopCatalog
{
    public static IReadOnlyList<WorkshopCategory> Categories { get; } = new[]
    {
        new WorkshopCategory("WEAPONS", "무기"),
        new WorkshopCategory("VEHICLES", "차량"),
        new WorkshopCategory("CHARACTERS", "캐릭터·장비"),
        new WorkshopCategory("SYSTEMS", "시스템"),
        new WorkshopCategory("SCENARIOS_MP", "멀티 시나리오"),
        new WorkshopCategory("SCENARIOS_SP", "싱글 시나리오"),
        new WorkshopCategory("TERRAINS", "지형·맵"),
        new WorkshopCategory("AI", "AI"),
        new WorkshopCategory("GAMEMASTER", "게임 마스터"),
        new WorkshopCategory("EFFECTS", "효과"),
        new WorkshopCategory("PROPS", "소품"),
        new WorkshopCategory("STRUCTURES", "건물"),
        new WorkshopCategory("COMPOSITIONS", "배치물"),
        new WorkshopCategory("CREATURES", "생물"),
        new WorkshopCategory("MISC", "기타"),
        new WorkshopCategory("TESTDEV", "테스트·개발"),
    };

    public static IReadOnlyList<WorkshopSort> Sorts { get; } = new[]
    {
        new WorkshopSort("popularity", "인기순"),
        new WorkshopSort("subscribers", "구독자순"),
        new WorkshopSort("newest", "최신순"),
        new WorkshopSort("rating", "평점순"),
    };

    public static string LabelOf(string tag) =>
        Categories.FirstOrDefault(c => string.Equals(c.Tag, tag, StringComparison.OrdinalIgnoreCase))?.Label ?? tag;
}

/// <summary>One mod in a Workshop list.</summary>
public sealed class WorkshopListItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Author { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Version { get; init; } = "";
    public long SizeBytes { get; init; }
    public int Subscribers { get; init; }
    public double Rating { get; init; }          // 0..1
    public string? ThumbnailUrl { get; init; }
    public List<string> Tags { get; init; } = new();
}

public sealed class WorkshopListPage
{
    public int TotalCount { get; init; }
    public List<WorkshopListItem> Items { get; init; } = new();
}

public static partial class WorkshopPage
{
    public const string ListUrl = "https://reforger.armaplatform.com/workshop";
    public const int PageSize = 16; // the site always answers 16 per page

    /// <summary>Address of one page of the Workshop list, exactly like the site's own filters build it.
    /// Several tags would mean "any of these"; this program asks one category at a time.</summary>
    public static string BuildListUrl(string? search, string? tag, string sort, int page)
    {
        var q = new List<string> { "page=" + Math.Max(1, page), "sort=" + Uri.EscapeDataString(sort) };
        if (!string.IsNullOrWhiteSpace(search))
            q.Add("search=" + Uri.EscapeDataString(search.Trim()));
        if (!string.IsNullOrWhiteSpace(tag))
            q.Add("tags=" + Uri.EscapeDataString(tag));
        return ListUrl + "?" + string.Join("&", q);
    }

    /// <summary>Reads props.pageProps.assets = { count, rows: [...] } from a Workshop list page.</summary>
    public static WorkshopListPage ParseList(string html)
    {
        var m = NextDataRegex().Match(html);
        if (!m.Success)
            throw new FormatException("워크숍 페이지 형식이 바뀌어 목록을 읽을 수 없습니다.");

        JsonNode? root;
        try { root = JsonNode.Parse(m.Groups[1].Value); }
        catch (JsonException ex) { throw new FormatException("워크숍 목록 데이터를 읽을 수 없습니다.", ex); }

        if (root?["props"]?["pageProps"]?["assets"] is not JsonObject assets)
            throw new FormatException("워크숍 페이지 형식이 바뀌어 목록을 읽을 수 없습니다.");

        var items = new List<WorkshopListItem>();
        foreach (var row in (assets["rows"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var id = Str(row, "id").ToUpperInvariant();
            if (!ModEntry.IsValidId(id))
                continue;
            items.Add(new WorkshopListItem
            {
                Id = id,
                Name = Str(row, "name"),
                Author = row["author"] is JsonObject author ? Str(author, "username") : "",
                Summary = Str(row, "summary"),
                Version = Str(row, "currentVersionNumber"),
                SizeBytes = (long)Num(row, "currentVersionSize"),
                Subscribers = (int)Num(row, "subscriberCount"),
                Rating = Num(row, "averageRating"),
                ThumbnailUrl = Thumbnail(row),
                Tags = (row["tags"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                    .Select(t => Str(t, "name").ToUpperInvariant()).Where(t => t.Length > 0).ToList(),
            });
        }

        return new WorkshopListPage { TotalCount = (int)Num(assets, "count"), Items = items };
    }

    // the smallest thumbnail that is at least ~280px wide, else the full preview
    private static string? Thumbnail(JsonObject row)
    {
        if (row["previews"] is not JsonArray previews || previews.FirstOrDefault() is not JsonObject first)
            return null;
        var thumbs = (first["thumbnails"]?["image/jpeg"] as JsonArray)?.OfType<JsonObject>()
            .Select(t => (url: Str(t, "url"), width: Num(t, "width")))
            .Where(t => t.url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.width)
            .ToList();
        var pick = thumbs?.FirstOrDefault(t => t.width >= 280);
        if (pick is { url.Length: > 0 })
            return pick.Value.url;
        var url = Str(first, "url");
        return url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url : null;
    }

    private static double Num(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v)
            return 0;
        if (v.TryGetValue(out double d))
            return d;
        if (v.TryGetValue(out long l))
            return l;
        return v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0;
    }
}

public sealed partial class WorkshopClient
{
    public async Task<WorkshopListPage> SearchAsync(string? search, string? tag, string sort, int page, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(WorkshopPage.BuildListUrl(search, tag, sort, page), ct);
        resp.EnsureSuccessStatusCode();
        return WorkshopPage.ParseList(await resp.Content.ReadAsStringAsync(ct));
    }
}
