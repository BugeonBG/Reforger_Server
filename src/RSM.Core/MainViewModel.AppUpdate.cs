namespace RSM.Core;

// RSM updating itself from GitHub Releases (see AppUpdater). Checked when the program opens;
// the sidebar shows when a newer version is waiting.
public sealed partial class MainViewModel
{
    private bool _closingForUpdate;

    public AsyncRelayCommand CheckAppUpdateCommand { get; }
    public AsyncRelayCommand InstallAppUpdateCommand { get; }
    public RelayCommand OpenReleasesCommand { get; }

    /// <summary>This build's version; tests set their own.</summary>
    public Version AppVersion { get; set; } = AppInfo.Version;
    public string AppVersionText => $"v{AppVersion.ToString(3)}";

    /// <summary>Repository baked in by the release workflow; tests set their own.</summary>
    public string BakedUpdateRepository { get; set; } = AppInfo.BakedUpdateRepository;

    /// <summary>Makes the updater; tests hand in one that talks to a fake GitHub.</summary>
    public Func<string, AppUpdater> AppUpdaterFactory { get; set; }

    private bool _autoUpdateApp = true;
    public bool AutoUpdateApp { get => _autoUpdateApp; set => Set(ref _autoUpdateApp, value); }

    private string _updateRepository = "";
    /// <summary>Typed in the program (only needed for builds made with build.bat).</summary>
    public string UpdateRepository
    {
        get => _updateRepository;
        set
        {
            if (Set(ref _updateRepository, (value ?? "").Trim()))
            {
                base.OnPropertyChanged(nameof(EffectiveUpdateRepository));
                base.OnPropertyChanged(nameof(HasBakedRepository));
            }
        }
    }

    public bool HasBakedRepository => BakedUpdateRepository.Length > 0;

    public string EffectiveUpdateRepository =>
        HasBakedRepository ? BakedUpdateRepository : AppUpdater.NormalizeRepository(UpdateRepository);

    private AppRelease? _appUpdate;
    public AppRelease? AppUpdate
    {
        get => _appUpdate;
        private set
        {
            if (Set(ref _appUpdate, value))
            {
                base.OnPropertyChanged(nameof(AppUpdateAvailable));
                base.OnPropertyChanged(nameof(AppUpdateButtonText));
            }
        }
    }

    public bool AppUpdateAvailable => _appUpdate != null;
    public string AppUpdateButtonText => _appUpdate == null ? "" : $"새 버전 {_appUpdate.VersionText} 설치";

    private bool _isUpdatingApp;
    public bool IsUpdatingApp { get => _isUpdatingApp; private set => Set(ref _isUpdatingApp, value); }

    private string _appUpdateStatusText = "";
    public string AppUpdateStatusText { get => _appUpdateStatusText; private set => Set(ref _appUpdateStatusText, value); }

    //---------------------------------------------------------------- start-up
    /// <summary>True when an update was installed and the program is restarting.</summary>
    private async Task<bool> OnStartupAppUpdateAsync()
    {
        if (_ui.AppExePath is { } exe)
        {
            if (File.Exists(exe + ".old"))
                AppendLog($"RSM이 {AppVersionText}(으)로 업데이트되었습니다.");
            AppUpdater.CleanupLeftovers(exe);
        }

        if (!AutoUpdateApp)
        {
            AppUpdateStatusText = $"지금 버전 {AppVersionText} · 자동 확인 꺼짐";
            return false;
        }
        try
        {
            await CheckAppUpdateAsync(automatic: true);
        }
        catch (Exception ex)
        {
            AppendLog($"RSM 업데이트 확인 중 오류: {ex.Message}");
            return false;
        }
        return _closingForUpdate;
    }

    //---------------------------------------------------------------- check
    public async Task CheckAppUpdateAsync(bool automatic)
    {
        var repo = EffectiveUpdateRepository;
        if (repo.Length == 0)
        {
            AppUpdateStatusText = $"지금 버전 {AppVersionText} · 업데이트 받을 GitHub 저장소가 정해지지 않음";
            if (!automatic)
                _ui.ShowInfo("업데이트를 받을 GitHub 저장소가 없습니다.\n\n'서버 설치' 화면의 'RSM 업데이트'에 저장소(예: 아이디/RSM)를 넣거나, GitHub 릴리스에서 받은 RSM.exe를 쓰세요. 릴리스에서 받은 RSM.exe에는 저장소가 들어 있습니다.");
            return;
        }

        AppUpdateStatusText = "새 버전 확인 중…";
        AppRelease? release;
        try
        {
            release = await AppUpdaterFactory(repo).CheckAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            AppUpdateStatusText = $"지금 버전 {AppVersionText} · 확인 실패 (인터넷 연결 확인)";
            if (!automatic)
                _ui.ShowError($"새 버전을 확인하지 못했습니다.\n\n{ex.Message}");
            return;
        }

        AppUpdate = release;
        var when = DateTime.Now.ToString("HH:mm");
        if (release == null)
        {
            AppUpdateStatusText = $"지금 버전 {AppVersionText} · 최신 ({when} 확인)";
            if (!automatic)
                AppendLog($"RSM이 최신 버전입니다 ({AppVersionText}).");
            return;
        }

        AppUpdateStatusText = $"지금 버전 {AppVersionText} · 새 버전 {release.VersionText} 있음";
        AppendLog($"RSM 새 버전 {release.VersionText}이(가) 나왔습니다 (지금 {AppVersionText}).");

        // at start-up (server not running yet) ask once; otherwise the sidebar button waits for the user
        if (automatic && !IsServerRunning && _ui.Confirm(UpdatePrompt(release)))
            await InstallAppUpdateAsync();
        else if (!automatic && _ui.Confirm(UpdatePrompt(release)))
            await InstallAppUpdateAsync();
    }

