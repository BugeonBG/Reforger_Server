namespace RSM.Core;

public enum LogKind
{
    Normal,
    /// <summary>Known harmless engine chatter (also in a clean vanilla server): dimmed, not counted as a problem.</summary>
    Noise,
    /// <summary>Milestones worth spotting: server online, join code, players joining, saves, install done.</summary>
    Highlight,
    Warning,
    Error,
}

/// <summary>Which console lines to show.</summary>
public enum LogLevelFilter
{
    All,
    WarningsAndErrors,
    ErrorsOnly,
    /// <summary>Only the milestones and problems — hides the engine's loading chatter.</summary>
    Important,
}

/// <summary>Sorts console lines (the program's own and the server's) so the console can colour and filter them.</summary>
public static partial class LogLine
{
    // the server tags its lines "ENGINE    (E):", "SCRIPT    (W):"
    private static readonly string[] ErrorMarks = { "(E):", "(F):", " 오류", "오류:", "실패", "FATAL", "Exception", "crash" };
    private static readonly string[] WarningMarks = { "(W):", "경고", "참고:", "주의", "접속 문제:" };

    // the stock game prints these on every start, mods or not; they are not the user's problem
    private static readonly string[] NoiseMarks =
    {
        "is obsolete", "Hierarchy cannot be combined", "Unknown keyword/data", "Failed to load ''",
        "Wrong GUID/name for resource", "Material file not found", "Cannot load material",
        "No module info used", "There is not enough of SCR_CatalogSpawnerUserAction", "GM Budget got clamped",
        "Cannot get the size from terrain", "doesn't contain RadioManagerEntity", "does not have any transceiver",
        "JsonApi Array name=\"admins\"",
        "InputManager: unknown key",
        // -logStats every 10 s
        "frame time (avg",
    };

    private static readonly string[] HighlightMarks =
    {
        // mod downloads at server start
        "Addon Download started", "Downloading ", "Download speed", "Required addons are ready",
        // server
        "Entered online game state",
        "Server registered with address",
        "Direct Join Code",
        "Starting RPL server",
        "Connecting player", "Updating player", "Player connected", "Player disconnected", "Disconnecting identity",
        "Save completed successfully",
        "BattlEye Server: Initialized",
        // program
        "서버 온라인", "서버를 시작", "서버가 종료", "서버를 종료", "설치/업데이트가 끝났습니다", "최신 버전", "새 서버 버전", "저장했습니다",
    };

    public static LogKind Classify(string line)
    {
        if (string.IsNullOrEmpty(line))
            return LogKind.Normal;
        if (Contains(line, NoiseMarks))
            return LogKind.Noise;
        if (DownloadProgressRegex().IsMatch(line))
            return LogKind.Highlight;
        if (Contains(line, ErrorMarks))
            return LogKind.Error;
        if (Contains(line, WarningMarks))
            return LogKind.Warning;
        if (Contains(line, HighlightMarks))
            return LogKind.Highlight;
        return LogKind.Normal;
    }

    public static bool Matches(string line, LogLevelFilter level, string? search)
    {
        if (!string.IsNullOrWhiteSpace(search) && line.IndexOf(search.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        var kind = Classify(line);
        return level switch
        {
            LogLevelFilter.ErrorsOnly => kind == LogKind.Error,
            LogLevelFilter.WarningsAndErrors => kind is LogKind.Error or LogKind.Warning,
            LogLevelFilter.Important => kind is LogKind.Error or LogKind.Warning or LogKind.Highlight || (kind == LogKind.Normal && !LooksLikeServerLine(line)),
            _ => true,
        };
    }

    /// <summary>Server output starts with a module name ("SCRIPT    (W): …", "BACKEND      : …"), and in
    /// console.log also with the engine's clock ("13:19:21.333 ENGINE …"). Lines without that are the
    /// program's own messages, which are always worth showing.</summary>
    public static bool LooksLikeServerLine(string line) => ServerLineRegex().IsMatch(line);

    [System.Text.RegularExpressions.GeneratedRegex(@"^(\[\d\d:\d\d:\d\d\]\s+)?(\d\d:\d\d:\d\d\.\d{3}\s+)?[A-Z][A-Z_]{2,}\s+(\([A-Z]\))?\s*:")]
    private static partial System.Text.RegularExpressions.Regex ServerLineRegex();

    // "BACKEND      : RHS - Status Quo: [==>_____] 7% 15/191 MB"
    [System.Text.RegularExpressions.GeneratedRegex(@"BACKEND\s*:\s*(?<name>.+?):\s*\[[=>_]+\]\s*(?<pct>\d{1,3})%\s*(?<done>[\d.]+)/(?<total>[\d.]+)\s*(?<unit>[KMG]B)")]
    private static partial System.Text.RegularExpressions.Regex DownloadProgressRegex();

    /// <summary>Mod download progress the server prints while it starts, e.g. "RHS - Status Quo 85% (163/191 MB)".</summary>
    public static string? DownloadProgress(string line)
    {
        var m = DownloadProgressRegex().Match(line);
        return m.Success ? $"{m.Groups["name"].Value.Trim()} {m.Groups["pct"].Value}% ({m.Groups["done"].Value}/{m.Groups["total"].Value} {m.Groups["unit"].Value})" : null;
    }

    /// <summary>"Direct Join Code: 0268219882" → "0268219882".</summary>
    public static string? JoinCode(string line)
    {
        var i = line.IndexOf("Direct Join Code:", StringComparison.OrdinalIgnoreCase);
        return i < 0 ? null : line[(i + "Direct Join Code:".Length)..].Trim();
    }

    private static bool Contains(string line, string[] marks)
    {
        foreach (var m in marks)
            if (line.Contains(m, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
