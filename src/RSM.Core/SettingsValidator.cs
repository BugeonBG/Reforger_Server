namespace RSM.Core;

/// <summary>Catches the mistakes that stop a server from starting, with messages in Korean.
/// Limits follow the Bohemia wiki's Server Config page.</summary>
public static class SettingsValidator
{
    public static List<string> Validate(ServerSettings s)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(s.ServerName))
            errors.Add("서버 이름을 입력하세요.");
        else if (s.ServerName.Length > 100)
            errors.Add("서버 이름은 100자 이하여야 합니다.");

        if (string.IsNullOrWhiteSpace(s.ScenarioId))
            errors.Add("시나리오를 선택하세요.");

        if (s.MaxPlayers < 1 || s.MaxPlayers > 256)
            errors.Add("최대 인원은 1~256 사이여야 합니다.");

        if (s.AdminPassword.Contains(' '))
            errors.Add("관리자 비밀번호에는 공백을 쓸 수 없습니다. (#login 명령에서 잘립니다)");

        CheckPort(errors, "게임 포트", s.BindPort);
        CheckPort(errors, "공개 포트", s.PublicPort);
        CheckPort(errors, "A2S 포트", s.A2sPort);
        if (s.BindPort == s.A2sPort)
            errors.Add("게임 포트와 A2S 포트가 같습니다. 서로 다른 번호를 쓰세요.");

        if (s.RconEnabled)
        {
            CheckPort(errors, "RCON 포트", s.RconPort);
            if (s.RconPort == s.BindPort || s.RconPort == s.A2sPort)
                errors.Add("RCON 포트가 게임 포트나 A2S 포트와 겹칩니다.");
            if (string.IsNullOrWhiteSpace(s.RconPassword))
                errors.Add("RCON을 쓰려면 RCON 비밀번호를 입력하세요.");
            else if (s.RconPassword.Contains(' '))
                errors.Add("RCON 비밀번호에는 공백을 쓸 수 없습니다.");
            if (s.RconPermission != "admin" && s.RconPermission != "monitor")
                errors.Add("RCON 권한은 admin 또는 monitor 여야 합니다.");
        }

        if (s.ServerMaxViewDistance < 500 || s.ServerMaxViewDistance > 10000)
            errors.Add("최대 시야 거리는 500~10000 사이여야 합니다.");
        if (s.NetworkViewDistance < 500 || s.NetworkViewDistance > 5000)
            errors.Add("네트워크 시야 거리는 500~5000 사이여야 합니다.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in s.Mods)
        {
            var label = m.Name.Length > 0 ? $"{m.ModId} ({m.Name})" : m.ModId;
            if (!ModEntry.IsValidId(m.ModId))
                errors.Add($"모드 ID가 올바르지 않습니다: {label} — 16자리 16진수여야 합니다.");
            else if (!seen.Add(m.ModId))
                errors.Add($"같은 모드가 두 번 들어 있습니다: {label}");
        }

        return errors;
    }

    private static void CheckPort(List<string> errors, string label, int port)
    {
        if (port < 1 || port > 65535)
            errors.Add($"{label}는 1~65535 사이여야 합니다.");
    }
}