    private string UpdatePrompt(AppRelease r)
    {
        var notes = r.Notes.Length > 700 ? r.Notes[..700] + "…" : r.Notes;
        return $"RSM 새 버전 {r.VersionText}이(가) 나왔습니다. (지금 {AppVersionText})\n\n" +
               (notes.Length > 0 ? $"바뀐 점:\n{notes}\n\n" : "") +
               "지금 업데이트할까요? 새 버전을 받은 뒤 프로그램이 잠깐 꺼졌다 다시 켜집니다. 설정과 모드는 그대로입니다.";
    }

    //---------------------------------------------------------------- install
    public async Task InstallAppUpdateAsync()
    {
        if (IsUpdatingApp)
            return;
        if (AppUpdate == null)
        {
            await CheckAppUpdateAsync(automatic: false);
            return; // the check asks and installs if there is something
        }

        var release = AppUpdate;
        if (_ui.AppExePath is not { } exe || !File.Exists(exe))
        {
            _ui.ShowInfo($"이 실행 방식에서는 자동으로 바꿀 수 없습니다. 릴리스 페이지에서 RSM.exe를 받아 바꿔 주세요.\n{release.PageUrl}");
            _ui.Open(release.PageUrl);
            return;
        }

        if (IsServerRunning)
        {
            var players = CurrentPlayers() is { } n ? $" (지금 접속자 {n}명)" : "";
            if (!_ui.Confirm($"업데이트하려면 RSM을 다시 켜야 해서, 지금 실행 중인 서버가 꺼집니다{players}.\n\n계속할까요?"))
                return;
        }

        IsUpdatingApp = true;
        string downloaded;
        try
        {
            StatusText = $"RSM {release.VersionText} 받는 중…";
            var progress = new Progress<double>(p => StatusText = $"RSM {release.VersionText} 받는 중… {p:P0}");
            downloaded = await AppUpdaterFactory(EffectiveUpdateRepository).DownloadAsync(release, exe, progress);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            IsUpdatingApp = false;
            StatusText = "RSM 업데이트 실패";
            _ui.ShowError($"새 버전을 받지 못했습니다.\n\n{ex.Message}\n\n릴리스 페이지에서 직접 받을 수도 있습니다:\n{release.PageUrl}");
            return;
        }

        // unsaved settings: save, drop, or stop here
        if (IsDirty)
        {
            var answer = _ui.ConfirmYesNoCancel("저장하지 않은 설정이 있습니다. 저장하고 업데이트할까요?\n\n예: 저장 후 업데이트 / 아니요: 저장하지 않고 업데이트 / 취소: 업데이트 안 함");
            if (answer == null || (answer == true && !TrySaveConfig(showSuccess: false)))
            {
                AppUpdater.CleanupLeftovers(exe);
                IsUpdatingApp = false;
                StatusText = "RSM 업데이트를 취소했습니다";
                return;
            }
        }

        if (IsServerRunning)
        {
            AppendLog("RSM 업데이트를 위해 서버를 종료합니다...");
            StopServer();
            for (var i = 0; i < 120 && IsServerRunning; i++)
                await Task.Delay(250);
        }

        try
        {
            AppUpdater.Swap(exe, downloaded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppUpdater.CleanupLeftovers(exe);
            IsUpdatingApp = false;
            StatusText = "RSM 업데이트 실패";
            _ui.ShowError($"RSM.exe를 바꾸지 못했습니다.\n{exe}\n\n{ex.Message}\n\n" +
                          "Program Files처럼 쓰기가 막힌 폴더라면 RSM.exe를 바탕화면이나 다른 폴더로 옮겨서 쓰세요.");
            return;
        }

        AppendLog($"RSM {release.VersionText}으로 바꿨습니다. 다시 시작합니다.");
        _closingForUpdate = true;
        SaveAppSettings();
        _ui.RestartApp(exe, "--updated");
    }
}
