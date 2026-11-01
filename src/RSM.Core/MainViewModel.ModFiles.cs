namespace RSM.Core;

// Deleting mods for good: off the list AND the files the server downloaded (unchecking only
// leaves them out of config.json; the files stay on disk so switching back on is instant).
public sealed partial class MainViewModel
{
    private List<InstalledMod> _installedMods = new();

    public RelayCommand DeleteModCommand { get; }
    public RelayCommand CleanOrphanModFilesCommand { get; }
    public RelayCommand OpenAddonsDirCommand { get; }

    /// <summary>Where the server keeps downloaded mods.</summary>
    public string AddonsDir => InstalledMods.AddonsDir(ProfileDir, ExtraArgs);

    /// <summary>Downloaded mod folders that are not in the list any more (left over from earlier).</summary>
    public IReadOnlyList<InstalledMod> OrphanModFiles =>
        _installedMods.Where(i => !Mods.Any(m => string.Equals(m.ModId, i.Id, StringComparison.OrdinalIgnoreCase))).ToList();

    public string ModFilesText => _installedMods.Count == 0
        ? "서버가 받은 모드 파일이 없습니다"
        : $"받은 모드 파일 {_installedMods.Count}개 · {ServerMonitorViewModel.FormatGb(_installedMods.Sum(i => i.Bytes))}";

    public string OrphanModFilesText
    {
        get
        {
            var o = OrphanModFiles;
            return o.Count == 0 ? "목록에 없는 모드 파일이 없습니다" : $"목록에 없는 모드 파일 {o.Count}개 · {ServerMonitorViewModel.FormatGb(o.Sum(i => i.Bytes))}";
        }
    }

