using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RSM.Core;

public sealed record WorkshopDependency(string Id, string Name, string Version);

/// <summary>What the Workshop page says about one mod.</summary>
public sealed class WorkshopMod
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";

    /// <summary>Direct dependencies, plus any nested ones the page already lists.</summary>
    public List<WorkshopDependency> Dependencies { get; init; } = new();
}

/// <summary>A mod that something in the list needs.</summary>
public sealed record RequiredMod(string Id, string Name, string RequiredBy, string RequiredById = "");

public sealed class DependencyReport
{
    /// <summary>Every mod that was looked up, by id.</summary>
    public Dictionary<string, WorkshopMod> Mods { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every dependency found, all the way down, in discovery order (a starting mod that another
    /// starting mod needs is listed too; the caller decides what is missing).</summary>
    public List<RequiredMod> Required { get; } = new();

    /// <summary>Ids that could not be looked up (offline, removed, site changed).</summary>
    public List<string> Failed { get; } = new();
}

/// <summary>Reads mod details from the public Workshop site (reforger.armaplatform.com).
/// Each mod page embeds its data as JSON in &lt;script id="__NEXT_DATA__"&gt;, under
/// props.pageProps.asset, with "dependencies": [{ asset: { id, name }, version, dependencies: [...] }].</summary>
public static partial class WorkshopPage
{
    public const string BaseUrl = "https://reforger.armaplatform.com/workshop/";

    [GeneratedRegex(@"<script id=""__NEXT_DATA__"" type=""application/json""[^>]*>(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex NextDataRegex();

    /// <summary>Null when the page has no mod in it. Throws <see cref="FormatException"/> when the page
    /// does not look like a Workshop page any more (the site changed).</summary>
    public static WorkshopMod? Parse(string html)
    {
        var m = NextDataRegex().Match(html);
        if (!m.Success)
            throw new FormatException("워크숍 페이지 형식이 바뀌어 모드 정보를 읽을 수 없습니다.");

        JsonNode? root;
        try { root = JsonNode.Parse(m.Groups[1].Value); }
        catch (JsonException ex) { throw new FormatException("워크숍 페이지의 데이터를 읽을 수 없습니다.", ex); }

        if (root?["props"]?["pageProps"]?["asset"] is not JsonObject asset)
            return null;

        var deps = new List<WorkshopDependency>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(asset["dependencies"] as JsonArray, deps, seen, depth: 0);

        return new WorkshopMod
        {
            Id = Str(asset, "id").ToUpperInvariant(),
            Name = Str(asset, "name"),
            Version = Str(asset, "currentVersionNumber"),
            Dependencies = deps,
        };
    }

    private static void Collect(JsonArray? list, List<WorkshopDependency> into, HashSet<string> seen, int depth)
    {
        if (list == null || depth > 20)
            return;
        foreach (var item in list.OfType<JsonObject>())
        {
            var asset = item["asset"] as JsonObject;
            var id = asset == null ? "" : Str(asset, "id").ToUpperInvariant();
            if (ModEntry.IsValidId(id) && seen.Add(id))
                into.Add(new WorkshopDependency(id, Str(asset!, "name"), Str(item, "version")));
            Collect(item["dependencies"] as JsonArray, into, seen, depth + 1);
        }
    }

    private static string Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out string? s) ? s.Trim() : "";
}

/// <summary>Looks mods up on the Workshop, remembering answers for the rest of the session.</summary>
public sealed partial class WorkshopClient
{
    private const int MaxLookups = 300;     // a sane ceiling for one dependency walk
    private const int MaxParallel = 4;      // be gentle with Bohemia's site

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, Task<WorkshopMod?>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public WorkshopClient(HttpClient http) => _http = http;

    public Task<WorkshopMod?> GetAsync(string id, CancellationToken ct = default)
    {
        id = id.Trim().ToUpperInvariant();
        var task = _cache.GetOrAdd(id, key => FetchAsync(key, ct));
        // don't remember failures: next time, try again
        task.ContinueWith(t => { if (t.IsFaulted || t.IsCanceled) _cache.TryRemove(id, out _); }, TaskScheduler.Default);
        return task;
    }

    /// <summary>Always asks the site (for update checks: mods change while the program runs).</summary>
    public async Task<WorkshopMod?> GetFreshAsync(string id, CancellationToken ct = default)
    {
        id = id.Trim().ToUpperInvariant();
        var mod = await FetchAsync(id, ct);
        _cache[id] = Task.FromResult(mod);
        return mod;
    }

    private async Task<WorkshopMod?> FetchAsync(string id, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(WorkshopPage.BaseUrl + id, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync(ct);
        return WorkshopPage.Parse(html);
    }

    /// <summary>Looks up <paramref name="rootIds"/> and every mod they need, following each dependency's
    /// own page too, so nothing further down the chain is missed.</summary>
    public async Task<DependencyReport> ResolveAsync(IEnumerable<string> rootIds, CancellationToken ct = default)
    {
        var report = new DependencyReport();
        var roots = new HashSet<string>(rootIds.Select(i => i.Trim().ToUpperInvariant()).Where(ModEntry.IsValidId), StringComparer.OrdinalIgnoreCase);
        var queued = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wave = roots.ToList();

        while (wave.Count > 0 && queued.Count <= MaxLookups)
        {
            using var gate = new SemaphoreSlim(MaxParallel);
            var lookups = wave.Select(async id =>
            {
                await gate.WaitAsync(ct);
                try { return (id, mod: await GetAsync(id, ct), ok: true); }
                catch (Exception ex) when (ex is HttpRequestException or FormatException or TaskCanceledException) { return (id, mod: (WorkshopMod?)null, ok: false); }
                finally { gate.Release(); }
            }).ToList();

            var next = new List<string>();
            foreach (var (id, mod, ok) in await Task.WhenAll(lookups))
            {
                if (!ok || mod == null)
                {
                    report.Failed.Add(id);
                    continue;
                }
                report.Mods[id] = mod;
                foreach (var dep in mod.Dependencies)
                {
                    if (!string.Equals(dep.Id, mod.Id, StringComparison.OrdinalIgnoreCase) && required.Add(dep.Id))
                        report.Required.Add(new RequiredMod(dep.Id, dep.Name, mod.Name.Length > 0 ? mod.Name : mod.Id, mod.Id));
                    if (queued.Add(dep.Id))
                        next.Add(dep.Id);
                }
            }
            wave = next;
        }

        return report;
    }
}
