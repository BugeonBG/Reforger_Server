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

    //---------------------------------------------------------------- update state (not saved)
    private string _latestVersion = "";
    private string _runningVersion = "";

    /// <summary>Newest version on the Workshop; empty until checked.</summary>
    public string LatestVersion { get => _latestVersion; set => Set(ref _latestVersion, value ?? ""); }

    /// <summary>Version the running server loaded; empty while the server is off.</summary>
    public string RunningVersion { get => _runningVersion; set => Set(ref _runningVersion, value ?? ""); }

    /// <summary>A newer version exists than the one pinned here, or than the one the running server has.</summary>
    public bool HasUpdate =>
        LatestVersion.Length > 0 &&
        (Version.Length > 0 ? Version != LatestVersion : RunningVersion.Length > 0 && RunningVersion != LatestVersion);

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
}