    /// <summary>Looks at the download folder again and shows on each row what is on disk.</summary>
    public void RefreshInstalledMods()
    {
        try
        {
            _installedMods = InstalledMods.Scan(AddonsDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _installedMods = new();
        }
        foreach (var m in Mods)
            m.Installed = _installedMods.FirstOrDefault(i => string.Equals(i.Id, m.ModId, StringComparison.OrdinalIgnoreCase));
        SyncModScenarios();
        base.OnPropertyChanged(nameof(ModFilesText));
        base.OnPropertyChanged(nameof(OrphanModFilesText));
        base.OnPropertyChanged(nameof(OrphanModFiles));
        RefreshCommands();
    }

    /// <summary>Removes the mod from the list and deletes its downloaded files, after asking.</summary>
    public void DeleteMod(ModEntry mod)
    {
        RefreshInstalledMods();
        var files = mod.Installed;
        var label = mod.Name.Length > 0 ? mod.Name : mod.ModId;

        if (files != null && IsServerRunning)
        {
            _ui.ShowError($"서버가 켜져 있는 동안에는 모드 파일을 지울 수 없습니다(서버가 쓰는 중).\n\n서버를 멈춘 뒤 다시 눌러 주세요.\n\n'{label}'만 서버에서 빼려면 체크를 풀고 저장하면 됩니다.");
            return;
        }

        // other mods in the list that need this one
        var needers = Mods.Where(m => m != mod && _knownDependencies.TryGetValue(m.ModId, out var deps) &&
                                      deps.Contains(mod.ModId, StringComparer.OrdinalIgnoreCase))
                          .Select(m => m.Name.Length > 0 ? m.Name : m.ModId).ToList();

        var text = $"'{label}' 모드를 완전히 삭제할까요?\n\n• 모드 목록에서 빠집니다\n" +
                   (files != null
                       ? $"• 서버 PC에서 받은 파일 {files.SizeText}를 지웁니다\n  {files.Folder}\n"
                       : "• 서버 PC에 받은 파일은 없습니다 (목록에서만 빠집니다)\n") +
                   (needers.Count > 0 ? $"\n주의: 이 모드가 필요한 모드가 있습니다 — {string.Join(", ", needers)}\n이 모드를 지우면 그 모드들도 서버에서 동작하지 않습니다.\n" : "") +
                   "\n다시 쓰려면 모드를 다시 추가하고 서버가 다시 받아야 합니다.";
        if (!_ui.Confirm(text))
            return;

        if (files != null)
        {
            try
            {
                InstalledMods.Delete(files, AddonsDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _ui.ShowError($"모드 파일을 지우지 못했습니다.\n{files.Folder}\n\n{ex.Message}\n\n서버나 다른 프로그램이 파일을 쓰고 있는지 확인하세요. 목록은 그대로 두었습니다.");
                RefreshInstalledMods();
                return;
            }
        }

        var wasDirty = IsDirty;
        Mods.Remove(mod);
        AppendLog(files != null ? $"모드를 완전히 삭제했습니다: {label} ({files.SizeText})" : $"모드를 목록에서 뺐습니다: {label}");
        SaveAfterModChange(wasDirty);
        RefreshInstalledMods();
    }

    /// <summary>Deletes downloaded mod folders that are no longer in the list.</summary>
    private void CleanOrphanModFiles()
    {
        RefreshInstalledMods();
        var orphans = OrphanModFiles;
        if (orphans.Count == 0)
            return;
        if (IsServerRunning)
        {
            _ui.ShowError("서버가 켜져 있는 동안에는 모드 파일을 지울 수 없습니다. 서버를 멈춘 뒤 다시 눌러 주세요.");
            return;
        }
        var total = ServerMonitorViewModel.FormatGb(orphans.Sum(o => o.Bytes));
        if (!_ui.Confirm($"목록에 없는 모드 파일 {orphans.Count}개({total})를 지울까요?\n\n• " +
                         string.Join("\n• ", orphans.Take(15).Select(o => $"{o.Name} {o.Version} — {o.SizeText}")) +
                         (orphans.Count > 15 ? $"\n… 외 {orphans.Count - 15}개" : "") +
                         "\n\n모드 목록(체크를 푼 모드 포함)에 있는 모드는 건드리지 않습니다."))
            return;

        var deleted = 0;
        foreach (var o in orphans)
        {
            try
            {
                InstalledMods.Delete(o, AddonsDir);
                deleted++;
                AppendLog($"모드 파일을 지웠습니다: {o.Name} ({o.SizeText})");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                AppendLog($"오류: 모드 파일을 지우지 못했습니다: {o.Folder} — {ex.Message}");
            }
        }
        StatusText = $"목록에 없는 모드 파일 {deleted}개를 지웠습니다 ({total})";
        RefreshInstalledMods();
    }

    //---------------------------------------------------------------- scenarios that come with mods
    private readonly HashSet<string> _modScenarioIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Puts the scenarios of switched-on, downloaded mods at the top of the scenario list
    /// ("[Zimnitrita] Conflict"), and takes away those of mods that were switched off or deleted.</summary>
    private void SyncModScenarios()
    {
        var wanted = new List<ScenarioInfo>();
        foreach (var m in Mods.Where(m => m.IsEnabled))
        {
            // from the Workshop (works before the server has downloaded the mod), plus anything the download's meta file lists
            var fromWorkshop = _workshopScenarios.TryGetValue(m.ModId, out var ws) ? ws : new List<ScenarioInfo>();
            var fromMeta = m.Installed?.Scenarios ?? Array.Empty<ScenarioInfo>();
            var label = m.Name.Length > 0 ? m.Name : m.Installed?.Name ?? m.ModId;
            foreach (var s in fromWorkshop.Concat(fromMeta))
                if (wanted.All(w => !string.Equals(w.Id, s.Id, StringComparison.OrdinalIgnoreCase)))
                    wanted.Add(new ScenarioInfo(s.Id, $"[{label}] {s.DisplayName}"));
        }

        // drop the ones no longer offered (keep the selected one so the setting is not silently lost)
        foreach (var gone in Scenarios.Where(s => _modScenarioIds.Contains(s.Id) && wanted.All(w => !string.Equals(w.Id, s.Id, StringComparison.OrdinalIgnoreCase)) && s.Id != ScenarioId).ToList())
            Scenarios.Remove(gone);
        _modScenarioIds.Clear();

        var at = 0;
        foreach (var w in wanted)
        {
            _modScenarioIds.Add(w.Id);
            var existing = Scenarios.FirstOrDefault(s => string.Equals(s.Id, w.Id, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (ScenarioCatalog.Official.Contains(existing))
                    continue;
                Scenarios.Remove(existing); // a "[설정 파일] …" / "[서버] …" stand-in: replace with the named one
            }
            Scenarios.Insert(Math.Min(at++, Scenarios.Count), w);
        }

        // keep the selection pointing at the entry in the list
        if (Scenarios.FirstOrDefault(s => s.Id == ScenarioId) is { } sel && !ReferenceEquals(sel, SelectedScenario))
            SetSelectedScenarioQuietly(sel);
    }

    // like the update bump: save right away unless other edits are waiting
    private void SaveAfterModChange(bool wasDirty)
    {
        if (wasDirty)
            AppendLog("저장하지 않은 다른 설정이 있어 자동 저장은 하지 않았습니다. 저장을 누르면 반영됩니다.");
        else
            TrySaveConfig(showSuccess: false);
    }
}
