namespace RSM.Core;

/// <summary>Why a player was disconnected, from the numbers the server logs:
/// "ServerImpl event: disconnected (identity=0x00000001), group=1, reason=4".
/// The player's game shows the same numbers with names: "Kick cause code: group=1 'REPLICATION', reason=4 'TIMEOUT'".
/// Names come from the codes players and Bohemia's tracker have reported; unknown ones are shown as numbers.</summary>
public sealed record KickCause(
    int Group,
    int Reason,
    string Code,
    LogSeverity Severity,
    string Title,
    string ForAdmin,
    string ForPlayer)
{
    /// <summary>"group=1 'REPLICATION', reason=4 'TIMEOUT'" — what the player sees on their screen.</summary>
    public string CodeText => $"group={Group}{GroupNameText}, reason={Reason}{(Code.Length > 0 ? $" '{Code}'" : "")}";

    public string SeverityKey => Severity.ToString().ToLowerInvariant();

    /// <summary>A code from our list (not just numbers we have never seen).</summary>
    public bool IsKnown => Code.Length > 0 || KickCauses.GroupName(Group) != null;

    private string GroupNameText => KickCauses.GroupName(Group) is { } g ? $" '{g}'" : "";

    /// <summary>A failure worth telling the player about (not a normal leave or server shutdown).</summary>
    public bool IsProblem => Severity is LogSeverity.Problem or LogSeverity.Caution;
}

public static class KickCauses
{
    private const string ModFix =
        "1) 게임을 끕니다.\n" +
        "2) 문서\\My Games\\ArmaReforger\\addons 폴더에서 이 서버에서 쓰는 모드 폴더를 지웁니다. (잘 모르겠으면 addons 폴더 안을 전부 지워도 됩니다. 모드는 다시 받으면 됩니다.)\n" +
        "3) Steam에서 Arma Reforger 우클릭 → 속성 → 설치된 파일 → '게임 파일 무결성 검사'를 누릅니다.\n" +
        "4) 다시 접속하면 서버의 모드를 새로 받습니다.";

    private static readonly Dictionary<int, string> Groups = new()
    {
        [1] = "REPLICATION",
        [3] = "BATTLEYE",
        [4] = "DATA",
        [6] = "PLAYER_MANAGER",
    };

    public static string? GroupName(int group) => Groups.TryGetValue(group, out var g) ? g : null;

