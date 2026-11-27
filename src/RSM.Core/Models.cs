// Copyright (c) 2026 Anu. All rights reserved.
// RSM (Reforger Server Manager) — 무단 복제·수정·배포를 금합니다.

namespace RSM.Core;

/// <summary>Everything the program lets you edit in the server's config.json.
/// Keys it does not know about are kept as they are when saving (see <see cref="ServerConfigJson"/>).</summary>
public sealed class ServerSettings
{
    // game
    public string ServerName { get; set; } = "한국 리포저 서버";
    public string Password { get; set; } = "";
    public string AdminPassword { get; set; } = "";
    public List<string> Admins { get; set; } = new();
    public string ScenarioId { get; set; } = ScenarioCatalog.DefaultScenarioId;
    public int MaxPlayers { get; set; } = 32;
    public bool Visible { get; set; } = true;
    public bool CrossPlatform { get; set; }

    // network
    public string BindAddress { get; set; } = "";
    public int BindPort { get; set; } = 2001;
    public string PublicAddress { get; set; } = "";
    public int PublicPort { get; set; } = 2001;
    public int A2sPort { get; set; } = 17777;

    // rcon
    public bool RconEnabled { get; set; }
    public int RconPort { get; set; } = 19999;
    public string RconPassword { get; set; } = "";
    public string RconPermission { get; set; } = "admin";

    // gameProperties
    public int ServerMaxViewDistance { get; set; } = 1600;
    public int NetworkViewDistance { get; set; } = 1500;
    public bool DisableThirdPerson { get; set; }
    public bool BattlEye { get; set; } = true;
    public bool FastValidation { get; set; } = true;
    public bool VonCanTransmitCrossFaction { get; set; }

    public List<ModEntry> Mods { get; set; } = new();
}

/// <summary>Workshop version numbers ("9.0.1", "1.5.41", "0.16.5237").</summary>
public static class ModVersion
{
    /// <summary>&lt;0, 0, &gt;0 like string.Compare, comparing dot-separated numbers ("9.0.1" &gt; "8.4.1", "1.0.33" &gt; "1.0.32").
    /// Parts that are not numbers are compared as text.</summary>
    public static int Compare(string? a, string? b)
    {
        var pa = (a ?? "").Trim().Split('.', '-', '_');
        var pb = (b ?? "").Trim().Split('.', '-', '_');
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length ? pa[i] : "0";
            var y = i < pb.Length ? pb[i] : "0";
            int c;
            if (long.TryParse(x, out var nx) && long.TryParse(y, out var ny))
                c = nx.CompareTo(ny);
            else
                c = string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            if (c != 0)
                return c;
        }
        return 0;
    }

    /// <summary>True when <paramref name="candidate"/> is a higher version than <paramref name="current"/>.</summary>
    public static bool IsNewer(string? candidate, string? current) =>
        !string.IsNullOrWhiteSpace(candidate) && !string.IsNullOrWhiteSpace(current) && Compare(candidate, current) > 0;
}

/// <summary>One Workshop mod. Observable so the mod grid can edit it in place.</summary>
public sealed class ModEntry : ObservableObject
{
    private string _modId = "";
    private string _name = "";
    private string _version = "";
    private bool _isEnabled = true;

    public ModEntry() { }

    public ModEntry(string modId, string name = "", string version = "", bool isEnabled = true)
    {
        _modId = modId;
        _name = name;
        _version = version;
        _isEnabled = isEnabled;
    }

    /// <summary>16 hex digit Workshop GUID, e.g. 5965550F24A0C152.</summary>
    public string ModId { get => _modId; set => Set(ref _modId, (value ?? "").Trim().ToUpperInvariant()); }

    /// <summary>Only a label; the server ignores it.</summary>
    public string Name { get => _name; set => Set(ref _name, value ?? ""); }

    /// <summary>Empty = always the latest version.</summary>
    public string Version { get => _version; set => Set(ref _version, (value ?? "").Trim()); }

