// Copyright (c) 2026 Anu. All rights reserved.
// RSM (Reforger Server Manager) — 무단 복제·수정·배포를 금합니다.

using System.Collections.ObjectModel;

namespace RSM.Core;

// Mod categories (현대전 / 좀비전 / 2차대전 / 베트남전 …): RSM-only labels for the mods page.
//  - a mod can be in several categories;
//  - "공통" mods (ACE, admin tools, radio, UI … — guessed from the name, or set by hand) are in every category;
//  - the user ticks mods in and out of each category; every change is saved at once.
// None of this is written to the server's config.json (an unknown key there can make the server refuse
// the file): it lives in RSM's own settings, keyed by modId, so it also holds across server configs.
public sealed partial class MainViewModel
{
    public const string Uncategorised = "미분류";
    public const string AllTabKey = "";
    public const string CommonTabKey = "\u0001common";
    public const string UncategorisedTabKey = "\u0001none";

    private readonly List<string> _categoryNames = new();
    private readonly Dictionary<string, HashSet<string>> _categorySets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _commonOverride = new(StringComparer.OrdinalIgnoreCase);
    private string _selectedModCategory = AllTabKey;
    private bool _showOtherModsInCategory;

    /// <summary>The category names in the user's order.</summary>
    public IReadOnlyList<string> ModCategoryNames => _categoryNames;

    /// <summary>The tabs above the mods table: 전체, each category, 공통, 미분류 — with counts.</summary>
    public ObservableCollection<ModCategoryTab> ModCategoryTabs { get; } = new();

    /// <summary>The open tab's key (<see cref="AllTabKey"/>, a category name, <see cref="CommonTabKey"/>, <see cref="UncategorisedTabKey"/>).</summary>
    public string SelectedModCategory
    {
        get => _selectedModCategory;
        set
        {
            value ??= AllTabKey;
            if (ModCategoryTabs.All(t => t.Key != value))
                value = AllTabKey;
            if (!Set(ref _selectedModCategory, value))
                return;
            foreach (var t in ModCategoryTabs)
                t.SetSelected(t.Key == value);
            foreach (var m in Mods)
                ApplyCategoryState(m);
            base.OnPropertyChanged(nameof(IsCategoryTabSelected));
            base.OnPropertyChanged(nameof(SelectedCategoryName));
            RaiseCategoryEmptyText();
            RaiseModFilterChanged();
        }
    }

    /// <summary>A real category's tab is open (not 전체 / 공통 / 미분류): the "포함" column shows.</summary>
    public bool IsCategoryTabSelected => _categoryNames.Contains(_selectedModCategory);

    /// <summary>The open category's name, or "" (for the "포함" column header and hints).</summary>
    public string SelectedCategoryName => IsCategoryTabSelected ? _selectedModCategory : "";

    /// <summary>In a category's tab, also list the mods that are not in it, so they can be ticked in.</summary>
    public bool ShowOtherModsInCategory
    {
        get => _showOtherModsInCategory;
        set
        {
            if (Set(ref _showOtherModsInCategory, value))
            {
                RaiseCategoryEmptyText();
                RaiseModFilterChanged();
            }
        }
    }

    /// <summary>Shown over the empty table when the open tab holds no mods; empty otherwise.</summary>
    public string ModCategoryEmptyText
    {
        get
        {
            if (Mods.Count == 0 || _selectedModCategory == AllTabKey || (IsCategoryTabSelected && ShowOtherModsInCategory))
                return "";
            if (ModCategoryTabs.FirstOrDefault(t => t.Key == _selectedModCategory) is not { Count: 0 } tab)
                return "";
            return tab.Key switch
            {
                CommonTabKey => "공통 모드가 없습니다.\n모드의 '분류' 칸에서 '공통'을 체크하면 모든 분류에 들어갑니다.",
                UncategorisedTabKey => "분류 안 된 모드가 없습니다.",
                _ => $"'{tab.Name}'에 넣은 모드가 없습니다.\n'다른 모드도 보기'를 켜고 '포함'을 체크하거나, 모드의 '분류' 칸에서 고르세요.",
            };
        }
    }

    public bool HasModCategoryEmptyText => ModCategoryEmptyText.Length > 0;

    private void RaiseCategoryEmptyText()
    {
        base.OnPropertyChanged(nameof(ModCategoryEmptyText));
        base.OnPropertyChanged(nameof(HasModCategoryEmptyText));
    }

    //---------------------------------------------------------------- loading / saving

