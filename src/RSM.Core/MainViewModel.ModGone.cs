// Copyright (c) 2026 Anu. All rights reserved.
// RSM (Reforger Server Manager) — 무단 복제·수정·배포를 금합니다.

using System.Text.RegularExpressions;

namespace RSM.Core;

// Mods deleted from the Workshop (or made private). The server will not start with one switched on:
//   BACKEND (E): Addon 6A3873C87EA8E2BA - Addon was not found on workshop.
//   BACKEND (E): 1 addons are not downloadable! Cannot start until they are removed from server config.
// RSM learns about them from Workshop lookups and from that server output, and offers to switch them off.
public sealed partial class MainViewModel
{
    private readonly HashSet<string> _goneFromWorkshop = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _goneAsked = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _goneBlockedStart = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"Addon (?<id>[0-9A-Fa-f]{16}) - Addon was not found on workshop")]
    private static partial Regex GoneLineRegex();

    /// <summary>True when the Workshop said this mod does not exist (deleted or private).</summary>
    public bool IsGoneFromWorkshop(string modId) => _goneFromWorkshop.Contains(modId);

    private static string GonePrompt(IReadOnlyList<ModEntry> gone, bool starting)
    {
        var list = string.Join("\n", gone.Select(m => $"• {(m.Name.Length > 0 ? m.Name : "(이름 없음)")}  ({m.ModId})"));
        return "워크숍에서 삭제됐거나 비공개로 바뀐 모드가 켜져 있습니다:\n\n" + list +
               "\n\n이 모드가 켜져 있으면 서버가 시작되지 않습니다 (\"Addon was not found on workshop\")." +
               (starting ? "\n\n이 모드를 끄고 서버를 시작할까요?\n(아니요를 누르면 그대로 시작합니다.)" : "\n\n이 모드를 끌까요? 목록에는 남아 있어서 나중에 다시 켤 수 있습니다.");
    }

    /// <summary>A Workshop lookup came back "no such mod" for these ids.</summary>
    private void OnModsGoneFromWorkshop(IEnumerable<string> ids, bool ask)
    {
        foreach (var id in ids)
            _goneFromWorkshop.Add(id);
        var gone = Mods.Where(m => m.IsEnabled && _goneFromWorkshop.Contains(m.ModId)).ToList();
        if (gone.Count == 0)
            return;
        AppendLog("경고: 워크숍에서 삭제됐거나 비공개로 바뀐 모드가 켜져 있습니다 — " + string.Join(", ", gone.Select(Label)) +
                  ". 켜 두면 서버가 시작되지 않습니다.");

        var fresh = gone.Where(m => _goneAsked.Add(m.ModId)).ToList();
        if (!ask || fresh.Count == 0 || !_ui.Confirm(GonePrompt(fresh, starting: false)))
            return;
        foreach (var m in fresh)
            m.IsEnabled = false;
        AppendLog("워크숍에서 사라진 모드를 껐습니다: " + string.Join(", ", fresh.Select(Label)) + " — 저장을 누르면 설정 파일에 반영됩니다.");
    }

    /// <summary>Server output: remembers the mods the server could not find.</summary>
    private void NoteModGoneFromWorkshop(string line)
    {
        if (GoneLineRegex().Match(line) is not { Success: true } m)
            return;
        var id = m.Groups["id"].Value.ToUpperInvariant();
        Ui(() =>
        {
            _goneFromWorkshop.Add(id);
            _goneBlockedStart.Add(id);
        });
    }

    /// <summary>The server just quit; if a gone mod was why, offer to switch it off and start again.</summary>
    private void OfferToDropGoneModsAfterFailedStart()
    {
        if (_goneBlockedStart.Count == 0)
            return;
        var ids = _goneBlockedStart.ToList();
        _goneBlockedStart.Clear();
        var gone = Mods.Where(m => m.IsEnabled && ids.Contains(m.ModId, StringComparer.OrdinalIgnoreCase)).ToList();
        if (gone.Count == 0)
            return;

        AppendLog("서버가 시작되지 않은 이유: 워크숍에서 사라진 모드 — " + string.Join(", ", gone.Select(Label)));
        if (!_ui.Confirm(GonePrompt(gone, starting: false).Replace("이 모드를 끌까요?", "이 모드를 끄고 서버를 다시 시작할까요?")))
            return;
        foreach (var m in gone)
        {
            m.IsEnabled = false;
            _goneAsked.Add(m.ModId);
        }
        AppendLog("워크숍에서 사라진 모드를 껐습니다: " + string.Join(", ", gone.Select(Label)));
        StartServer(automatic: false);
    }
}