    /// <summary>Off = kept in this program's list but left out of config.json, so the server ignores it.</summary>
    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }

    //---------------------------------------------------------------- categories (RSM labels, not in config.json)
    // A mod can be in several categories. "공통" (common) = in every category, including ones added later —
    // base / system mods like ACE or admin tools. The view model owns the saved state and pushes it here
    // with SetCategoryState; ticking a box here only asks it to change (CategoryToggleRequested).
    private bool _isCommon;
    private string _commonHint = "";
    private string _categoryText = "미분류";
    private bool _isInSelectedCategory;
    private IReadOnlyList<ModCategoryOption> _categoryOptions = Array.Empty<ModCategoryOption>();
    internal bool ApplyingCategories;

    /// <summary>Raised when the user ticks or unticks a category in this mod's category list (name, on).</summary>
    public event Action<ModEntry, string, bool>? CategoryToggleRequested;

    /// <summary>In every category. Set by the user, or guessed from the name (see <see cref="ModCategoryDetector"/>).</summary>
    public bool IsCommon { get => _isCommon; set => Set(ref _isCommon, value); }

    /// <summary>Why it is (or is not) common, for the tooltip: "자동: 이름에 'ACE'" / "직접 지정".</summary>
    public string CommonHint { get => _commonHint; private set => Set(ref _commonHint, value); }

    /// <summary>"공통", "현대전 · 2차대전" or "미분류" — the 분류 cell.</summary>
    public string CategoryText { get => _categoryText; private set => Set(ref _categoryText, value); }

    /// <summary>One tick box per category, in the user's order. Ticked = the mod is in it (all ticked when common).</summary>
    public IReadOnlyList<ModCategoryOption> CategoryOptions { get => _categoryOptions; private set => Set(ref _categoryOptions, value); }

    /// <summary>The "포함" box while one category's tab is open: is this mod in that category.</summary>
    public bool IsInSelectedCategory { get => _isInSelectedCategory; set => Set(ref _isInSelectedCategory, value); }

    /// <summary>True for the properties above — RSM-only labels that must never mark the server config dirty.</summary>
    public static bool IsCategoryProperty(string? name) =>
        name is nameof(IsCommon) or nameof(CommonHint) or nameof(CategoryText) or nameof(CategoryOptions) or nameof(IsInSelectedCategory);

    /// <summary>Shows the saved state. <paramref name="members"/> = the categories it was put in by hand
    /// (kept while common, so switching common off brings them back).</summary>
    internal void SetCategoryState(IReadOnlyList<string> allNames, ICollection<string> members, bool common, string commonHint, bool inSelected)
    {
        ApplyingCategories = true;
        try
        {
            IsCommon = common;
            CommonHint = commonHint;
            if (!_categoryOptions.Select(o => o.Name).SequenceEqual(allNames))
                CategoryOptions = allNames.Select(n => new ModCategoryOption(n, OnOptionToggled)).ToList();
            foreach (var o in _categoryOptions)
                o.SetChecked(common || members.Contains(o.Name));
            CategoryText = common ? "공통" :
                members.Count == 0 ? "미분류" :
                string.Join(" · ", allNames.Where(members.Contains).Concat(members.Where(m => !allNames.Contains(m))));
            IsInSelectedCategory = inSelected;
        }
        finally
        {
            ApplyingCategories = false;
        }
    }

    private void OnOptionToggled(string category, bool on) => CategoryToggleRequested?.Invoke(this, category, on);

    public static bool IsValidId(string? id) =>
        id != null && id.Length == 16 && id.All(Uri.IsHexDigit);

    public ModEntry Clone() => new(ModId, Name, Version, IsEnabled);

    //---------------------------------------------------------------- downloaded files (not saved)
    private InstalledMod? _installed;

    /// <summary>The copy the server downloaded, if any.</summary>
    public InstalledMod? Installed
    {
        get => _installed;
        set
        {
            if (Set(ref _installed, value))
            {
                base.OnPropertyChanged(nameof(IsInstalled));
                base.OnPropertyChanged(nameof(InstalledText));
            }
        }
    }

    public bool IsInstalled => _installed != null;

    /// <summary>"0.16.5237 · 191 MB", or "안 받음" before the server has downloaded it.</summary>
    public string InstalledText =>
        _installed == null ? "안 받음" : _installed.Version.Length > 0 ? $"{_installed.Version} · {_installed.SizeText}" : _installed.SizeText;

    //---------------------------------------------------------------- download while the server starts (not saved)
    private string _downloadText = "";

    /// <summary>"받기 대기 중" / "받는 중 46%" / "방금 받음" while the server fetches mods; empty otherwise.</summary>
    public string DownloadText
    {
        get => _downloadText;
        set
        {
            if (Set(ref _downloadText, value ?? ""))
                base.OnPropertyChanged(nameof(HasDownloadText));
        }
    }

    public bool HasDownloadText => _downloadText.Length > 0;

    //---------------------------------------------------------------- update state (not saved)
    private string _latestVersion = "";
    private string _runningVersion = "";

    /// <summary>Newest version on the Workshop; empty until checked.</summary>
    public string LatestVersion { get => _latestVersion; set => Set(ref _latestVersion, value ?? ""); }

    /// <summary>Version the running server loaded; empty while the server is off.</summary>
    public string RunningVersion { get => _runningVersion; set => Set(ref _runningVersion, value ?? ""); }

    /// <summary>A newer version exists than the one pinned here, or than the one the running server has.
    /// Only ever upwards: the Workshop page sometimes shows an older number than the one in use.</summary>
    public bool HasUpdate =>
        LatestVersion.Length > 0 &&
        (Version.Length > 0 ? ModVersion.IsNewer(LatestVersion, Version) : RunningVersion.Length > 0 && ModVersion.IsNewer(LatestVersion, RunningVersion));

    /// <summary>For the "최신" column.</summary>
    public string LatestText =>
        LatestVersion.Length == 0 ? "" :
        !HasUpdate ? LatestVersion :
        Version.Length > 0 ? $"{LatestVersion} ↑" : $"{LatestVersion} ↑ (서버 {RunningVersion})";

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name is nameof(Version) or nameof(LatestVersion) or nameof(RunningVersion))
        {
            base.OnPropertyChanged(nameof(HasUpdate));
            base.OnPropertyChanged(nameof(LatestText));
        }
    }
}

