namespace RSM.Core;

/// <summary>Which mods the mods page shows.</summary>
public enum ModListFilter
{
    All,
    /// <summary>On disk: the server has downloaded them.</summary>
    Downloaded,
    NotDownloaded,
    /// <summary>Being downloaded (or queued) right now.</summary>
    Downloading,
}

// Search and filter on the mods page: by name or ID, and by whether the server has the files.
// The list itself is not changed; the window applies ModMatchesFilter to its view of Mods.
public sealed partial class MainViewModel
{
    private string _modSearchText = "";
    private ModListFilter _modListFilter = ModListFilter.All;

    /// <summary>Raised when the shown subset may have changed (search, filter, downloads).</summary>
    public event Action? ModFilterChanged;

    public IReadOnlyList<Choice<ModListFilter>> ModListFilters { get; } = new[]
    {
        new Choice<ModListFilter>(ModListFilter.All, "전체"),
        new Choice<ModListFilter>(ModListFilter.Downloaded, "받은 모드"),
        new Choice<ModListFilter>(ModListFilter.NotDownloaded, "안 받은 모드"),
        new Choice<ModListFilter>(ModListFilter.Downloading, "받는 중"),
    };

    /// <summary>Name or ID (part of it), several words = all must match.</summary>
    public string ModSearchText
    {
        get => _modSearchText;
        set
        {
            if (Set(ref _modSearchText, value ?? ""))
                RaiseModFilterChanged();
        }
    }

    public ModListFilter ModListFilter
    {
        get => _modListFilter;
        set
        {
            if (Set(ref _modListFilter, value))
                RaiseModFilterChanged();
        }
    }

    public bool IsModFilterActive => ModSearchText.Trim().Length > 0 || ModListFilter != ModListFilter.All;

    /// <summary>"검색 결과 3개" while searching/filtering; empty otherwise.</summary>
    public string ModFilterResultText =>
        !IsModFilterActive ? "" : Mods.Count(ModMatchesFilter) is var n && n == 0 ? "맞는 모드 없음" : $"{n}개 표시 중";

    public bool ModMatchesFilter(ModEntry m)
    {
        switch (ModListFilter)
        {
            case ModListFilter.Downloaded when !m.IsInstalled:
            case ModListFilter.NotDownloaded when m.IsInstalled:
            case ModListFilter.Downloading when !m.HasDownloadText:
                return false;
        }

        var words = ModSearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
            return true;
        var haystack = $"{m.Name} {m.ModId} {m.Installed?.Name} {m.Installed?.Version} {m.Version}";
        return words.All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    public RelayCommand ClearModSearchCommand => _clearModSearch ??= new RelayCommand(() =>
    {
        ModSearchText = "";
        ModListFilter = ModListFilter.All;
    });
    private RelayCommand? _clearModSearch;

    private void RaiseModFilterChanged()
    {
        base.OnPropertyChanged(nameof(IsModFilterActive));
        base.OnPropertyChanged(nameof(ModFilterResultText));
        ModFilterChanged?.Invoke();
    }
}