    public static KickCause Describe(int group, int reason) => (group, reason) switch
    {
        (1, 3) => new(1, 3, "CONNECTION_FAILURE", LogSeverity.Caution, "연결 실패",
            "플레이어와 서버 사이의 연결이 중간에 끊겼습니다. 대부분 플레이어 쪽 인터넷(와이파이, VPN, 공유기) 문제입니다. 여러 사람이 동시에 겪는다면 서버 PC의 인터넷이나 포트포워딩을 확인하세요.",
            "서버와 연결이 중간에 끊겨서 접속이 안 됐어요 (연결 실패).\n" +
            "• 와이파이라면 가능하면 유선으로 바꿔 주세요.\n• VPN이나 핑 줄여주는 프로그램을 쓰고 있다면 끄고 해 주세요.\n• 게임을 완전히 껐다 켠 뒤 다시 접속해 주세요."),
        (1, 4) => new(1, 4, "TIMEOUT", LogSeverity.Caution, "응답 시간 초과",
            "플레이어 쪽에서 일정 시간 응답이 없어 서버가 끊었습니다. 인터넷이 불안정하거나, 모드를 받거나 맵을 불러오는 데 너무 오래 걸린 경우입니다.",
            "서버가 기다리는 동안 응답이 없어서 접속이 끊겼어요 (응답 시간 초과).\n" +
            "• 게임을 껐다 켜고 다시 접속해 주세요. 처음 접속이라 모드를 받는 중이었다면, 다 받은 뒤 다시 들어오면 됩니다.\n• 인터넷이 불안정하면 유선 연결을 권해요.\n• 다른 다운로드나 방송을 하고 있다면 잠시 멈춰 주세요."),
        (1, 9) => new(1, 9, "SHUTDOWN", LogSeverity.Info, "연결 종료",
            "연결이 닫혔습니다. 플레이어가 게임을 나갔거나 서버가 꺼질 때 남는 코드라 보통 문제가 아닙니다. 게임에 들어오기 전에 이 코드로 끊겼다면 입장 도중 취소한 경우입니다.",
            "연결이 끊겼어요. 서버가 재시작된 거라면 1~2분 뒤에 다시 접속해 주세요. (접속 코드가 바뀌었을 수 있어요.)"),
        (1, 11) => new(1, 11, "SERVER_UNREACHABLE", LogSeverity.Caution, "서버에 닿지 못함",
            "플레이어가 서버 주소로 연결하지 못했습니다. 한 사람만 그렇다면 그 사람의 네트워크(IPv6, VPN) 문제이고, 모두 그렇다면 공유기 포트포워딩(UDP 2001)·방화벽·publicAddress 설정을 확인하세요. publicAddress는 비워 두는 것이 가장 안전합니다.",
            "내 PC에서 서버까지 연결이 닿지 않았어요 (서버에 닿지 못함).\n" +
            "• 서버 목록 대신 '다이렉트 접속'에 접속 코드를 넣어 들어와 보세요.\n• VPN을 쓰고 있다면 끄고 해 주세요.\n• 그래도 안 되면 알려 주세요. 서버 쪽 설정을 확인해 볼게요."),
        (3, 3) => new(3, 3, "GAME_RESTART_REQUIRED", LogSeverity.Caution, "BattlEye: 게임 재시작 필요",
            "안티치트 BattlEye가 플레이어에게 게임 재시작을 요구했습니다. BattlEye가 업데이트됐거나 플레이어 쪽 BattlEye가 제대로 안 켜진 경우입니다.",
            "안티치트(BattlEye)가 게임 재시작을 요구해서 접속이 끊겼어요.\n" +
            "• 게임을 완전히 끄고(작업 표시줄에서도 종료) 다시 켠 뒤 접속해 주세요.\n• 계속되면 Steam에서 '게임 파일 무결성 검사'를 해 주세요."),
        (3, _) => new(3, reason, "", LogSeverity.Caution, "BattlEye가 접속을 막음",
            "안티치트 BattlEye가 플레이어를 내보냈습니다. 같은 시각 콘솔에 BattlEye 줄이 있으면 거기에 구체적인 이유가 나옵니다.",
            "안티치트(BattlEye) 문제로 접속이 끊겼어요.\n" +
            "• 게임을 완전히 끄고 다시 켠 뒤 접속해 주세요.\n• 계속되면 Steam에서 '게임 파일 무결성 검사'를 하고, 백신·오버레이 프로그램을 잠시 꺼 보세요."),
        (4, 1) => new(4, 1, "RDB_MISMATCH", LogSeverity.Problem, "모드 파일이 서버와 다름",
            "플레이어 PC의 모드 데이터가 서버와 다릅니다. 모드가 업데이트됐는데 플레이어 쪽에 옛 파일이 남아 있거나, 파일이 깨진 경우입니다. 서버 문제가 아니라 플레이어가 모드를 다시 받으면 해결됩니다.",
            "내 PC의 모드 파일이 서버와 달라서 접속이 끊겼어요 (모드 데이터 불일치). 옛날 파일이 남아 있거나 깨진 경우예요.\n" + ModFix),
        (4, 2) => new(4, 2, "SCRIPT_MISMATCH", LogSeverity.Problem, "모드 스크립트가 서버와 다름",
            "플레이어 PC의 모드 코드(스크립트)가 서버와 다릅니다. 모드 버전 차이나 깨진 모드 파일이 원인입니다. 게임 버전이 서버와 다른 경우에도 생길 수 있습니다.",
            "내 PC의 모드가 서버와 버전이 달라서 접속이 끊겼어요 (스크립트 불일치).\n" + ModFix + "\n5) 게임 자체도 최신 버전인지 Steam에서 확인해 주세요."),
        (4, _) => new(4, reason, "", LogSeverity.Problem, "게임·모드 데이터가 서버와 다름",
            "플레이어의 게임이나 모드 데이터가 서버와 맞지 않습니다. 모드 파일을 지우고 다시 받거나 게임 파일 무결성 검사를 하면 대부분 해결됩니다.",
            "내 게임/모드 파일이 서버와 맞지 않아서 접속이 끊겼어요.\n" + ModFix),
        (6, 0) => new(6, 0, "KICK", LogSeverity.Info, "강퇴됨",
            "관리자 명령이나 투표로 서버에서 내보내졌습니다.",
            "관리자나 투표로 서버에서 내보내졌어요."),
        (6, _) => new(6, reason, "", LogSeverity.Caution, "서버가 내보냄 (플레이어 관리)",
            "서버의 플레이어 관리 기능이 플레이어를 내보냈습니다. 강퇴, 차단, 또는 접속 조건(비밀번호·인원) 문제일 수 있습니다.",
            "서버에서 접속을 받지 않았어요. 서버 비밀번호와 인원이 꽉 찼는지 확인해 주세요. 계속되면 관리자에게 말해 주세요."),
        _ => new(group, reason, "", LogSeverity.Caution, $"끊김 (코드 group={group}, reason={reason})",
            $"RSM이 아직 모르는 끊김 코드입니다(group={group}, reason={reason}). 플레이어 게임 화면에는 이 숫자와 함께 영어 이름이 나옵니다. 그 이름을 물어보면 원인을 알 수 있습니다.",
            $"접속이 끊겼어요 (코드 group={group}, reason={reason}).\n" +
            "• 게임 화면에 나온 오류 문구를 캡처해서 보내 주세요.\n• 우선 게임을 껐다 켜고 다시 접속해 주세요."),
    };

