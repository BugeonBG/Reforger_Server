using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json.Nodes;

namespace RSM.Core;

/// <summary>Everything the main window shows and does. Has no WPF dependency, so it is tested
/// without a window (see tests/RSM.Core.Tests).</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 5000;

    private static readonly HttpClient Http = CreateHttp();

    private static readonly HashSet<string> SettingProperties = new()
    {
        nameof(ServerName), nameof(Password), nameof(AdminPassword), nameof(AdminsText), nameof(ScenarioId),
        nameof(MaxPlayers), nameof(Visible), nameof(CrossPlatform), nameof(BindPort), nameof(PublicAddress),
        nameof(PublicPort), nameof(A2sPort), nameof(RconEnabled), nameof(RconPort), nameof(RconPassword),
        nameof(RconPermission), nameof(ServerMaxViewDistance), nameof(NetworkViewDistance),
        nameof(DisableThirdPerson), nameof(BattlEye), nameof(FastValidation), nameof(VonCanTransmitCrossFaction),
    };

    private static readonly HashSet<string> PathProperties = new()
    {
        nameof(SteamCmdDir), nameof(ServerDir), nameof(ConfigPath), nameof(ProfileDir),
        nameof(UseExperimental), nameof(MaxFps), nameof(ExtraArgs), nameof(AutoUpdateOnLaunch),
        nameof(AutoUpdateMods), nameof(ModCheckMinutes), nameof(ModRestartPolicy),
        nameof(AutoUpdateApp), nameof(UpdateRepository),
    };

    private readonly IUiServices _ui;
    private readonly SynchronizationContext? _sync;
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;
    private readonly AppSettingsStore _store;
    private readonly WorkshopClient _workshop;
    private readonly object _logLock = new();
    private readonly List<RelayCommandBase> _commands = new();

    private JsonObject _configRoot = ServerConfigJson.CreateDefault();
    private string _bindAddress = "";
    private bool _loading;
    private ProcessRunner? _server;
    private DateTime? _serverStartedUtc;
    private readonly Dictionary<string, List<string>> _knownDependencies = new(StringComparer.OrdinalIgnoreCase);

    public MainViewModel(IUiServices ui, SynchronizationContext? sync, AppSettingsStore store, WorkshopClient? workshop = null,
                         SystemStatsReader? stats = null, A2sClient? a2s = null)
    {
        _ui = ui;
        _sync = sync;
        _store = store;
        _workshop = workshop ?? new WorkshopClient(Http);
        Browser = new WorkshopBrowserViewModel(
            _workshop,
            id => Mods.Any(m => string.Equals(m.ModId, id, StringComparison.OrdinalIgnoreCase)),
            AddFromWorkshop,
            url => _ui.Open(url));
        Monitor = new ServerMonitorViewModel(
            stats ?? new SystemStatsReader(),
            a2s ?? new A2sClient(),
            () => _server?.ProcessId,
            () => A2sPort,
            () => _serverStartedUtc);

        CurrentPlayers = () => Monitor.PlayerCount;
        AppUpdaterFactory = repo => new AppUpdater(Http, repo, AppVersion);

        foreach (var s in ScenarioCatalog.Official)
            Scenarios.Add(s);

        Mods.CollectionChanged += OnModsChanged;

        var app = store.Load();
        _loading = true;
        SteamCmdDir = app.SteamCmdDir;
        ServerDir = app.ServerDir;
        ConfigPath = app.ConfigPath;
        ProfileDir = app.ProfileDir;
        UseExperimental = app.UseExperimental;
        AutoUpdateOnLaunch = app.AutoUpdateOnLaunch;
        MaxFps = app.MaxFps;
        ExtraArgs = app.ExtraArgs;
        WindowWidth = app.WindowWidth;
        WindowHeight = app.WindowHeight;
        WindowMaximized = app.WindowMaximized;
        ConsoleHeight = app.ConsoleHeight;
        ConsoleFontSize = Math.Clamp(app.ConsoleFontSize, MinConsoleFont, MaxConsoleFont);
        AutoUpdateApp = app.AutoUpdateApp;
        UpdateRepository = app.UpdateRepository;
        AutoUpdateMods = app.AutoUpdateMods;
        ModCheckMinutes = app.ModCheckMinutes > 0 ? app.ModCheckMinutes : 30;
        ModRestartPolicy = app.ModRestartPolicy;
        _loading = false;
        var migrated = MigrateDefaultPaths();

        InstallOrUpdateCommand = Async(() => InstallOrUpdateAsync(automatic: false), () => !IsBusy && !IsServerRunning);
        CheckUpdateCommand = Async(() => CheckForUpdatesAsync(automatic: false), () => !IsBusy && !IsCheckingUpdate && ServerInstalled);
        ListScenariosCommand = Async(ListScenariosAsync, () => !IsBusy && !IsServerRunning);
        BrowseSteamCmdDirCommand = Cmd(() => Browse(v => SteamCmdDir = v, "SteamCMD 폴더 선택", SteamCmdDir));
        BrowseServerDirCommand = Cmd(() => Browse(v => ServerDir = v, "서버 설치 폴더 선택", ServerDir));
        BrowseProfileDirCommand = Cmd(() => Browse(v => ProfileDir = v, "프로필(로그) 폴더 선택", ProfileDir));
        OpenServerDirCommand = Cmd(() => OpenFolder(ServerDir));
        OpenProfileDirCommand = Cmd(() => OpenFolder(ProfileDir));
        NewConfigCommand = Cmd(NewConfig, () => !IsBusy);
        OpenConfigCommand = Cmd(OpenConfig, () => !IsBusy);
        SaveConfigCommand = Cmd(() => TrySaveConfig(showSuccess: true), () => !IsBusy);
        SaveConfigAsCommand = Cmd(SaveConfigAs, () => !IsBusy);
        StartServerCommand = Cmd(() => StartServer(automatic: false), () => !IsBusy && !IsServerRunning);
        RefreshLogSummaryCommand = Cmd(RefreshLogSummary);
        CheckAppUpdateCommand = Async(() => CheckAppUpdateAsync(automatic: false), () => !IsUpdatingApp);
        InstallAppUpdateCommand = Async(InstallAppUpdateAsync, () => !IsUpdatingApp);
        OpenReleasesCommand = Cmd(() => _ui.Open(EffectiveUpdateRepository.Length > 0 ? $"https://github.com/{EffectiveUpdateRepository}/releases" : "https://github.com"));
        DeleteModCommand = new RelayCommand(p => { if (p is ModEntry m) DeleteMod(m); });
        CleanOrphanModFilesCommand = Cmd(CleanOrphanModFiles, () => !IsServerRunning && OrphanModFiles.Count > 0);
        OpenAddonsDirCommand = Cmd(() => OpenFolder(AddonsDir));
        CheckModUpdatesCommand = Async(() => CheckModUpdatesAsync(automatic: false), () => !IsCheckingMods && Mods.Count > 0);
        StopServerCommand = Cmd(StopServer, () => IsServerRunning);
        AddModCommand = Cmd(AddMod);
        RemoveModCommand = Cmd(RemoveSelectedMod, () => SelectedMod != null);
        ImportModsCommand = Cmd(ImportMods);
        CopyModsJsonCommand = Cmd(CopyModsJson, () => Mods.Count > 0);
        OpenWorkshopCommand = Cmd(OpenWorkshop, () => SelectedMod != null && ModEntry.IsValidId(SelectedMod.ModId));
        CheckDependenciesCommand = Async(
            () => CheckDependenciesAsync(Mods.Where(m => m.IsEnabled).Select(m => m.ModId).Where(ModEntry.IsValidId).ToList(), manual: true),
            () => !IsCheckingDependencies && Mods.Any(m => m.IsEnabled));
        EnableAllModsCommand = Cmd(() => SetAllEnabled(true), () => Mods.Any(m => !m.IsEnabled));
        ExportModListCommand = Cmd(ExportModList, () => Mods.Count > 0);
        ImportModListCommand = Cmd(ImportModList);
        DisableAllModsCommand = Cmd(() => SetAllEnabled(false), () => Mods.Any(m => m.IsEnabled));
        AddFirewallRuleCommand = Cmd(AddFirewallRule);
        ClearLogCommand = Cmd(() => { lock (_logLock) LogLines.Clear(); });

        if (File.Exists(ConfigPath))
        {
            try
            {
                LoadConfigFrom(ConfigPath);
            }
            catch (Exception ex)
            {
                AppendLog($"config.json을 읽지 못했습니다: {ex.Message}");
                ApplyToView(new ServerSettings(), ServerConfigJson.CreateDefault());
            }
        }
        else
        {
            ApplyToView(new ServerSettings(), ServerConfigJson.CreateDefault());
        }

        if (migrated != null)
            AppendLog(migrated);

        RefreshInstalled();
        RefreshInstalledMods();
        StatusText = ServerInstalled ? "준비됨" : "서버가 아직 설치되지 않았습니다. '서버 설치' 화면에서 설치하세요.";
    }

    //============================================================== collections
    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<ScenarioInfo> Scenarios { get; } = new();
    public ObservableCollection<ModEntry> Mods { get; } = new();

    public int EnabledModCount => Mods.Count(m => m.IsEnabled);

    /// <summary>Sidebar badge: "8", or "5/8" when some are switched off.</summary>
    public string ModsBadgeText => EnabledModCount == Mods.Count ? Mods.Count.ToString() : $"{EnabledModCount}/{Mods.Count}";

    /// <summary>Line above the mod table.</summary>
    public string ModCountText => Mods.Count == 0 ? "" : $"활성 {EnabledModCount}개 / 전체 {Mods.Count}개";

    /// <summary>The "서버 현황" page.</summary>
    public ServerMonitorViewModel Monitor { get; }

    /// <summary>The "워크숍 둘러보기" page.</summary>
    public WorkshopBrowserViewModel Browser { get; }
    public IReadOnlyList<string> RconPermissions { get; } = new[] { "admin", "monitor" };

    //============================================================== paths (saved in %AppData%)
    private string _steamCmdDir = "";
    private string _serverDir = "";
    private string _configPath = "";
    private string _profileDir = "";
    private bool _useExperimental;
    private int _maxFps;
    private string _extraArgs = "";

    public string SteamCmdDir { get => _steamCmdDir; set => Set(ref _steamCmdDir, value ?? ""); }
    public string ServerDir
    {
        get => _serverDir;
        set
        {
            var old = _serverDir;
            if (Set(ref _serverDir, value ?? "") && !_loading && old.Length > 0 && _serverDir.Length > 0)
                FollowServerDir(old, _serverDir);
        }
    }
    public string ConfigPath { get => _configPath; set => Set(ref _configPath, value ?? ""); }
    public string ProfileDir { get => _profileDir; set => Set(ref _profileDir, value ?? ""); }
    public bool UseExperimental { get => _useExperimental; set => Set(ref _useExperimental, value); }

    private bool _autoUpdateOnLaunch = true;
    public bool AutoUpdateOnLaunch { get => _autoUpdateOnLaunch; set => Set(ref _autoUpdateOnLaunch, value); }

    //---------------------------------------------------------------- window layout + console view
    // set by the window when it closes; saved with the other program settings
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public double ConsoleHeight { get; set; } = 300;

    public const double MinConsoleFont = 10, MaxConsoleFont = 20;
    private double _consoleFontSize = 13;
    public double ConsoleFontSize
    {
        get => _consoleFontSize;
        set => Set(ref _consoleFontSize, Math.Clamp(Math.Round(value), MinConsoleFont, MaxConsoleFont));
    }

    private LogLevelFilter _logLevel = LogLevelFilter.All;
    /// <summary>Which console lines are shown (the window applies it to the list).</summary>
    public LogLevelFilter LogLevel { get => _logLevel; set => Set(ref _logLevel, value); }

    // radio buttons bind to these
    public bool LogShowAll { get => LogLevel == LogLevelFilter.All; set { if (value) LogLevel = LogLevelFilter.All; } }
    public bool LogShowImportant { get => LogLevel == LogLevelFilter.Important; set { if (value) LogLevel = LogLevelFilter.Important; } }
    public bool LogShowProblems { get => LogLevel == LogLevelFilter.WarningsAndErrors; set { if (value) LogLevel = LogLevelFilter.WarningsAndErrors; } }
    public bool LogShowErrors { get => LogLevel == LogLevelFilter.ErrorsOnly; set { if (value) LogLevel = LogLevelFilter.ErrorsOnly; } }

    private string _logSearch = "";
    public string LogSearch { get => _logSearch; set => Set(ref _logSearch, value ?? ""); }

    public bool ShowLogLine(string line) => LogLine.Matches(line, LogLevel, LogSearch);

    /// <summary>Lines the console currently shows, joined for the clipboard.</summary>
    public string VisibleLogText() => string.Join(Environment.NewLine, LogLines.Where(ShowLogLine));

    private string _buildStatusText = "";
    private bool _isCheckingUpdate;
    private bool _updateAvailable;

    /// <summary>"설치된 빌드 24870635 · 최신입니다" and the like, shown under the install button.</summary>
    public string BuildStatusText { get => _buildStatusText; private set => Set(ref _buildStatusText, value); }
    public bool IsCheckingUpdate { get => _isCheckingUpdate; private set => Set(ref _isCheckingUpdate, value); }
    public bool UpdateAvailable { get => _updateAvailable; private set => Set(ref _updateAvailable, value); }
    public int MaxFps { get => _maxFps; set => Set(ref _maxFps, value); }
    public string ExtraArgs { get => _extraArgs; set => Set(ref _extraArgs, value ?? ""); }

    //============================================================== server settings (config.json)
    private string _serverName = "";
    private string _password = "";
    private string _adminPassword = "";
    private string _adminsText = "";
    private string _scenarioId = "";
    private int _maxPlayers;
    private bool _visible;
    private bool _crossPlatform;
    private int _bindPort;
    private string _publicAddress = "";
    private int _publicPort;
    private int _a2sPort;
    private bool _rconEnabled;
    private int _rconPort;
    private string _rconPassword = "";
    private string _rconPermission = "admin";
    private int _serverMaxViewDistance;
    private int _networkViewDistance;
    private bool _disableThirdPerson;
    private bool _battlEye;
    private bool _fastValidation;
    private bool _vonCanTransmitCrossFaction;

    public string ServerName { get => _serverName; set => Set(ref _serverName, value ?? ""); }
    public string Password { get => _password; set => Set(ref _password, value ?? ""); }
    public string AdminPassword { get => _adminPassword; set => Set(ref _adminPassword, value ?? ""); }

    /// <summary>SteamID64 or IdentityId, one per line.</summary>
    public string AdminsText { get => _adminsText; set => Set(ref _adminsText, value ?? ""); }

    public string ScenarioId
    {
        get => _scenarioId;
        set
        {
            if (Set(ref _scenarioId, (value ?? "").Trim()))
                Set(ref _selectedScenario, Scenarios.FirstOrDefault(s => s.Id == _scenarioId), nameof(SelectedScenario));
        }
    }

    public int MaxPlayers { get => _maxPlayers; set => Set(ref _maxPlayers, value); }
    public bool Visible { get => _visible; set => Set(ref _visible, value); }
    public bool CrossPlatform { get => _crossPlatform; set => Set(ref _crossPlatform, value); }
    public int BindPort { get => _bindPort; set => Set(ref _bindPort, value); }
    public string PublicAddress { get => _publicAddress; set => Set(ref _publicAddress, (value ?? "").Trim()); }
    public int PublicPort { get => _publicPort; set => Set(ref _publicPort, value); }
    public int A2sPort { get => _a2sPort; set => Set(ref _a2sPort, value); }
    public bool RconEnabled { get => _rconEnabled; set => Set(ref _rconEnabled, value); }
    public int RconPort { get => _rconPort; set => Set(ref _rconPort, value); }
    public string RconPassword { get => _rconPassword; set => Set(ref _rconPassword, value ?? ""); }
    public string RconPermission { get => _rconPermission; set => Set(ref _rconPermission, value ?? "admin"); }
    public int ServerMaxViewDistance { get => _serverMaxViewDistance; set => Set(ref _serverMaxViewDistance, value); }
    public int NetworkViewDistance { get => _networkViewDistance; set => Set(ref _networkViewDistance, value); }
    public bool DisableThirdPerson { get => _disableThirdPerson; set => Set(ref _disableThirdPerson, value); }
    public bool BattlEye { get => _battlEye; set => Set(ref _battlEye, value); }
    public bool FastValidation { get => _fastValidation; set => Set(ref _fastValidation, value); }
    public bool VonCanTransmitCrossFaction { get => _vonCanTransmitCrossFaction; set => Set(ref _vonCanTransmitCrossFaction, value); }

    private ScenarioInfo? _selectedScenario;

    /// <summary>Points the picker at <paramref name="s"/> without touching the setting (same id).</summary>
    private void SetSelectedScenarioQuietly(ScenarioInfo s) => Set(ref _selectedScenario, s, nameof(SelectedScenario));

    public ScenarioInfo? SelectedScenario
    {
        get => _selectedScenario;
        set
        {
            if (Set(ref _selectedScenario, value) && value != null)
                ScenarioId = value.Id;
        }
    }

    //============================================================== mods tab
    private ModEntry? _selectedMod;
    private string _newModId = "";
    private string _newModName = "";
    private string _modImportText = "";

    public ModEntry? SelectedMod { get => _selectedMod; set => Set(ref _selectedMod, value); }
    public string NewModId { get => _newModId; set => Set(ref _newModId, value ?? ""); }
    public string NewModName { get => _newModName; set => Set(ref _newModName, value ?? ""); }
    public string ModImportText { get => _modImportText; set => Set(ref _modImportText, value ?? ""); }

    //============================================================== state
    private string _configPreview = "";
    private string _statusText = "";
    private bool _isBusy;
    private bool _isServerRunning;
    private bool _isDirty;
    private bool _serverInstalled;

    public string ConfigPreview { get => _configPreview; private set => Set(ref _configPreview, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    /// <summary>For the window to report small things (e.g. "copied") in the status line.</summary>
    public void SetStatus(string text) => StatusText = text;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public bool IsServerRunning { get => _isServerRunning; private set => Set(ref _isServerRunning, value); }
    public bool IsDirty { get => _isDirty; private set => Set(ref _isDirty, value); }
    public bool ServerInstalled { get => _serverInstalled; private set => Set(ref _serverInstalled, value); }

    private bool _isCheckingDependencies;

    /// <summary>True while the Workshop is being asked which mods the list needs.</summary>
    public bool IsCheckingDependencies { get => _isCheckingDependencies; private set => Set(ref _isCheckingDependencies, value); }

    /// <summary>The dependency check started by the last add/import (tests await it).</summary>
    public Task LastDependencyCheck { get; private set; } = Task.CompletedTask;

    public string InstallButtonText => !ServerInstalled ? "서버 설치" : UpdateAvailable ? "새 버전으로 업데이트" : "서버 업데이트";

    public string WindowTitle =>
        "리포저 서버 관리자" + (IsDirty ? " *" : "") + (IsServerRunning ? " — 서버 실행 중" : "");

    //============================================================== commands
    public AsyncRelayCommand InstallOrUpdateCommand { get; }
    public AsyncRelayCommand CheckUpdateCommand { get; }
    public AsyncRelayCommand ListScenariosCommand { get; }
    public RelayCommand BrowseSteamCmdDirCommand { get; }
    public RelayCommand BrowseServerDirCommand { get; }
    public RelayCommand BrowseProfileDirCommand { get; }
    public RelayCommand OpenServerDirCommand { get; }
    public RelayCommand OpenProfileDirCommand { get; }
    public RelayCommand NewConfigCommand { get; }
    public RelayCommand OpenConfigCommand { get; }
    public RelayCommand SaveConfigCommand { get; }
    public RelayCommand SaveConfigAsCommand { get; }
    public RelayCommand StartServerCommand { get; }
    public RelayCommand StopServerCommand { get; }
    public RelayCommand AddModCommand { get; }
    public RelayCommand RemoveModCommand { get; }
    public RelayCommand ImportModsCommand { get; }
    public RelayCommand CopyModsJsonCommand { get; }
    public RelayCommand OpenWorkshopCommand { get; }
    public AsyncRelayCommand CheckDependenciesCommand { get; }
    public RelayCommand EnableAllModsCommand { get; }
    public RelayCommand ExportModListCommand { get; }
    public RelayCommand ImportModListCommand { get; }
    public RelayCommand DisableAllModsCommand { get; }
    public RelayCommand AddFirewallRuleCommand { get; }
    public RelayCommand ClearLogCommand { get; }

    //============================================================== change tracking
    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);

        if (name == null)
            return;

        if (SettingProperties.Contains(name))
        {
            MarkDirty();
        }
        else if (PathProperties.Contains(name))
        {
            if (!_loading)
                SaveAppSettings();
            if (name == nameof(ServerDir))
            {
                RefreshInstalled();
                if (!_loading)
                    ShowInstalledBuild();
            }
            if (name is nameof(ProfileDir) or nameof(ExtraArgs) && !_loading)
                RefreshInstalledMods();
        }

        switch (name)
        {
            case nameof(IsDirty):
            case nameof(IsServerRunning):
                base.OnPropertyChanged(nameof(WindowTitle));
                RefreshCommands();
                break;
            case nameof(IsBusy):
            case nameof(SelectedMod):
            case nameof(IsCheckingDependencies):
            case nameof(IsCheckingUpdate):
            case nameof(IsCheckingMods):
            case nameof(IsUpdatingApp):
                RefreshCommands();
                break;
            case nameof(LogLevel):
                base.OnPropertyChanged(nameof(LogShowAll));
                base.OnPropertyChanged(nameof(LogShowImportant));
                base.OnPropertyChanged(nameof(LogShowProblems));
                base.OnPropertyChanged(nameof(LogShowErrors));
                break;
            case nameof(ServerInstalled):
            case nameof(UpdateAvailable):
                base.OnPropertyChanged(nameof(InstallButtonText));
                RefreshCommands();
                break;
        }
    }

    private void OnModsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (ModEntry m in e.OldItems) m.PropertyChanged -= OnModPropertyChanged;
        if (e.NewItems != null)
            foreach (ModEntry m in e.NewItems)
            {
                m.PropertyChanged += OnModPropertyChanged;
                ApplyKnownVersions(m);
                m.Installed = _installedMods.FirstOrDefault(i => i.Id == m.ModId);
            }
        base.OnPropertyChanged(nameof(ModsWithUpdates));
        MarkDirty();
        RefreshCommands();
        RaiseModCounts();
        Browser?.RefreshAdded();
    }

    private void RaiseModCounts()
    {
        base.OnPropertyChanged(nameof(EnabledModCount));
        base.OnPropertyChanged(nameof(ModsBadgeText));
        base.OnPropertyChanged(nameof(ModCountText));
    }

    private void SetAllEnabled(bool on)
    {
        var changed = 0;
        foreach (var m in Mods.Where(m => m.IsEnabled != on))
        {
            m.IsEnabled = on;
            changed++;
        }
        AppendLog(on ? $"모드 {changed}개를 활성화했습니다." : $"모드 {changed}개를 비활성화했습니다. 저장하면 서버가 이 모드들을 불러오지 않습니다.");
    }

    private void OnModPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModEntry.HasUpdate))
            base.OnPropertyChanged(nameof(ModsWithUpdates));
        // update state is shown, not saved
        if (e.PropertyName is nameof(ModEntry.LatestVersion) or nameof(ModEntry.RunningVersion) or nameof(ModEntry.HasUpdate) or nameof(ModEntry.LatestText)
            or nameof(ModEntry.Installed) or nameof(ModEntry.IsInstalled) or nameof(ModEntry.InstalledText))
            return;
        MarkDirty();
        if (e.PropertyName == nameof(ModEntry.IsEnabled))
        {
            SyncModScenarios();
            RaiseModCounts();
            RefreshCommands();
        }
        if (sender == SelectedMod)
            RefreshCommands();
    }

    private void MarkDirty()
    {
        if (_loading)
            return;
        IsDirty = true;
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        var copy = (JsonObject)_configRoot.DeepClone();
        ServerConfigJson.Write(ToSettings(), copy);
        ConfigPreview = ServerConfigJson.Serialize(copy);
    }

    private void RefreshCommands()
    {
        foreach (var c in _commands)
            c.Raise();
    }

    private void RefreshInstalled() =>
        ServerInstalled = ServerDir.Length > 0 && File.Exists(ServerTools.ServerExe(ServerDir));

    //============================================================== settings <-> view
    public ServerSettings ToSettings() => new()
    {
        ServerName = ServerName.Trim(),
        Password = Password,
        AdminPassword = AdminPassword,
        Admins = AdminsText
            .Split(new[] { '\r', '\n', ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct()
            .ToList(),
        ScenarioId = ScenarioId,
        MaxPlayers = MaxPlayers,
        Visible = Visible,
        CrossPlatform = CrossPlatform,
        BindAddress = _bindAddress,
        BindPort = BindPort,
        PublicAddress = PublicAddress,
        PublicPort = PublicPort,
        A2sPort = A2sPort,
        RconEnabled = RconEnabled,
        RconPort = RconPort,
        RconPassword = RconPassword,
        RconPermission = RconPermission,
        ServerMaxViewDistance = ServerMaxViewDistance,
        NetworkViewDistance = NetworkViewDistance,
        DisableThirdPerson = DisableThirdPerson,
        BattlEye = BattlEye,
        FastValidation = FastValidation,
        VonCanTransmitCrossFaction = VonCanTransmitCrossFaction,
        Mods = Mods.Select(m => m.Clone()).ToList(),
    };

    private void ApplyToView(ServerSettings s, JsonObject root)
    {
        _loading = true;
        try
        {
            _configRoot = root;
            _bindAddress = s.BindAddress;

            // a scenario from a workshop mod is not in the list yet: add it so the dropdown can show it
            if (s.ScenarioId.Length > 0 && Scenarios.All(x => x.Id != s.ScenarioId))
                Scenarios.Add(new ScenarioInfo(s.ScenarioId, "[설정 파일] " + ScenarioCatalog.NameFromId(s.ScenarioId)));

            ServerName = s.ServerName;
            Password = s.Password;
            AdminPassword = s.AdminPassword;
            AdminsText = string.Join(Environment.NewLine, s.Admins);
            ScenarioId = s.ScenarioId;
            MaxPlayers = s.MaxPlayers;
            Visible = s.Visible;
            CrossPlatform = s.CrossPlatform;
            BindPort = s.BindPort;
            PublicAddress = s.PublicAddress;
            PublicPort = s.PublicPort;
            A2sPort = s.A2sPort;
            RconEnabled = s.RconEnabled;
            RconPort = s.RconPort;
            RconPassword = s.RconPassword;
            RconPermission = s.RconPermission;
            ServerMaxViewDistance = s.ServerMaxViewDistance;
            NetworkViewDistance = s.NetworkViewDistance;
            DisableThirdPerson = s.DisableThirdPerson;
            BattlEye = s.BattlEye;
            FastValidation = s.FastValidation;
            VonCanTransmitCrossFaction = s.VonCanTransmitCrossFaction;

            // one by one rather than Clear(), so each removed entry is unhooked
            while (Mods.Count > 0)
                Mods.RemoveAt(Mods.Count - 1);
            foreach (var m in s.Mods)
                Mods.Add(m.Clone());
        }
        finally
        {
            _loading = false;
        }

        IsDirty = false;
        RefreshPreview();
    }

    //============================================================== config file
    public void LoadConfigFrom(string path)
    {
        var root = ServerConfigJson.Load(path);
        var settings = ServerConfigJson.Read(root);

        // switched-off mods are kept beside config.json, not in it
        var off = DisabledModsFile.Load(path)
            .Where(d => settings.Mods.All(m => !string.Equals(m.ModId, d.ModId, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        settings.Mods.AddRange(off);

        ApplyToView(settings, root);
        AppendLog(off.Count == 0 ? $"설정을 불러왔습니다: {path}" : $"설정을 불러왔습니다: {path} (비활성 모드 {off.Count}개 포함)");
    }

    /// <summary>Validates and writes config.json. Returns false (after telling the user why) on failure.</summary>
    public bool TrySaveConfig(bool showSuccess)
    {
        var settings = ToSettings();
        var errors = SettingsValidator.Validate(settings);
        if (errors.Count > 0)
        {
            _ui.ShowError("설정에 문제가 있어 저장하지 못했습니다.\n\n• " + string.Join("\n• ", errors));
            return false;
        }

        if (string.IsNullOrWhiteSpace(ConfigPath))
        {
            _ui.ShowError("config.json을 저장할 경로를 정하세요. ('설정 파일' 화면)");
            return false;
        }

        try
        {
            ServerConfigJson.Write(settings, _configRoot);
            ServerConfigJson.Save(ConfigPath, _configRoot);
            DisabledModsFile.Save(ConfigPath, settings.Mods);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ui.ShowError($"config.json을 저장하지 못했습니다.\n{ConfigPath}\n\n{ex.Message}");
            return false;
        }

        IsDirty = false;
        RefreshPreview();
        AppendLog($"설정을 저장했습니다: {ConfigPath}");
        if (showSuccess)
            StatusText = "설정 저장됨";
        return true;
    }

    private bool ConfirmDiscard() =>
        !IsDirty || _ui.Confirm("저장하지 않은 변경 사항이 있습니다. 버리고 계속할까요?");

    private void NewConfig()
    {
        if (!ConfirmDiscard())
            return;
        ApplyToView(new ServerSettings(), ServerConfigJson.CreateDefault());
        IsDirty = true;
        AppendLog("새 설정을 만들었습니다. 저장하면 config.json이 만들어집니다.");
    }

    private void OpenConfig()
    {
        if (!ConfirmDiscard())
            return;
        var path = _ui.PickOpenFile("config.json 열기", "JSON 파일 (*.json)|*.json|모든 파일 (*.*)|*.*", ConfigPath);
        if (path == null)
            return;
        try
        {
            LoadConfigFrom(path);
            ConfigPath = path;
        }
        catch (Exception ex)
        {
            _ui.ShowError($"이 파일을 읽지 못했습니다.\n{path}\n\n{ex.Message}");
        }
    }

    private void SaveConfigAs()
    {
        var path = _ui.PickSaveFile("config.json 저장 위치", "JSON 파일 (*.json)|*.json", ConfigPath);
        if (path == null)
            return;
        var old = ConfigPath;
        ConfigPath = path;
        if (!TrySaveConfig(showSuccess: true))
            ConfigPath = old;
    }

    //============================================================== paths that follow the server folder
    /// <summary>config.json and the profile (logs + downloaded mods) live inside the server folder by
    /// default. When the server folder moves, take them along so mods don't pile up on the old drive.</summary>
    private void FollowServerDir(string oldDir, string newDir)
    {
        var changed = new List<string>();
        if (Rebase(ConfigPath, oldDir, newDir) is { } cfg)
        {
            ConfigPath = cfg;
            changed.Add("설정 파일 → " + cfg);
            MarkDirty(); // the settings on screen get written to the new place on the next save
        }
        if (Rebase(ProfileDir, oldDir, newDir) is { } prof)
        {
            ProfileDir = prof;
            changed.Add("로그·모드 폴더 → " + prof);
        }
        if (changed.Count > 0)
            AppendLog("서버 폴더를 따라 위치를 옮겼습니다: " + string.Join(", ", changed));
    }

    /// <summary>"C:\a\b\x.json" moved from "C:\a" to "E:\z" → "E:\z\b\x.json"; null when not under oldDir.</summary>
    public static string? Rebase(string path, string oldDir, string newDir)
    {
        static string Norm(string p) =>
            p.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
        var p = Norm(path);
        var o = Norm(oldDir);
        if (p.Length == 0 || o.Length == 0)
            return null;
        if (string.Equals(p, o, StringComparison.OrdinalIgnoreCase))
            return Norm(newDir);
        if (!p.StartsWith(o + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;
        return Norm(newDir) + p[o.Length..];
    }

    /// <summary>Earlier versions left config.json and the profile at the default C:\ReforgerServer\server
    /// even after the server folder was changed. Bring them next to the real server folder (copying the
    /// saved config), once. Returns a log line when something moved.</summary>
    private string? MigrateDefaultPaths()
    {
        var defaults = new AppSettings();
        if (string.Equals(Rebase(ServerDir, defaults.ServerDir, defaults.ServerDir), Rebase(defaults.ServerDir, defaults.ServerDir, defaults.ServerDir), StringComparison.OrdinalIgnoreCase))
            return null; // still using the default server folder

        var moved = new List<string>();
        try
        {
            if (Rebase(ConfigPath, defaults.ServerDir, ServerDir) is { } cfg && Rebase(ConfigPath, ServerDir, ServerDir) == null)
            {
                if (File.Exists(ConfigPath) && !File.Exists(cfg))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
                    File.Copy(ConfigPath, cfg);
                    var side = DisabledModsFile.PathFor(ConfigPath);
                    if (File.Exists(side))
                        File.Copy(side, DisabledModsFile.PathFor(cfg), overwrite: true);
                }
                ConfigPath = cfg;
                moved.Add("설정 파일 → " + cfg);
            }
            if (Rebase(ProfileDir, defaults.ServerDir, ServerDir) is { } prof && Rebase(ProfileDir, ServerDir, ServerDir) == null)
            {
                ProfileDir = prof;
                moved.Add("로그·모드 폴더 → " + prof);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"설정 파일을 서버 폴더로 옮기지 못했습니다: {ex.Message}";
        }
        return moved.Count == 0 ? null : "서버 폴더에 맞춰 위치를 옮겼습니다: " + string.Join(", ", moved);
    }

    //============================================================== install / update
    private async Task InstallOrUpdateAsync(bool automatic)
    {
        if (string.IsNullOrWhiteSpace(SteamCmdDir) || string.IsNullOrWhiteSpace(ServerDir))
        {
            _ui.ShowError("SteamCMD 폴더와 서버 설치 폴더를 먼저 정하세요.");
            return;
        }

        var warnings = ServerTools.InstallPathWarnings(SteamCmdDir, ServerDir);
        if (automatic)
            warnings.ForEach(w => AppendLog("참고: " + w));
        else if (warnings.Count > 0 &&
            !_ui.Confirm("설치 위치에 문제가 있을 수 있습니다.\n\n• " + string.Join("\n• ", warnings) +
                         "\n\n권장: SteamCMD는 C:\\ReforgerServer\\steamcmd, 서버는 C:\\ReforgerServer\\server 처럼 " +
                         "OneDrive 밖의 영문 경로에 따로 두세요.\n\n그래도 이 위치에 설치할까요?"))
        {
            AppendLog("설치를 취소했습니다. 설치 위치를 바꾼 뒤 다시 누르세요.");
            return;
        }

        IsBusy = true;
        try
        {
            var steamCmd = ServerTools.SteamCmdExe(SteamCmdDir);
            if (!File.Exists(steamCmd))
            {
                StatusText = "SteamCMD 내려받는 중...";
                AppendLog($"SteamCMD가 없어 내려받습니다: {SteamCmdDir}");
                await ServerTools.DownloadSteamCmdAsync(Http, SteamCmdDir);
                AppendLog("SteamCMD를 내려받았습니다. 처음 실행할 때 스스로 업데이트하느라 시간이 조금 걸립니다.");
            }

            Directory.CreateDirectory(ServerDir);
            var branch = UseExperimental ? "실험(Experimental)" : "정식";
            StatusText = $"서버 {branch} 버전 설치/업데이트 중... (수 GB라 오래 걸릴 수 있습니다)";

            var args = ServerTools.BuildSteamCmdUpdateArgs(ServerDir, UseExperimental);
            var output = new List<string>();
            int code;
            bool success;

            // show how much has arrived: SteamCMD prints little while it downloads
            using var progressCts = new CancellationTokenSource();
            var progress = ReportDownloadProgressAsync(ServerDir, branch, progressCts.Token);
            try
            {
                for (var attempt = 1; ; attempt++)
                {
                    output.Clear();
                    code = await RunToolAsync(steamCmd, args, SteamCmdDir, output);
                    success = output.Any(l => l.Contains("Success!", StringComparison.OrdinalIgnoreCase) && l.Contains("fully installed", StringComparison.OrdinalIgnoreCase));
                    if (success || attempt >= 2 || !ServerTools.SteamCmdOnlyUpdatedItself(code, output))
                        break;
                    AppendLog("SteamCMD가 자기 자신만 업데이트하고 끝났습니다. 이어서 서버 설치를 다시 시작합니다.");
                }
            }
            finally
            {
                progressCts.Cancel();
                await progress;
            }

            RefreshInstalled();
            if (!ServerInstalled)
                OfferInstalledServerElsewhere();

            if (success && ServerInstalled)
            {
                AppendLog("서버 설치/업데이트가 끝났습니다.");
                StatusText = "서버 설치/업데이트 완료";
                UpdateAvailable = false;
                ShowInstalledBuild();
            }
            else if (success)
            {
                AppendLog("SteamCMD는 설치를 마쳤다고 했지만, 서버 폴더에서 ArmaReforgerServer.exe를 찾지 못했습니다. 서버 폴더 경로를 확인하세요.");
                StatusText = "서버 실행 파일을 찾지 못함 — 서버 폴더 확인";
            }
            else
            {
                AppendLog($"SteamCMD가 설치 완료 메시지 없이 끝났습니다 (종료 코드 {code}). 위 로그를 확인하고 한 번 더 실행해 보세요.");
                StatusText = "설치/업데이트 실패 — 로그 확인";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    //============================================================== startup: find the server, check for updates
    /// <summary>Called once the window is up: points at the server if SteamCMD put it somewhere else,
    /// then (if switched on) checks Steam for a newer build and updates.</summary>
    public async Task OnStartupAsync()
    {
        // RSM itself first: a new version replaces this one and restarts, so the rest would be wasted
        if (await OnStartupAppUpdateAsync())
            return;

        if (!ServerInstalled)
            OfferInstalledServerElsewhere();

        ShowInstalledBuild();
        if (ServerInstalled && AutoUpdateOnLaunch)
        {
            try
            {
                await CheckForUpdatesAsync(automatic: true);
            }
            catch (Exception ex)
            {
                AppendLog($"업데이트 확인 중 오류: {ex.Message}");
            }
        }

        // then the mods: bump pinned versions to the newest, and keep checking while the program is open
        if (AutoUpdateMods && Mods.Count > 0)
        {
            try
            {
                await CheckModUpdatesAsync(automatic: true);
            }
            catch (Exception ex)
            {
                AppendLog($"모드 업데이트 확인 중 오류: {ex.Message}");
            }
        }
        StartModUpdateLoop();
    }

    /// <summary>SteamCMD sometimes ignores force_install_dir and installs into its own library.
    /// If the server is there, offer to use that folder (no need to download 10 GB again).</summary>
    private void OfferInstalledServerElsewhere()
    {
        var found = ServerTools.FindInstalledServer(ServerDir, SteamCmdDir);
        if (found == null || string.Equals(Path.GetFullPath(found), Path.GetFullPath(ServerDir), StringComparison.OrdinalIgnoreCase))
            return;

        if (!_ui.Confirm($"서버가 지정한 서버 폴더가 아닌 다른 곳에 설치되어 있습니다.\n\n찾은 위치: {found}\n지정한 서버 폴더: {ServerDir}\n\n" +
                         "서버 폴더를 찾은 위치로 바꿀까요? (다시 내려받을 필요 없이 바로 쓸 수 있습니다)"))
        {
            AppendLog($"다른 위치에 설치된 서버를 찾았지만 서버 폴더는 그대로 두었습니다: {found}");
            return;
        }

        ServerDir = found;
        if (MigrateDefaultPaths() is { } moved)
            AppendLog(moved);
        AppendLog($"서버 폴더를 설치된 위치로 바꿨습니다: {found}");
        StatusText = "서버를 찾았습니다";
    }

    private void ShowInstalledBuild()
    {
        if (!ServerInstalled)
        {
            BuildStatusText = "";
            return;
        }
        var installed = ServerTools.ReadInstalledBuildId(ServerDir, SteamCmdDir, ServerTools.AppId(UseExperimental));
        BuildStatusText = installed is { } b ? $"설치된 빌드 {b}" : "설치된 빌드를 알 수 없음";
    }

    /// <summary>Asks Steam (through SteamCMD) for the newest build and compares it with the installed one.
    /// automatic: update straight away (used at start-up); otherwise ask first.</summary>
    public async Task CheckForUpdatesAsync(bool automatic)
    {
        var steamCmd = ServerTools.SteamCmdExe(SteamCmdDir);
        if (!ServerInstalled || !File.Exists(steamCmd) || IsBusy)
            return;

        var appId = ServerTools.AppId(UseExperimental);
        var installed = ServerTools.ReadInstalledBuildId(ServerDir, SteamCmdDir, appId);

        IsCheckingUpdate = true;
        StatusText = "새 서버 버전이 있는지 확인하는 중...";
        AppendLog("스팀에서 최신 서버 버전을 확인합니다...");
        long? latest = null;
        try
        {
            for (var attempt = 1; attempt <= 2 && latest == null; attempt++)
            {
                var output = new List<string>();
                var code = await RunToolAsync(steamCmd, ServerTools.BuildAppInfoArgs(appId), SteamCmdDir, output, echo: false);
                latest = ServerTools.ParseLatestBuildId(output);
                if (latest == null && !ServerTools.SteamCmdOnlyUpdatedItself(code, output))
                    break;
            }
        }
        finally
        {
            IsCheckingUpdate = false;
        }

        if (latest == null)
        {
            AppendLog("최신 버전을 확인하지 못했습니다 (인터넷 연결이나 스팀 상태를 확인하세요).");
            StatusText = "업데이트 확인 실패";
            ShowInstalledBuild();
            return;
        }

        if (installed is { } have && have == latest)
        {
            UpdateAvailable = false;
            BuildStatusText = $"설치된 빌드 {have} · 최신입니다";
            AppendLog($"서버가 최신 버전입니다 (빌드 {have}).");
            StatusText = "서버 최신 버전";
            return;
        }

        UpdateAvailable = true;
        var from = installed?.ToString() ?? "알 수 없음";
        BuildStatusText = $"설치된 빌드 {from} · 새 빌드 {latest} 있음";
        AppendLog($"새 서버 버전이 나왔습니다: 빌드 {from} → {latest}");

        if (IsServerRunning)
        {
            StatusText = "새 서버 버전 있음 — 서버를 멈춘 뒤 업데이트하세요";
            return;
        }

        if (automatic || _ui.Confirm($"새 서버 버전이 나왔습니다.\n\n설치된 빌드: {from}\n최신 빌드: {latest}\n\n지금 업데이트할까요? (바뀐 부분만 받습니다)"))
        {
            if (automatic)
                AppendLog("자동 업데이트를 시작합니다. (설치 화면에서 끌 수 있습니다)");
            await InstallOrUpdateAsync(automatic: true);
        }
    }

    private async Task ReportDownloadProgressAsync(string dir, string branch, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(3000, ct);
                var bytes = await Task.Run(() => ServerTools.DirectorySize(dir), ct);
                if (!ct.IsCancellationRequested && bytes > 0)
                    StatusText = $"서버 {branch} 버전 받는 중… 지금까지 {ServerMonitorViewModel.FormatGb(bytes)}";
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Runs a helper program to the end, copying its output to the log (and to <paramref name="capture"/>).</summary>
    private async Task<int> RunToolAsync(string exe, string args, string workingDir, List<string>? capture = null, bool echo = true)
    {
        using var runner = new ProcessRunner();
        runner.Output += line =>
        {
            if (capture != null)
                lock (capture) capture.Add(line);
            if (echo) AppendLog(line);
        };
        AppendLog($"> {Path.GetFileName(exe)} {args}");
        runner.Start(exe, args, workingDir);
        return await runner.WaitForExitAsync();
    }

    //============================================================== scenarios
    private async Task ListScenariosAsync()
    {
        var exe = ServerTools.ServerExe(ServerDir);
        if (!File.Exists(exe))
        {
            _ui.ShowError("서버가 설치되어 있지 않습니다. '서버 설치' 화면에서 먼저 설치하세요.");
            return;
        }
        if (IsServerRunning)
        {
            _ui.ShowError("서버가 켜져 있는 동안에는 시나리오를 찾을 수 없습니다. 서버를 멈춘 뒤 다시 눌러 주세요.");
            return;
        }
        // with the config the server loads the mods, so their scenarios are listed too
        if (!TrySaveConfig(showSuccess: false))
            return;

        IsBusy = true;
        StatusText = "모드를 불러와 시나리오 목록을 만드는 중... (모드가 많으면 몇 분 걸립니다)";
        var lines = new List<string>();
        var lastOutput = DateTime.UtcNow;

        try
        {
            using var runner = new ProcessRunner();
            runner.Output += line =>
            {
                lock (lines)
                {
                    lines.Add(line);
                    lastOutput = DateTime.UtcNow;
                }
            };

            Directory.CreateDirectory(ProfileDir);
            var args = "-config " + ServerTools.Quote(Path.GetFullPath(ConfigPath)) +
                       " -profile " + ServerTools.Quote(Path.GetFullPath(ProfileDir)) + " -listScenarios";
            AppendLog($"> {Path.GetFileName(exe)} {args}");
            runner.Start(exe, args, ServerDir);

            // The server prints the list and may then keep running; stop it once the list has stopped growing.
            var started = DateTime.UtcNow;
            while (runner.IsRunning)
            {
                await Task.Delay(500);
                int found;
                DateTime last;
                lock (lines)
                {
                    found = ScenarioCatalog.ParseListOutput(lines).Count;
                    last = lastOutput;
                }
                bool online;
                lock (lines)
                    online = lines.Any(l => l.Contains("Entered online game state", StringComparison.Ordinal));
                // done once the server has gone on to start the game, or the list stopped growing for a while
                if (online || (found > 0 && DateTime.UtcNow - last > TimeSpan.FromSeconds(20)) || DateTime.UtcNow - started > TimeSpan.FromMinutes(6))
                    break;
            }
            runner.Kill();
            await runner.WaitForExitAsync();
        }
        finally
        {
            IsBusy = false;
        }

        List<string> ids;
        lock (lines)
            ids = ScenarioCatalog.ParseListOutput(lines);

        var added = 0;
        foreach (var id in ids.Where(id => Scenarios.All(s => s.Id != id)))
        {
            Scenarios.Add(new ScenarioInfo(id, "[서버] " + ScenarioCatalog.NameFromId(id)));
            added++;
        }

        AppendLog($"시나리오 {ids.Count}개를 찾았고, 그중 {added}개를 목록에 새로 추가했습니다.");
        StatusText = ids.Count > 0 ? $"시나리오 {ids.Count}개 찾음" : "시나리오를 찾지 못했습니다 — 로그 폴더의 console.log를 확인하세요.";
    }

    //============================================================== run
    /// <summary>automatic: started by the program (mod update restart) — log problems instead of asking.</summary>
    private bool StartServer(bool automatic)
    {
        // what we already know (from earlier dependency checks) about switched-on mods needing something absent
        var problems = MissingKnownDependencies();
        if (problems.Count > 0 && automatic)
            AppendLog("경고: 활성화된 모드에 필요한 모드가 빠져 있습니다 — " + string.Join(", ", problems.Take(5)));
        else if (problems.Count > 0 &&
            !_ui.Confirm("활성화된 모드에 필요한 모드가 빠져 있거나 비활성화되어 있습니다.\n\n• " + string.Join("\n• ", problems.Take(12)) +
                         (problems.Count > 12 ? $"\n… 외 {problems.Count - 12}개" : "") +
                         "\n\n이대로 시작하면 서버가 뜨지 못할 수 있습니다. '모드' 화면의 '필요 모드 확인'을 누르면 한 번에 정리됩니다.\n\n그래도 서버를 시작할까요?"))
            return false;

        if (!TrySaveConfig(showSuccess: false))
            return false;

        var exe = ServerTools.ServerExe(ServerDir);
        if (!File.Exists(exe))
        {
            _ui.ShowError($"서버 실행 파일이 없습니다.\n{exe}\n\n'서버 설치' 화면에서 서버를 설치하세요.");
            return false;
        }

        try
        {
            Directory.CreateDirectory(ProfileDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ui.ShowError($"프로필 폴더를 만들지 못했습니다.\n{ProfileDir}\n\n{ex.Message}");
            return false;
        }

        var args = ServerTools.BuildServerArgs(ConfigPath, ProfileDir, MaxFps, ExtraArgs);
        var runner = new ProcessRunner();
        runner.Output += line =>
        {
            AppendLog(line);
            var issuesBefore = Monitor.Connections.Version;
            Monitor.OnServerLog(line);
            if (Monitor.Connections.Version != issuesBefore && Monitor.Connections.Issues() is [var issue, ..])
                AppendLog($"접속 문제: {issue.Who} — {issue.Cause.Title}. '서버 현황' 화면에서 이 플레이어에게 보낼 안내문을 복사할 수 있습니다.");
            OnServerOutput(line);
        };
        runner.Exited += code => Ui(() =>
        {
            if (_server == runner)
            {
                _server = null;
                IsServerRunning = false;
                StatusText = "서버 정지됨";
                ForgetRunningModVersions();
                RefreshInstalledMods();
                JoinCode = "";
            }
            AppendLog($"서버가 종료되었습니다. (종료 코드 {code})");
            runner.Dispose();
        });

        AppendLog($"> {exe} {args}");
        Monitor.Tracker.Clear();
        Monitor.Connections.ServerStarted();
        try
        {
            runner.Start(exe, args, ServerDir);
        }
        catch (Exception ex)
        {
            runner.Dispose();
            _ui.ShowError($"서버를 시작하지 못했습니다.\n\n{ex.Message}");
            return false;
        }

        _server = runner;
        _serverStartedUtc = DateTime.UtcNow;
        IsServerRunning = true;
        StatusText = $"서버 실행 중 — UDP {BindPort} (A2S {A2sPort})";
        _runningVersionsTask = RecordRunningModVersionsAsync();
        return true;
    }

    /// <summary>"A가 필요로 하는 B" lines for switched-on mods whose known dependencies are not switched on.</summary>
    public List<string> MissingKnownDependencies()
    {
        var byId = Mods.GroupBy(m => m.ModId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var mod in Mods.Where(m => m.IsEnabled))
        {
            if (!_knownDependencies.TryGetValue(mod.ModId, out var deps))
                continue;
            foreach (var dep in deps)
            {
                if (byId.TryGetValue(dep, out var d) && d.IsEnabled)
                    continue;
                var depName = d != null && d.Name.Length > 0 ? d.Name : dep;
                var state = d == null ? "목록에 없음" : "비활성";
                result.Add($"{(mod.Name.Length > 0 ? mod.Name : mod.ModId)}에 필요한 {depName} ({state})");
            }
        }
        return result.Distinct().ToList();
    }

    private void StopServer()
    {
        if (_server == null)
            return;
        AppendLog("서버를 종료합니다...");
        _server.Kill();
    }

    //============================================================== mods
    private void AddMod()
    {
        var parsed = ModListParser.Parse(NewModId);
        if (parsed.Count == 0)
        {
            _ui.ShowError("모드 ID를 입력하세요. 16자리 16진수(예: 5965550F24A0C152)나 워크숍 링크를 넣으면 됩니다.");
            return;
        }
        if (parsed.Count == 1 && NewModName.Trim().Length > 0)
            parsed[0].Name = NewModName.Trim();

        var before = Mods.Select(m => m.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (added, updated) = ModListParser.Merge(Mods, parsed);
        AppendLog($"모드 {added}개 추가, {updated}개 갱신");
        NewModId = "";
        NewModName = "";
        StartDependencyCheck(Mods.Select(m => m.ModId).Where(id => !before.Contains(id)).ToList());
    }

    private void RemoveSelectedMod()
    {
        if (SelectedMod != null)
            Mods.Remove(SelectedMod);
    }

    private void ImportMods()
    {
        var text = ModImportText.Trim().Length > 0 ? ModImportText : _ui.GetClipboardText() ?? "";
        var parsed = ModListParser.Parse(text);
        if (parsed.Count == 0)
        {
            _ui.ShowError("가져올 모드를 찾지 못했습니다.\n게임의 모드 매니저 → JSON 탭 내용이나, 모드 ID / 워크숍 링크 목록을 붙여넣으세요.");
            return;
        }
        var before = Mods.Select(m => m.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (added, updated) = ModListParser.Merge(Mods, parsed);
        AppendLog($"모드 가져오기: {parsed.Count}개 중 {added}개 추가, {updated}개 갱신");
        StatusText = $"모드 {added}개 추가됨";
        ModImportText = "";
        StartDependencyCheck(Mods.Select(m => m.ModId).Where(id => !before.Contains(id)).ToList());
    }

    /// <summary>"추가" on a workshop browser card.</summary>
    private void AddFromWorkshop(WorkshopListItem item)
    {
        if (Mods.Any(m => string.Equals(m.ModId, item.Id, StringComparison.OrdinalIgnoreCase)))
            return;
        Mods.Add(new ModEntry(item.Id, item.Name));
        AppendLog($"워크숍에서 모드를 추가했습니다: {item.Name} ({item.Id})");
        StatusText = $"'{item.Name}' 추가됨";
        StartDependencyCheck(new List<string> { item.Id });
    }

    /// <param name="offerToActivate">false when restoring a saved list: inactive mods were left
    /// inactive on purpose, so only mods missing from the list are offered.</param>
    private void StartDependencyCheck(List<string> ids, bool offerToActivate = true)
    {
        ids = ids.Where(ModEntry.IsValidId).ToList();
        if (ids.Count == 0)
            return;
        LastDependencyCheck = RunDependencyCheck(ids, offerToActivate);
    }

    private async Task RunDependencyCheck(List<string> ids, bool offerToActivate)
    {
        try
        {
            await CheckDependenciesAsync(ids, manual: false, offerToActivate);
        }
        catch (Exception ex)
        {
            AppendLog($"필요 모드를 확인하지 못했습니다: {ex.Message}");
        }
    }

    /// <summary>Asks the Workshop what <paramref name="ids"/> need, fills in missing names, and offers to
    /// add whatever required mods are not in the list yet.</summary>
    private async Task CheckDependenciesAsync(List<string> ids, bool manual, bool offerToActivate = true)
    {
        if (ids.Count == 0)
            return;

        IsCheckingDependencies = true;
        StatusText = "워크숍에서 필요 모드를 확인하는 중...";
        DependencyReport report;
        try
        {
            report = await _workshop.ResolveAsync(ids);
        }
        finally
        {
            IsCheckingDependencies = false;
        }

        // names straight from the Workshop for anything still unnamed
        foreach (var m in Mods)
        {
            if (m.Name.Length == 0 && report.Mods.TryGetValue(m.ModId, out var info) && info.Name.Length > 0)
                m.Name = info.Name;
        }

        if (report.Failed.Count > 0)
            AppendLog($"워크숍에서 정보를 가져오지 못한 모드: {string.Join(", ", report.Failed)} — 인터넷 연결이나 모드 ID를 확인하세요.");

        // remember what each mod needs, so starting the server can warn without asking the Workshop again
        foreach (var (id, info) in report.Mods)
            _knownDependencies[id] = info.Dependencies.Select(d => d.Id).ToList();

        static string NameOf(RequiredMod r) => r.Name.Length > 0 ? r.Name : r.Id;
        var byId = Mods.GroupBy(m => m.ModId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var missing = report.Required.Where(r => !byId.ContainsKey(r.Id)).ToList();
        var switchedOff = offerToActivate
            ? report.Required.Where(r => byId.TryGetValue(r.Id, out var m) && !m.IsEnabled).ToList()
            : new List<RequiredMod>();

        if (missing.Count == 0 && switchedOff.Count == 0)
        {
            var msg = report.Required.Count == 0
                ? "필요 모드가 따로 없습니다."
                : $"필요 모드 {report.Required.Count}개가 모두 목록에 있습니다.";
            AppendLog(msg);
            StatusText = msg;
            if (manual)
                _ui.ShowInfo(report.Failed.Count == 0 ? msg : msg + $"\n\n단, {report.Failed.Count}개 모드는 워크숍에서 확인하지 못했습니다. 콘솔을 보세요.");
            return;
        }

        const int shown = 12;
        var parts = new List<string>();
        void Section(string title, List<RequiredMod> list)
        {
            if (list.Count == 0)
                return;
            var lines = list.Take(shown).Select(r => $"• {NameOf(r)}   ({r.RequiredBy}에 필요)").ToList();
            if (list.Count > shown)
                lines.Add($"… 외 {list.Count - shown}개");
            parts.Add(title + "\n" + string.Join("\n", lines));
        }
        Section($"목록에 없는 필요 모드가 {missing.Count}개 있습니다.", missing);
        Section($"비활성화된 필요 모드가 {switchedOff.Count}개 있습니다.", switchedOff);

        var action = (missing.Count > 0, switchedOff.Count > 0) switch
        {
            (true, true) => "없는 모드는 추가하고 비활성 모드는 활성화할까요?",
            (true, false) => "같이 추가할까요?",
            _ => "같이 활성화할까요?",
        };
        var question = string.Join("\n\n", parts) + "\n\n" + action +
                       " 빠지면 서버가 시작하지 못하거나 모드가 제대로 동작하지 않습니다. (버전은 늘 최신으로 받습니다)";

        if (_ui.Confirm(question))
        {
            foreach (var r in missing)
                Mods.Add(new ModEntry(r.Id, r.Name));
            foreach (var r in switchedOff)
                byId[r.Id].IsEnabled = true;
            if (missing.Count > 0)
                AppendLog($"필요 모드 {missing.Count}개를 추가했습니다: {string.Join(", ", missing.Select(NameOf))}");
            if (switchedOff.Count > 0)
                AppendLog($"비활성이던 필요 모드 {switchedOff.Count}개를 활성화했습니다: {string.Join(", ", switchedOff.Select(NameOf))}");
            StatusText = "필요 모드를 정리했습니다";
        }
        else
        {
            var all = missing.Concat(switchedOff).ToList();
            AppendLog($"필요 모드 {all.Count}개를 추가하지 않았습니다(또는 활성화하지 않았습니다): {string.Join(", ", all.Select(NameOf))}");
            StatusText = $"필요 모드 {all.Count}개가 빠져 있습니다";
        }
    }

    /// <summary>Saves every mod (name, version, id, active or not) to a .json or .txt file the user picks.</summary>
    private void ExportModList()
    {
        var folder = SafeDirectoryOf(ConfigPath);
        var initial = Path.Combine(folder ?? "", ModListFile.DefaultFileName(DateTime.Now));
        var path = _ui.PickSaveFile("모드 목록 저장", ModListFile.DialogFilterSave, initial);
        if (path == null)
            return;
        try
        {
            ModListFile.Save(path, Mods, DateTime.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ui.ShowError($"모드 목록을 저장하지 못했습니다.\n{path}\n\n{ex.Message}");
            return;
        }
        AppendLog($"모드 목록 {Mods.Count}개를 저장했습니다: {path}");
        StatusText = $"모드 목록 {Mods.Count}개 저장됨";
    }

    /// <summary>Restores a list saved by <see cref="ExportModList"/> (or any file that names mods).</summary>
    private void ImportModList()
    {
        var path = _ui.PickOpenFile("모드 목록 불러오기", ModListFile.DialogFilterOpen, SafeDirectoryOf(ConfigPath) is { } d ? Path.Combine(d, "x.json") : null);
        if (path == null)
            return;

        List<ModEntry> loaded;
        try
        {
            loaded = ModListFile.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ui.ShowError($"파일을 읽지 못했습니다.\n{path}\n\n{ex.Message}");
            return;
        }
        if (loaded.Count == 0)
        {
            _ui.ShowError("이 파일에서 모드를 찾지 못했습니다.\n'목록 저장'으로 만든 .json/.txt 파일이나 모드 ID가 적힌 파일을 고르세요.");
            return;
        }

        var replace = true;
        if (Mods.Count > 0)
        {
            var answer = _ui.ConfirmYesNoCancel(
                $"파일에서 모드 {loaded.Count}개를 찾았습니다. 지금 목록에는 모드 {Mods.Count}개가 있습니다.\n\n" +
                "예: 지금 목록을 이 파일의 목록으로 바꾸기\n아니요: 지금 목록에 더하기\n취소: 그만두기");
            if (answer == null)
                return;
            replace = answer == true;
        }

        var before = Mods.Select(m => m.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (replace)
        {
            while (Mods.Count > 0)
                Mods.RemoveAt(Mods.Count - 1);
            foreach (var m in loaded)
                Mods.Add(m.Clone());
            AppendLog($"모드 목록을 불러왔습니다: {loaded.Count}개 (활성 {loaded.Count(m => m.IsEnabled)}개) — {path}");
        }
        else
        {
            var (added, updated) = ModListParser.Merge(Mods, loaded);
            AppendLog($"모드 목록을 더했습니다: {added}개 추가, {updated}개 갱신 — {path}");
        }

        StatusText = $"모드 {Mods.Count}개 불러옴 — 저장 후 서버를 시작하면 내려받습니다";
        _ui.ShowInfo($"모드 {Mods.Count}개를 불러왔습니다 (활성 {EnabledModCount}개).\n\n" +
                     "저장(Ctrl+S)한 뒤 서버를 시작하면, 활성화된 모드를 서버가 워크숍에서 한 번에 내려받습니다.");

        // anything newly in the list: make sure what it needs is there too
        StartDependencyCheck(Mods.Where(m => m.IsEnabled && !before.Contains(m.ModId)).Select(m => m.ModId).ToList(), offerToActivate: false);
    }

    private static string? SafeDirectoryOf(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            return dir != null && Directory.Exists(dir) ? dir : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void CopyModsJson()
    {
        _ui.SetClipboardText(ServerConfigJson.Serialize(new JsonObject { ["mods"] = ModListParser.ToJsonArray(Mods.Where(m => m.IsEnabled)) }));
        StatusText = "활성 모드 목록을 JSON으로 클립보드에 복사했습니다.";
    }

    private void OpenWorkshop()
    {
        if (SelectedMod != null)
            _ui.Open($"https://reforger.armaplatform.com/workshop/{SelectedMod.ModId}");
    }

    //============================================================== misc
    private void AddFirewallRule()
    {
        var ports = ServerTools.PortsToOpen(ToSettings()).Distinct().ToList();
        if (_ui.RunElevated("cmd.exe", ServerTools.BuildFirewallCommand(ports)))
            AppendLog($"Windows 방화벽에 UDP {string.Join(", ", ports)} 허용 규칙을 추가했습니다. 공유기 포트포워딩은 따로 해야 합니다.");
        else
            AppendLog("방화벽 규칙을 추가하지 못했습니다. (관리자 권한 요청을 취소했거나 netsh가 실패했습니다)");
    }

    private void Browse(Action<string> set, string title, string current)
    {
        var picked = _ui.PickFolder(title, Directory.Exists(current) ? current : null);
        if (picked != null)
            set(picked);
    }

    private void OpenFolder(string dir)
    {
        if (Directory.Exists(dir))
            _ui.Open(dir);
        else
            _ui.ShowInfo($"폴더가 아직 없습니다.\n{dir}");
    }

    private void SaveAppSettings()
    {
        try
        {
            _store.Save(new AppSettings
            {
                SteamCmdDir = SteamCmdDir,
                ServerDir = ServerDir,
                ConfigPath = ConfigPath,
                ProfileDir = ProfileDir,
                UseExperimental = UseExperimental,
                AutoUpdateOnLaunch = AutoUpdateOnLaunch,
                MaxFps = MaxFps,
                ExtraArgs = ExtraArgs,
                WindowWidth = WindowWidth,
                WindowHeight = WindowHeight,
                WindowMaximized = WindowMaximized,
                ConsoleHeight = ConsoleHeight,
                ConsoleFontSize = ConsoleFontSize,
                AutoUpdateApp = AutoUpdateApp,
                UpdateRepository = UpdateRepository,
                AutoUpdateMods = AutoUpdateMods,
                ModCheckMinutes = ModCheckMinutes,
                ModRestartPolicy = ModRestartPolicy,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not worth interrupting the user for; paths just won't be remembered
        }
    }

    public void AppendLog(string line) => Ui(() =>
    {
        lock (_logLock)
        {
            LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
            while (LogLines.Count > MaxLogLines)
                LogLines.RemoveAt(0);
        }
    });

    /// <summary>Called when the window is closing. False = keep the window open.</summary>
    public bool ConfirmClose()
    {
        if (_closingForUpdate)
            return true; // already asked and saved before the update
        if (IsServerRunning && !_ui.Confirm("서버가 실행 중입니다.\n프로그램을 닫으면 서버도 함께 종료됩니다. 닫을까요?"))
            return false;

        if (IsDirty)
        {
            var answer = _ui.ConfirmYesNoCancel("저장하지 않은 설정이 있습니다. 저장할까요?");
            if (answer == null)
                return false;
            if (answer == true && !TrySaveConfig(showSuccess: false))
                return false;
        }
        return true;
    }

    public void Shutdown()
    {
        _backgroundCts.Cancel();
        Monitor.Stop();
        _server?.Dispose();
        _server = null;
        SaveAppSettings();
    }

    //============================================================== helpers
    private void Ui(Action action)
    {
        if (_sync == null || Environment.CurrentManagedThreadId == _uiThreadId)
            action();
        else
            _sync.Post(_ => action(), null);
    }

    private RelayCommand Cmd(Action execute, Func<bool>? canExecute = null)
    {
        var c = new RelayCommand(execute, canExecute);
        _commands.Add(new RelayCommandBase(c.RaiseCanExecuteChanged));
        return c;
    }

    private AsyncRelayCommand Async(Func<Task> execute, Func<bool> canExecute)
    {
        var c = new AsyncRelayCommand(execute, canExecute, ex =>
        {
            IsBusy = false;
            AppendLog($"오류: {ex.Message}");
            StatusText = "오류 — 로그 확인";
            _ui.ShowError(ex.Message);
        });
        _commands.Add(new RelayCommandBase(c.RaiseCanExecuteChanged));
        return c;
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) RSM-ReforgerServerManager/0.2");
        return http;
    }

    private sealed class RelayCommandBase
    {
        private readonly Action _raise;
        public RelayCommandBase(Action raise) => _raise = raise;
        public void Raise() => _raise();
    }
}
