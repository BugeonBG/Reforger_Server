using System.Text.Json;

namespace RSM.Core;

// Mod scenarios from the Workshop's "Scenarios" tab of each switched-on mod, remembered between runs
// (mod-scenarios.json next to the program settings) so the list is complete even before going online.
public sealed partial class MainViewModel
{
    private readonly Dictionary<string, List<ScenarioInfo>> _workshopScenarios = new(StringComparer.OrdinalIgnoreCase);
    private bool _scenarioCacheLoaded;
    private Task? _scenarioFetch;

    private string ScenarioCachePath => Path.Combine(Path.GetDirectoryName(_store.FilePath) ?? ".", "mod-scenarios.json");

    private sealed record CachedScenario(string Id, string Name);

    private void LoadScenarioCache()
    {
        if (_scenarioCacheLoaded)
            return;
        _scenarioCacheLoaded = true;
        try
        {
            if (!File.Exists(ScenarioCachePath))
                return;
            var data = JsonSerializer.Deserialize<Dictionary<string, List<CachedScenario>>>(File.ReadAllText(ScenarioCachePath));
            foreach (var (mod, list) in data ?? new())
                _workshopScenarios[mod] = list.Select(s => new ScenarioInfo(s.Id, s.Name)).ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // a broken cache is just refetched
        }
    }

    private void SaveScenarioCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScenarioCachePath)!);
            var data = _workshopScenarios.ToDictionary(kv => kv.Key, kv => kv.Value.Select(s => new CachedScenario(s.Id, s.DisplayName)).ToList());
            File.WriteAllText(ScenarioCachePath, JsonSerializer.Serialize(data));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Looks up the scenarios of switched-on mods on the Workshop. <paramref name="all"/>: every mod
    /// (start-up, the button); otherwise only mods not looked up yet (just added). Returns how many mods failed.</summary>
    public async Task<int> RefreshModScenariosAsync(bool all)
    {
        LoadScenarioCache();
        var ids = Mods.Where(m => m.IsEnabled && ModEntry.IsValidId(m.ModId) && (all || !_workshopScenarios.ContainsKey(m.ModId)))
                      .Select(m => m.ModId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0)
        {
            SyncModScenarios();
            return 0;
        }

        var failed = 0;
        using var gate = new SemaphoreSlim(4);
        var lookups = ids.Select(async id =>
        {
            await gate.WaitAsync();
            try { return (id, list: await _workshop.GetScenariosAsync(id), ok: true); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException) { return (id, list: new List<ScenarioInfo>(), ok: false); }
            finally { gate.Release(); }
        }).ToList();

        foreach (var (id, list, ok) in await Task.WhenAll(lookups))
        {
            if (ok)
                _workshopScenarios[id] = list;
            else
                failed++;
        }
        SaveScenarioCache();
        SyncModScenarios();
        return failed;
    }

    // mods added to the list: look up their scenarios in the background, one batch after another
    private void QueueScenarioLookup()
    {
        if (_loading)
            return;
        _scenarioFetch = After(_scenarioFetch ?? Task.CompletedTask);

        async Task After(Task previous)
        {
            try { await previous; } catch { /* reported by the earlier run */ }
            await Task.Yield(); // let a burst of additions (paste/import) finish first
            try { await RefreshModScenariosAsync(all: false); }
            catch (Exception ex) { AppendLog($"모드 시나리오 확인 중 오류: {ex.Message}"); }
        }
    }

    /// <summary>For tests: waits for background scenario lookups.</summary>
    public Task WaitForScenarioLookupsAsync() => _scenarioFetch ?? Task.CompletedTask;
}