    private void LoadCategories(AppSettings app)
    {
        _categoryNames.Clear();
        foreach (var name in app.ModCategoryNames ?? new List<string>())
            if (!string.IsNullOrWhiteSpace(name) && !_categoryNames.Contains(name.Trim()) && name.Trim() != Uncategorised)
                _categoryNames.Add(name.Trim());

        _categorySets.Clear();
        if (app.ModCategorySets != null)
            foreach (var (id, cats) in app.ModCategorySets)
                foreach (var cat in cats ?? new List<string>())
                    AddToSet(id, cat);
        // 0.19 kept one category per mod
        if (app.ModCategories != null)
            foreach (var (id, cat) in app.ModCategories)
                AddToSet(id, cat);

        _commonOverride.Clear();
        if (app.ModCommon != null)
            foreach (var (id, common) in app.ModCommon)
                if (ModEntry.IsValidId(id?.ToUpperInvariant()))
                    _commonOverride[id!.ToUpperInvariant()] = common;

        RebuildCategoryTabs();
        foreach (var m in Mods)
            ApplyCategoryState(m);
    }

    private void AddToSet(string? id, string? cat)
    {
        var key = id?.Trim().ToUpperInvariant();
        if (!ModEntry.IsValidId(key) || string.IsNullOrWhiteSpace(cat))
            return;
        if (!_categorySets.TryGetValue(key!, out var set))
            _categorySets[key!] = set = new HashSet<string>();
        set.Add(cat.Trim());
    }

    private Dictionary<string, List<string>> CategorySetsForSaving() =>
        _categorySets.Where(kv => kv.Value.Count > 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(CategoryIndex).ToList(), StringComparer.OrdinalIgnoreCase);

    private int CategoryIndex(string category)
    {
        var i = _categoryNames.IndexOf(category);
        return i >= 0 ? i : int.MaxValue;
    }

    private void RebuildCategoryTabs()
    {
        var keep = _selectedModCategory;
        ModCategoryTabs.Clear();
        ModCategoryTabs.Add(new ModCategoryTab(AllTabKey, "전체", OnTabSelected));
        foreach (var name in _categoryNames)
            ModCategoryTabs.Add(new ModCategoryTab(name, name, OnTabSelected));
        ModCategoryTabs.Add(new ModCategoryTab(CommonTabKey, "공통", OnTabSelected));
        ModCategoryTabs.Add(new ModCategoryTab(UncategorisedTabKey, Uncategorised, OnTabSelected));
        _selectedModCategory = ModCategoryTabs.Any(t => t.Key == keep) ? keep : AllTabKey;
        foreach (var t in ModCategoryTabs)
            t.SetSelected(t.Key == _selectedModCategory);
        base.OnPropertyChanged(nameof(ModCategoryNames));
        RefreshCategoryCounts();
    }

    private void OnTabSelected(ModCategoryTab tab) => SelectedModCategory = tab.Key;

    //---------------------------------------------------------------- state of one mod

    /// <summary>Common = the user's choice if they made one, otherwise the guess from the name.</summary>
    private bool IsCommonMod(ModEntry m, out string hint)
    {
        if (_commonOverride.TryGetValue(m.ModId, out var chosen))
        {
            hint = chosen ? "직접 공통으로 지정함 — 모든 분류에 들어갑니다" : "직접 공통에서 뺌";
            return chosen;
        }
        var word = ModCategoryDetector.SystemKeyword(m.Name, m.Installed?.Name);
        hint = word == null ? "" : $"자동: 이름에 '{word}'이(가) 있어 기본·편의 모드로 보고 모든 분류에 넣었습니다";
        return word != null;
    }

    private IReadOnlyCollection<string> ExplicitSet(ModEntry m) =>
        _categorySets.TryGetValue(m.ModId, out var set) ? set : (IReadOnlyCollection<string>)Array.Empty<string>();

    /// <summary>Is the mod in this category (by hand, or because it is common).</summary>
    public bool IsInCategory(ModEntry m, string category) =>
        IsCommonMod(m, out _) || (_categorySets.TryGetValue(m.ModId, out var set) && set.Contains(category));

    /// <summary>Not common and in no category.</summary>
    public bool IsUncategorised(ModEntry m) => !IsCommonMod(m, out _) && ExplicitSet(m).Count == 0;

    /// <summary>Puts the saved state onto a mod (on load, when added, when its name changes). Does not save.</summary>
    private void ApplyCategoryState(ModEntry m)
    {
        var common = IsCommonMod(m, out var hint);
        var set = _categorySets.TryGetValue(m.ModId, out var s) ? s : new HashSet<string>();
        m.SetCategoryState(_categoryNames, set, common, hint, IsCategoryTabSelected && (common || set.Contains(_selectedModCategory)));
    }

    private void RefreshCategoryCounts()
    {
        foreach (var t in ModCategoryTabs)
            t.Count = t.Key switch
            {
                AllTabKey => Mods.Count,
                CommonTabKey => Mods.Count(m => IsCommonMod(m, out _)),
                UncategorisedTabKey => Mods.Count(IsUncategorised),
                _ => Mods.Count(m => IsInCategory(m, t.Key)),
            };
        RaiseCategoryEmptyText();
    }