    /// <summary>The player left while playing without a failure code we know: a normal leave.</summary>
    public static readonly KickCause NormalLeave = new(0, 0, "", LogSeverity.Info, "나감", "플레이어가 게임을 나갔습니다.", "");

    public static readonly KickCause AuthFailed = new(0, 0, "AUTH", LogSeverity.Caution, "인증 실패",
        "플레이어가 접속을 시도했지만 Bohemia 계정 인증을 통과하지 못했습니다. 플레이어 쪽 인터넷 문제이거나, 게임을 막 켜서 아직 로그인이 덜 된 경우, 또는 Bohemia 서비스 장애입니다.",
        "접속할 때 계정 인증이 안 돼서 들어오지 못했어요.\n" +
        "• 게임 메인 화면에서 잠깐 기다렸다가(로그인 완료 후) 다시 접속해 주세요.\n• 게임을 껐다 켜고 다시 해 주세요.\n• 다른 서버도 안 들어가지면 Bohemia 서비스 점검일 수 있어요.");

    public static readonly KickCause DroppedWhileJoining = new(0, 0, "", LogSeverity.Caution, "입장 중 끊김",
        "플레이어가 인증은 통과했지만 게임 안에 들어오기 전에 연결이 끊겼습니다. 입장 중 취소했거나, 모드를 받다 실패했거나, 인터넷이 불안정한 경우입니다.",
        "서버에 들어오는 도중에 연결이 끊겼어요.\n" +
        "• 처음 접속이라 모드를 받는 중이었다면, 게임 메인 화면에서 모드를 다 받은 뒤 다시 접속해 주세요.\n• 게임을 껐다 켜고 다시 접속해 주세요.\n• 계속되면 게임 화면에 나온 오류 문구를 캡처해서 보내 주세요.");
}