/// <summary>One category tick box in a mod's category list.</summary>
public sealed class ModCategoryOption : ObservableObject
{
    private readonly Action<string, bool> _toggled;
    private bool _isChecked;

    public ModCategoryOption(string name, Action<string, bool> toggled)
    {
        Name = name;
        _toggled = toggled;
    }

    public string Name { get; }

    /// <summary>Ticking asks the view model to change the saved state; it then sets the real value back.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (value == _isChecked)
                return;
            _toggled(Name, value);
            // the view model has pushed the real state by now; tell the box again in case it said no
            OnPropertyChanged(nameof(IsChecked));
        }
    }

    internal void SetChecked(bool value) => Set(ref _isChecked, value, nameof(IsChecked));
}

/// <summary>A tab above the mods table: 전체 / one category / 공통 / 미분류, with how many mods it holds.</summary>
public sealed class ModCategoryTab : ObservableObject
{
    private readonly Action<ModCategoryTab> _selected;
    private int _count;
    private bool _isSelected;

    public ModCategoryTab(string key, string name, Action<ModCategoryTab> selected)
    {
        Key = key;
        Name = name;
        _selected = selected;
    }

    /// <summary>"" = all, a category name, or one of the MainViewModel.*TabKey values.</summary>
    public string Key { get; }
    public string Name { get; }

    public int Count { get => _count; set => Set(ref _count, value); }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value) && value)
                _selected(this);
        }
    }

    internal void SetSelected(bool value) => Set(ref _isSelected, value, nameof(IsSelected));
}

/// <summary>What to do when a mod the running server uses gets a new version.</summary>
public enum ModRestartPolicy
{
    NotifyOnly,
    /// <summary>Restart once nobody is connected.</summary>
    WhenEmpty,
    Now,
}

/// <summary>A choice for a combo box.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Where things are on disk, and how to start the server. Saved per Windows user.</summary>
public sealed class AppSettings
{
    public string SteamCmdDir { get; set; } = @"C:\ReforgerServer\steamcmd";
    public string ServerDir { get; set; } = @"C:\ReforgerServer\server";
    public string ConfigPath { get; set; } = @"C:\ReforgerServer\server\Configs\server.json";
    public string ProfileDir { get; set; } = @"C:\ReforgerServer\server\profile";
    public bool UseExperimental { get; set; }
    public int MaxFps { get; set; } = 60;
    public string ExtraArgs { get; set; } = "";

    /// <summary>Check Steam for a newer server build each time the program opens, and update if there is one.</summary>
    public bool AutoUpdateOnLaunch { get; set; } = true;

    // window layout, remembered between runs (0 = use the default size)
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public double ConsoleHeight { get; set; } = 300;
    public double ConsoleFontSize { get; set; } = 13;

    // RSM's own updates (GitHub Releases)
    public bool AutoUpdateApp { get; set; } = true;
    /// <summary>"owner/repo" for builds that do not have one baked in (made with build.bat).</summary>
    public string UpdateRepository { get; set; } = "";

    // mod updates
    public bool AutoUpdateMods { get; set; } = true;
    public int ModCheckMinutes { get; set; } = 30;
    public ModRestartPolicy ModRestartPolicy { get; set; } = ModRestartPolicy.WhenEmpty;

    // mod categories (RSM-only labels; the server never sees these)
    /// <summary>The category names shown on the mods page, in order. "미분류" is always added after them.</summary>
    public List<string> ModCategoryNames { get; set; } = new() { "현대전", "좀비전", "2차대전", "베트남전" };

    /// <summary>modId (upper-case) → the categories the user put it in. Mods not listed are uncategorised
    /// (unless common).</summary>
    public Dictionary<string, List<string>> ModCategorySets { get; set; } = new();

    /// <summary>modId → "common" (in every category) as the user set it. Mods not listed are guessed from their name.</summary>
    public Dictionary<string, bool> ModCommon { get; set; } = new();

    /// <summary>Old (0.19) form: modId → one category. Read once and moved into <see cref="ModCategorySets"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? ModCategories { get; set; }
}