    /// <summary>Category tab filter, used by <see cref="ModMatchesFilter"/>.</summary>
    private bool ModMatchesCategoryTab(ModEntry m) => _selectedModCategory switch
    {
        AllTabKey => true,
        CommonTabKey => IsCommonMod(m, out _),
        UncategorisedTabKey => IsUncategorised(m),
        _ => ShowOtherModsInCategory || IsInCategory(m, _selectedModCategory),
    };

    //---------------------------------------------------------------- changes from the user

    /// <summary>Put a mod in a category, or take it out. Taking a common mod out of one category makes it
    /// a normal mod that stays in all the others.</summary>
    public void SetModInCategory(ModEntry m, string category, bool on)
    {
        if (!_categoryNames.Contains(category) || !ModEntry.IsValidId(m.ModId))
            return;
        var id = m.ModId;
        if (!_categorySets.TryGetValue(id, out var set))
            _categorySets[id] = set = new HashSet<string>();

        if (IsCommonMod(m, out _))
        {
            if (on)
                return;
            _commonOverride[id] = false;
            set.Clear();
            foreach (var name in _categoryNames.Where(n => n != category))
                set.Add(name);
            AppendLog($"'{DisplayName(m)}'을(를) 공통에서 빼고 '{category}'에서만 뺐습니다. 나머지 분류에는 그대로 있습니다.");
        }
        else if (on)
            set.Add(category);
        else
            set.Remove(category);

        if (set.Count == 0)
            _categorySets.Remove(id);
        OnCategoryStateChanged(m);
    }

    /// <summary>Make a mod common (in every category) or not. The categories it was put in by hand are kept.</summary>
    public void SetModCommon(ModEntry m, bool common)
    {
        if (!ModEntry.IsValidId(m.ModId))
            return;
        _commonOverride[m.ModId] = common;
        OnCategoryStateChanged(m);
    }

    /// <summary>Put every mod back to the name guess for "common" (forgets the 공통 choices made by hand).</summary>
    public RelayCommand ResetCommonGuessCommand => _resetCommonGuess ??= new RelayCommand(() =>
    {
        if (_commonOverride.Count == 0)
        {
            AppendLog("공통 모드는 이미 자동 판별 상태입니다.");
            return;
        }
        if (!_ui.Confirm("직접 고친 '공통' 설정을 모두 지우고 모드 이름으로 다시 판별할까요?\n각 분류에 직접 넣은 모드는 그대로 둡니다."))
            return;
        _commonOverride.Clear();
        foreach (var m in Mods)
            ApplyCategoryState(m);
        AfterCategoryEdit();
        AppendLog("공통 모드를 이름으로 다시 판별했습니다.");
    });
    private RelayCommand? _resetCommonGuess;

    private void OnCategoryStateChanged(ModEntry m)
    {
        // the same mod can be listed only once, but apply to all entries with that ID just in case
        foreach (var other in Mods.Where(x => x.ModId == m.ModId))
            ApplyCategoryState(other);
        if (!Mods.Contains(m))
            ApplyCategoryState(m);
        AfterCategoryEdit();
    }

    private void AfterCategoryEdit()
    {
        RefreshCategoryCounts();
        SaveAppSettings();
        if (_selectedModCategory != AllTabKey)
            RaiseModFilterChanged();
    }

    private static string DisplayName(ModEntry m) => m.Name.Length > 0 ? m.Name : m.Installed?.Name is { Length: > 0 } n ? n : m.ModId;

    /// <summary>Category-related property changes on a mod. True when handled (never server data).</summary>
    private bool OnModCategoryPropertyChanged(ModEntry mod, string? property)
    {
        if (!ModEntry.IsCategoryProperty(property))
            return false;
        if (mod.ApplyingCategories || _loading)
            return true;
        switch (property)
        {
            case nameof(ModEntry.IsCommon):
                SetModCommon(mod, mod.IsCommon);
                break;
            case nameof(ModEntry.IsInSelectedCategory) when IsCategoryTabSelected:
                SetModInCategory(mod, _selectedModCategory, mod.IsInSelectedCategory);
                break;
        }
        return true;
    }

    /// <summary>The name, ID or downloaded copy changed: the "common" guess (or the saved state) may change with it.</summary>
    private void OnModIdentityChanged(ModEntry mod)
    {
        if (_loading)
            return;
        var before = (mod.IsCommon, mod.CategoryText);
        ApplyCategoryState(mod);
        if (before != (mod.IsCommon, mod.CategoryText))
            RefreshCategoryCounts();
    }

    private void OnModCategoryToggleRequested(ModEntry mod, string category, bool on) => SetModInCategory(mod, category, on);
}
