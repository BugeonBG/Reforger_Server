using System.Text.RegularExpressions;

namespace RSM.Core;

/// <summary>How much a log line matters to the person running the server.</summary>
public enum LogSeverity
{
    /// <summary>Good news: server online, players in, save done.</summary>
    Good,
    /// <summary>Plain information about what the server is doing.</summary>
    Info,
    /// <summary>Looks like an error but appears on every stock server; nothing to do.</summary>
    Harmless,
    /// <summary>Worth a look; usually not fatal.</summary>
    Caution,
    /// <summary>Something the user should fix.</summary>
    Problem,
}

/// <summary>What one log line means, in plain Korean.</summary>
public sealed record LogExplanation(LogSeverity Severity, string Title, string Meaning, string Action, string Module)
{
    public string SeverityText => Severity switch
    {
        LogSeverity.Good => "정상",
        LogSeverity.Info => "참고",
        LogSeverity.Harmless => "무시해도 됨",
        LogSeverity.Caution => "주의",
        _ => "문제",
    };

    /// <summary>For styling in XAML (good / info / harmless / caution / problem).</summary>
    public string SeverityKey => Severity.ToString().ToLowerInvariant();

    public bool HasAction => Action.Length > 0;
}

/// <summary>One group in the log check: the same kind of line, how often it appeared, and what it means.</summary>
public sealed record LogFinding(LogExplanation Explanation, int Count, string Example);

/// <summary>"로그 진단": what happened in this run, the real problems, and how much harmless noise there was.</summary>
public sealed class LogSummary
{
    public List<string> Facts { get; } = new();
    public List<LogFinding> Problems { get; } = new();
    public int HarmlessCount { get; set; }
    public int LineCount { get; set; }
    public string Headline { get; set; } = "";
    public LogSeverity Overall { get; set; } = LogSeverity.Info;
    public string OverallKey => Overall.ToString().ToLowerInvariant();
    public bool HasProblems => Problems.Count > 0;
}

/// <summary>Turns Reforger server log lines into Korean explanations. Rules are matched in order;
/// anything unknown gets a generic explanation from its module (ENGINE, BACKEND, …) and level ((E)/(W)).</summary>
public static partial class LogExplainer
{
    private sealed record Rule(Regex Pattern, LogSeverity Severity, string Title, string Meaning, string Action = "");

    private static Rule R(string pattern, LogSeverity s, string title, string meaning, string action = "") =>
        new(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), s, title, meaning, action);

    private const string StockNoise = "모드 없이 처음 설치한 서버에서도 매번 나오는 줄입니다. 게임 기본 데이터 안의 사소한 문제라서 서버 운영에는 영향이 없습니다.";

    // most specific first
    private static readonly Rule[] Rules =
    {
        //---------------------------------------------------------------- harmless stock-game noise
        R(@"is obsolete", LogSeverity.Harmless, "개발용 경고 (옛 함수 사용)",
          "게임 스크립트가 앞으로 없어질 예정인 옛 기능을 쓴다는 개발자용 알림입니다. " + StockNoise),
        R(@"Wrong GUID/name for resource", LogSeverity.Harmless, "파일 경로가 조금 어긋남",
          "게임이나 모드 데이터가 가리키는 파일의 이름표(GUID)가 맞지 않는다는 뜻입니다. 빨간 (E) 표시가 붙지만, 기본 게임에서도 서버를 켤 때마다 몇 줄씩 나오는 종류이고 서버 운영에는 영향이 없습니다. 모드 경로라면 그 모드의 일부(예: 번역 문구)가 기본값으로 보일 수 있습니다."),
        R(@"Hierarchy cannot be combined|Unknown keyword/data|Material file not found|Cannot load material|Failed to load ''",
          LogSeverity.Harmless, "게임 기본 데이터의 사소한 오류",
          "빨간 (E) 표시가 붙어 있지만 " + StockNoise),
        R(@"No module info used", LogSeverity.Harmless, "엔진 내부 메시지", StockNoise),
        R(@"GM Budget got clamped|SCR_CatalogSpawnerUserAction|Cannot get the size from terrain|RadioManagerEntity|does not have any transceiver",
          LogSeverity.Harmless, "시나리오 구성 경고", "시나리오 안의 설정값이 조금 맞지 않는다는 개발용 경고입니다. " + StockNoise),
        R(@"JsonApi Array name=""admins""", LogSeverity.Harmless, "admins 목록 처리 경고",
          "config.json의 관리자 목록(admins)을 게임 스크립트 쪽에서 따로 읽지 않는다는 뜻입니다. 공식 서버에서도 흔히 보이며, 관리자 비밀번호 로그인(#login)에는 영향이 없습니다."),
        R(@"duplicate notification info key|Can't open config file """"", LogSeverity.Harmless, "게임 데이터 중복/빈 설정 경고",
          "게임이나 모드 데이터에 같은 항목이 두 번 들어 있거나, 비어 있는 설정을 열려 했다는 개발용 경고입니다. 플레이에는 영향이 없습니다."),
        R(@"frame time \(avg|\bFPS\s*:", LogSeverity.Info, "서버 성능 기록",
          "서버가 10초마다 남기는 성능 기록입니다(-logStats). FPS가 서버의 초당 처리 횟수이고, 높을수록 좋습니다. RSM의 '서버 현황'에 이 값이 표시됩니다.",
          ""),

        //---------------------------------------------------------------- real problems
        R(@"JSON.*(is not valid|invalid)|Validation failed|Schema.*(error|fail)", LogSeverity.Problem,
          "설정 파일(config.json) 오류",
          "서버가 config.json을 읽다가 형식이나 값이 잘못된 곳을 찾았습니다. 이 상태로는 서버가 뜨지 않거나 일부 설정이 무시됩니다.",
          "이 줄 주변에 어느 항목이 문제인지 나옵니다. RSM의 '서버 설정'에서 값을 고치고 저장한 뒤 다시 시작하세요. 직접 편집했다면 '설정 파일' 화면에서 확인하세요."),
        R(@"(address|port).*(already in use|in use)|bind.*(fail|error)|failed to bind|Unable to (open|create) socket", LogSeverity.Problem,
          "포트를 쓸 수 없음",
          "게임 포트(기본 UDP 2001)나 A2S 포트(17777)를 다른 프로그램이 이미 쓰고 있습니다. 보통 서버가 하나 더 켜져 있을 때 생깁니다.",
          "작업 관리자에서 ArmaReforgerServer.exe가 이미 떠 있는지 확인해 끄거나, '서버 설정'에서 포트를 바꾸세요."),
        R(@"(addon|mod).*(download).*(fail|error|cancel)|failed to download|Download failed", LogSeverity.Problem,
          "모드 다운로드 실패",
          "서버가 워크숍에서 모드를 받지 못했습니다. 인터넷 연결, 워크숍 서버 상태, 또는 그 버전이 지워진 경우입니다.",
          "잠시 뒤 서버를 다시 시작해 보세요. 계속되면 '모드' 화면에서 그 모드의 버전 칸을 비워(최신) 저장하세요."),
        R(@"(addon|mod|dependency).*(not found|doesn't exist|does not exist|missing|unavailable|cannot be found)|Unable to (find|resolve) (addon|dependenc)", LogSeverity.Problem,
          "모드를 찾을 수 없음",
          "config.json에 적힌 모드 ID나 버전이 워크숍에 없습니다. 모드가 지워졌거나, 비공개로 바뀌었거나, 버전 번호가 틀린 경우입니다.",
          "'모드' 화면에서 '필요 모드 확인'을 누르고, 문제가 된 모드는 버전 칸을 비우거나 목록에서 빼세요."),
        R(@"Failed to register|registration failed|Unable to register|backend.*(unavailable|timeout|timed out|unreachable)", LogSeverity.Problem,
          "Bohemia 서버 목록 등록 실패",
          "서버가 Bohemia 온라인 서비스에 등록하지 못했습니다. 이 상태면 서버 목록과 다이렉트 접속 코드로 들어올 수 없습니다.",
          "인터넷 연결을 확인하세요. Bohemia 쪽 점검일 수도 있으니 잠시 뒤 서버를 다시 시작해 보세요."),
        R(@"(scenario|mission).*(not found|failed|invalid|cannot)|Unable to load (world|scenario|mission)", LogSeverity.Problem,
          "시나리오를 불러오지 못함",
          "config.json의 시나리오(scenarioId)를 찾지 못했거나 불러오지 못했습니다. 시나리오가 들어 있는 모드가 빠졌거나 ID가 틀린 경우입니다.",
          "'서버 설정'에서 시나리오를 다시 고르거나, 그 시나리오가 든 모드가 활성화돼 있는지 확인하세요."),
        R(@"out of memory|not enough memory|Allocation failed", LogSeverity.Problem,
          "메모리 부족",
          "서버가 쓸 메모리가 모자랍니다. 큰 모드를 많이 쓰거나 PC에서 다른 프로그램이 메모리를 많이 쓰는 경우입니다.",
          "다른 프로그램을 닫거나 모드 수를 줄이세요. '서버 현황'에서 메모리 사용량을 볼 수 있습니다."),
        R(@"Application crashed|Unhandled exception|FATAL|Crash ?dump|has crashed", LogSeverity.Problem,
          "서버가 비정상 종료",
          "서버 프로그램이 오류로 멈췄습니다.",
          "직전 몇 줄에 원인이 있는 경우가 많습니다. 최근에 넣은 모드를 하나씩 빼 보고, 계속되면 '로그 폴더'의 로그를 확인하세요."),
        R(@"authentication timeout|connection refused|authentication failed", LogSeverity.Caution,
          "플레이어 접속 실패",
          "누군가 접속하려다 인증 단계에서 끊겼습니다. 상대방 인터넷, 게임 버전 차이, 또는 접속하다 취소한 경우가 대부분입니다.",
          "한두 번이면 신경 쓸 필요 없습니다. 아무도 못 들어온다면 공유기 포트포워딩(UDP 2001)과 방화벽을 확인하세요."),
        R(@"\bkicked\b|BattlEye.*\b(ban|kick)", LogSeverity.Caution,
          "플레이어 강퇴",
          "플레이어가 서버에서 내보내졌습니다. 관리자 명령이나 BattlEye(안티치트)가 내보낸 경우입니다.",
          "BattlEye가 내보냈다면 이 줄에 이유가 함께 나옵니다."),

        //---------------------------------------------------------------- start-up, in order
        R(@"Initializing engine, version", LogSeverity.Info, "엔진 시작", "서버 프로그램이 켜지기 시작했습니다."),
        R(@"CLI Params:", LogSeverity.Info, "실행 옵션", "RSM이 서버를 켤 때 넘긴 옵션입니다(설정 파일 위치, 프로필 폴더, 최대 FPS 등)."),
        R(@"Addon dirs|Available addons|Loaded addons|gproj:", LogSeverity.Info, "애드온 목록",
          "서버가 불러올 수 있는/불러온 애드온 목록입니다. core와 ArmaReforger는 게임 기본 데이터이고, 그 밖의 것은 모드입니다."),
        R(@"Loading dedicated server config|Server config loaded|JSON is Valid|JSON Schema Validation", LogSeverity.Good, "설정 파일 읽기",
          "서버가 config.json을 읽었고 형식에 문제가 없습니다."),
        R(@"Module: Game; loaded", LogSeverity.Info, "게임 스크립트 준비 완료",
          "게임 규칙과 모드 코드를 다 읽어 들였습니다. 모드를 넣으면 files/classes 숫자가 늘어납니다. 여기서 멈추거나 오류가 나면 모드 코드 문제입니다."),
        R(@"Attempting online Game Config", LogSeverity.Info, "온라인 설정 확인", "Bohemia 서비스에서 받을 설정이 있는지 확인합니다. 따로 할 일은 없습니다."),
        R(@"Ping Site:", LogSeverity.Info, "가까운 Bohemia 지역", "서버 목록에서 핑을 잴 때 기준이 되는 Bohemia 데이터센터 위치입니다(한국은 보통 tokyo)."),
        R(@"Starting new playthrough", LogSeverity.Info, "새 게임 시작", "저장된 진행이 없어서 이 시나리오를 처음부터 시작합니다."),
        R(@"Enabled lag compensation", LogSeverity.Info, "지연 보정", "핑이 높은 플레이어도 맞추기 쉽게 하는 지연 보정이 켜졌습니다."),
        R(@"System language differs", LogSeverity.Harmless, "언어 설정 알림", "서버 언어가 저장된 값과 달라 영어로 맞췄다는 뜻입니다. 서버에는 영향이 없습니다."),
        R(@"Game destroyed|Game successfully destroyed", LogSeverity.Info, "서버 종료 처리", "서버가 게임을 정리하고 꺼지는 중입니다."),
        R(@"Addon Download started", LogSeverity.Info, "모드 다운로드 시작", "서버가 워크숍에서 모드를 받기 시작했습니다. 서버는 켜질 때만 모드를 받습니다."),
        R(@"\[[=>_]+\]\s*\d{1,3}%|Download speed|Downloading \d+ addons|Downloading [0-9A-F]{16} version", LogSeverity.Info, "모드 받는 중",
          "모드 다운로드 진행 상황입니다. 다 받을 때까지 서버는 온라인이 되지 않습니다. 큰 모드는 몇 GB라 시간이 걸립니다."),
        R(@"Required addons are ready", LogSeverity.Good, "모드 준비 완료", "필요한 모드를 다 받았습니다. 이제 맵을 불러옵니다."),
        R(@"Creating game instance", LogSeverity.Info, "게임 버전", "서버의 게임 버전입니다. 플레이어의 게임 버전과 같아야 접속할 수 있습니다."),
        R(@"Entities load|Subscene load|Game::LoadEntities|Terrain load|Map load time", LogSeverity.Info, "맵 불러오는 중", "맵과 맵 위의 물체를 불러오고 있습니다."),
        R(@"RPL listen address not specified", LogSeverity.Info, "기본 주소 사용",
          "bindAddress를 비워 두어 모든 네트워크 주소에서 접속을 받는다는 뜻입니다. 보통 이대로 두면 됩니다."),
        R(@"Starting RPL server, listening on address", LogSeverity.Good, "게임 포트 열림", "플레이어 접속을 받을 포트를 열었습니다."),
        R(@"BattlEye Server: (Initialized|'Initialized)|Loading BattlEye library|Config entry", LogSeverity.Info, "BattlEye(안티치트)", "안티치트 BattlEye가 켜졌습니다."),
        R(@"Connected to BE Master", LogSeverity.Good, "BattlEye 연결", "BattlEye 중앙 서버에 연결됐습니다."),
        R(@"Server registered with address", LogSeverity.Good, "서버 목록 등록",
          "Bohemia 서버 목록에 등록됐습니다. 적힌 주소가 이 서버의 공인 IP와 포트입니다. 다만 외부에서 실제로 들어올 수 있는지는 공유기 포트포워딩에 달려 있습니다."),
        R(@"Direct Join Code", LogSeverity.Good, "다이렉트 접속 코드",
          "게임의 '다이렉트 접속'에 이 코드를 넣으면 바로 들어올 수 있습니다. 서버를 켤 때마다 바뀝니다."),
        R(@"Entered online game state", LogSeverity.Good, "서버 온라인", "서버가 다 켜졌습니다. 이제 플레이어가 들어올 수 있습니다."),
        R(@"OnGameStateChanged = GAME", LogSeverity.Good, "게임 시작", "시나리오가 시작됐습니다."),

        //---------------------------------------------------------------- players
        R(@"ServerImpl event: authenticating", LogSeverity.Info, "접속 시도", "누군가 접속을 시도하고 있습니다. 괄호 안은 상대 주소와 현재 자리 수입니다."),
        R(@"Authenticated player", LogSeverity.Good, "플레이어 인증 성공", "플레이어가 인증을 통과했습니다. name= 뒤가 플레이어 이름입니다."),
        R(@"ServerImpl event: disconnected", LogSeverity.Info, "플레이어 나감", "플레이어 연결이 끊겼습니다(나가기, 튕김, 강퇴 등). reason 숫자는 끊긴 이유 코드입니다."),
        R(@"ServerImpl event: connected", LogSeverity.Info, "연결됨", "인증된 플레이어의 연결이 완료됐습니다."),
        R(@"BattlEye Server: (Adding player|Setting GUID|'Player #)", LogSeverity.Info, "BattlEye 플레이어 등록",
          "BattlEye가 플레이어를 확인했습니다. GUID는 플레이어 고유 번호로, 차단할 때 씁니다."),
        R(@"### (Connecting|Creating|Updating) player|Player connected:|Players connect(ed|ing):", LogSeverity.Info, "플레이어 입장 처리",
          "게임 안에 플레이어 캐릭터를 만드는 과정입니다. 'Players connected: 2 / 2'는 접속 인원입니다."),
        R(@"has joined faction", LogSeverity.Info, "진영 선택", "플레이어가 진영을 골랐습니다(괄호 안: US, USSR, FIA 등)."),
        R(@"Editor EDIT:.*opened the editor", LogSeverity.Info, "게임마스터 편집기", "플레이어가 게임마스터(GM) 편집기를 열었습니다."),
        R(@"Editor EDIT:.*spawned", LogSeverity.Info, "게임마스터가 물체 배치", "게임마스터가 편집기로 물체나 유닛을 맵에 놓았습니다."),
        R(@"\bvote(End|Start)\b|SCR_Voting", LogSeverity.Info, "투표", "게임 안 투표가 끝났거나 진행됐습니다."),
        R(@"MOBsSelected", LogSeverity.Info, "본부 위치 결정", "Conflict 모드에서 양 진영의 본부(MOB) 위치가 정해졌습니다."),

        //---------------------------------------------------------------- saving
        R(@"Save completed successfully", LogSeverity.Good, "자동 저장 완료", "진행 상황을 저장했습니다. 서버를 다시 켜도 이어서 할 수 있습니다."),
        R(@"\[PERSISTENCE\] Save|Save data transaction|Save data from container", LogSeverity.Info, "저장 처리", "게임 진행 상황이나 설정을 저장하거나 읽고 있습니다."),
    };

    private static readonly Dictionary<string, string> Modules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ENGINE"] = "엔진 (서버 프로그램 본체)",
        ["BACKEND"] = "Bohemia 온라인 서비스 (서버 목록, 워크숍·모드 다운로드, 플레이어 인증)",
        ["NETWORK"] = "네트워크 (플레이어 연결)",
        ["RPL"] = "네트워크 동기화 (접속 처리)",
        ["SCRIPT"] = "게임 스크립트 (게임 규칙, 모드 코드)",
        ["RESOURCES"] = "리소스 (모델·텍스처 같은 게임 파일)",
        ["WORLD"] = "월드 (맵과 물체)",
        ["ENTITY"] = "엔티티 (맵 위 물체 하나하나)",
        ["MATERIAL"] = "재질 (물체 겉모습)",
        ["PLATFORM"] = "저장 데이터",
        ["DEFAULT"] = "일반 메시지 (BattlEye, 저장 등)",
        ["PROFILING"] = "시간 측정 (각 단계에 걸린 시간)",
        ["GUI"] = "화면·언어",
        ["INIT"] = "초기화",
        ["RENDER"] = "그래픽 (서버는 화면 없이 돌아서 가짜 그래픽 장치를 씁니다)",
        ["TERRAIN"] = "지형",
        ["INPUT"] = "입력 장치",
        ["AUDIO"] = "소리",
        ["SOUND"] = "소리",
        ["PHYSICS"] = "물리",
        ["AI"] = "AI (봇)",
        ["NAVMESH"] = "AI 이동 경로",
    };

    // "[19:26:42] 19:26:42.155 SCRIPT    (W): text" → module, level, text
    [GeneratedRegex(@"^(?:\[\d\d:\d\d:\d\d\]\s+)?(?:\d\d:\d\d:\d\d\.\d{3}\s+)?(?<module>[A-Z][A-Z_]{1,})\s+(?:\((?<level>[A-Z])\))?\s*:\s?(?<text>.*)$")]
    private static partial Regex ServerLine();

    /// <summary>Null for the program's own (already Korean) lines.</summary>
    public static LogExplanation? Explain(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var trimmed = line.Trim();
        var m = ServerLine().Match(trimmed);
        if (!m.Success)
            return null; // RSM's own message

        var module = m.Groups["module"].Value;
        var level = m.Groups["level"].Value;
        var moduleText = Modules.TryGetValue(module, out var mt) ? $"{module} — {mt}" : module;

        // a player was disconnected with a cause code: explain the code
        if (DisconnectCodeRegex().Match(trimmed) is { Success: true } dc)
        {
            var cause = KickCauses.Describe(int.Parse(dc.Groups["g"].Value), int.Parse(dc.Groups["r"].Value));
            return new LogExplanation(cause.Severity, "플레이어 연결 끊김: " + cause.Title,
                cause.ForAdmin + $"\n\n플레이어 게임 화면에는 \"Kick cause code: {cause.CodeText}\"처럼 나옵니다.",
                cause.IsProblem ? "'서버 현황' 화면 아래 '접속 문제'에 이 플레이어가 나오고, 보낼 안내문을 복사할 수 있습니다." : "",
                moduleText);
        }

        foreach (var rule in Rules)
            if (rule.Pattern.IsMatch(trimmed))
                return new LogExplanation(rule.Severity, rule.Title, rule.Meaning, rule.Action, moduleText);

        // nothing specific: say what part of the server it came from and how worried to be
        return level switch
        {
            "E" or "F" => new LogExplanation(LogSeverity.Caution, "오류 표시가 붙은 줄",
                $"{(Modules.TryGetValue(module, out var a) ? a : module)} 쪽에서 오류를 남겼습니다. 리포저 서버는 게임 기본 데이터나 모드의 사소한 문제도 (E)로 표시해서, 서버가 계속 온라인이면 대부분 그냥 두어도 됩니다.",
                "서버가 꺼지거나 모드·접속에 문제가 있을 때만, 이 줄의 영어 문장을 검색해 보세요. 모드 이름이 들어 있으면 그 모드의 워크숍 페이지 댓글을 확인하세요.", moduleText),
            "W" => new LogExplanation(LogSeverity.Harmless, "경고 표시가 붙은 줄",
                $"{(Modules.TryGetValue(module, out var w) ? w : module)} 쪽의 경고입니다. 대부분 개발자용 알림이라 서버 운영에는 영향이 없습니다.", "", moduleText),
            _ => new LogExplanation(LogSeverity.Info, "진행 기록",
                $"{(Modules.TryGetValue(module, out var i) ? i : module)} 쪽에서 남긴 진행 기록입니다. 서버가 지금 무엇을 하고 있는지 보여주는 줄이라 따로 할 일은 없습니다.", "", moduleText),
        };
    }

    /// <summary>Looks over the lines of the current server run (from the last start) and says what matters.</summary>
    public static LogSummary Summarize(IReadOnlyList<string> lines)
    {
        // only the latest run: from the last time RSM started the server
        var start = 0;
        for (var i = lines.Count - 1; i >= 0; i--)
            if ((lines[i].Contains("] > ", StringComparison.Ordinal) && lines[i].Contains("ArmaReforgerServer", StringComparison.OrdinalIgnoreCase)) ||
                lines[i].Contains("Initializing engine, version", StringComparison.Ordinal))
            {
                start = i;
                break;
            }

        var s = new LogSummary();
        var problems = new Dictionary<string, (LogExplanation e, int n, string example)>();
        bool online = false, modsReady = false, configOk = false;
        string joinCode = "", version = "", address = "";
        var downloads = new List<string>();
        int joins = 0, leaves = 0, saves = 0;

        for (var i = start; i < lines.Count; i++)
        {
            var line = lines[i];
            s.LineCount++;
            if (line.Contains("Entered online game state", StringComparison.Ordinal)) online = true;
            if (line.Contains("Required addons are ready", StringComparison.Ordinal)) modsReady = true;
            if (line.Contains("JSON is Valid", StringComparison.Ordinal)) configOk = true;
            if (line.Contains("Save completed successfully", StringComparison.Ordinal)) saves++;
            if (line.Contains("Authenticated player", StringComparison.Ordinal)) joins++;
            if (line.Contains("ServerImpl event: disconnected", StringComparison.Ordinal)) leaves++;
            if (LogLine.JoinCode(line) is { Length: > 0 } code) joinCode = code;
            if (VersionRegex().Match(line) is { Success: true } v) version = v.Groups[1].Value;
            if (AddressRegex().Match(line) is { Success: true } ad) address = ad.Groups[1].Value;
            if (DownloadStartRegex().Match(line) is { Success: true } d) downloads.Add(d.Groups[1].Value.Trim());

            var e = Explain(line);
            if (e == null)
                continue;
            if (e.Severity == LogSeverity.Harmless)
                s.HarmlessCount++;
            else if (e.Severity is LogSeverity.Problem or LogSeverity.Caution)
            {
                var key = e.Title + "|" + e.Severity;
                problems[key] = problems.TryGetValue(key, out var p) ? (p.e, p.n + 1, p.example) : (e, 1, line.Trim());
            }
        }

        foreach (var (e, n, example) in problems.Values.OrderByDescending(p => p.e.Severity).ThenByDescending(p => p.n))
            s.Problems.Add(new LogFinding(e, n, example));

        if (version.Length > 0) s.Facts.Add($"게임 버전 {version}");
        if (configOk) s.Facts.Add("설정 파일을 문제없이 읽었습니다");
        if (downloads.Count > 0) s.Facts.Add($"모드 {downloads.Count}개를 받았습니다: {string.Join(", ", downloads.Take(5))}{(downloads.Count > 5 ? " …" : "")}" + (modsReady ? "" : " (아직 받는 중)"));
        else if (modsReady) s.Facts.Add("필요한 모드가 이미 다 있습니다");
        if (online) s.Facts.Add(address.Length > 0 ? $"서버 목록에 등록됨 ({address})" : "서버 목록에 등록됨");
        if (joinCode.Length > 0) s.Facts.Add($"다이렉트 접속 코드 {joinCode}");
        if (joins > 0 || leaves > 0) s.Facts.Add($"플레이어 입장 {joins}번, 나감 {leaves}번");
        if (saves > 0) s.Facts.Add($"자동 저장 {saves}번 성공");

        var real = s.Problems.Count(p => p.Explanation.Severity == LogSeverity.Problem);
        if (s.LineCount == 0)
        {
            s.Overall = LogSeverity.Info;
            s.Headline = "아직 서버 로그가 없습니다. 서버를 시작하면 여기서 진단합니다.";
        }
        else if (real > 0)
        {
            s.Overall = LogSeverity.Problem;
            s.Headline = $"확인이 필요한 문제가 {real}가지 있습니다.";
        }
        else if (online)
        {
            s.Overall = LogSeverity.Good;
            s.Headline = s.Problems.Count > 0 ? "서버는 정상입니다. 참고할 만한 줄이 몇 개 있습니다." : "서버는 정상입니다. 고칠 문제가 없습니다.";
        }
        else
        {
            s.Overall = LogSeverity.Info;
            s.Headline = downloads.Count > 0 && !modsReady ? "서버가 모드를 받는 중입니다. 다 받으면 온라인이 됩니다." : "서버가 켜지는 중입니다.";
        }
        return s;
    }

    [GeneratedRegex(@"ServerImpl event: disconnected \(identity=0x[0-9a-fA-F]+\),\s*group=(?<g>\d+),\s*reason=(?<r>\d+)")]
    private static partial Regex DisconnectCodeRegex();

    [GeneratedRegex(@"Creating game instance\(\w+\), version (\S+)")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"Server registered with address:\s*(\S+)")]
    private static partial Regex AddressRegex();

    [GeneratedRegex(@"Addon Download started [0-9A-Fa-f]{16} - (.+)$")]
    private static partial Regex DownloadStartRegex();
}
