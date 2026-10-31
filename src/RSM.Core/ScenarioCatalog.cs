using System.Text.RegularExpressions;

namespace RSM.Core;

public sealed class ScenarioInfo
{
    public ScenarioInfo(string id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }

    /// <summary>What goes into config.json, e.g. {ECC61978EDCC2B5A}Missions/23_Campaign.conf</summary>
    public string Id { get; }

    public string DisplayName { get; }

    public override string ToString() => DisplayName;
}

public static partial class ScenarioCatalog
{
    public const string DefaultScenarioId = "{ECC61978EDCC2B5A}Missions/23_Campaign.conf";

    /// <summary>Official scenarios. Workshop scenarios are added with "서버에서 목록 불러오기".</summary>
    public static IReadOnlyList<ScenarioInfo> Official { get; } = new[]
    {
        new ScenarioInfo("{ECC61978EDCC2B5A}Missions/23_Campaign.conf", "컨플릭트 - 에버론"),
        new ScenarioInfo("{C700DB41F0C546E1}Missions/23_Campaign_NorthCentral.conf", "컨플릭트 - 북부 에버론"),
        new ScenarioInfo("{28802845ADA64D52}Missions/23_Campaign_SWCoast.conf", "컨플릭트 - 남부 에버론"),
        new ScenarioInfo("{94992A3D7CE4FF8A}Missions/23_Campaign_Western.conf", "컨플릭트 - 서부 에버론"),
        new ScenarioInfo("{FDE33AFE2ED7875B}Missions/23_Campaign_Montignac.conf", "컨플릭트 - 몽티냑"),
        new ScenarioInfo("{C41618FD18E9D714}Missions/23_Campaign_Arland.conf", "컨플릭트 - 알랜드"),
        new ScenarioInfo("{0220741028718E7F}Missions/23_Campaign_HQC_Everon.conf", "HQ 커맨더 - 에버론"),
        new ScenarioInfo("{68D1240A11492545}Missions/23_Campaign_HQC_Arland.conf", "HQ 커맨더 - 알랜드"),
        new ScenarioInfo("{BB5345C22DD2B655}Missions/23_Campaign_HQC_Cain.conf", "HQ 커맨더 - 콜구예프"),
        new ScenarioInfo("{DAA03C6E6099D50F}Missions/24_CombatOps.conf", "컴뱃 옵스 - 알랜드"),
        new ScenarioInfo("{DFAC5FABD11F2390}Missions/26_CombatOpsEveron.conf", "컴뱃 옵스 - 에버론"),
        new ScenarioInfo("{CB347F2F10065C9C}Missions/CombatOpsCain.conf", "컴뱃 옵스 - 콜구예프"),
        new ScenarioInfo("{59AD59368755F41A}Missions/21_GM_Eden.conf", "게임 마스터 - 에버론"),
        new ScenarioInfo("{2BBBE828037C6F4B}Missions/22_GM_Arland.conf", "게임 마스터 - 알랜드"),
        new ScenarioInfo("{F45C6C15D31252E6}Missions/27_GM_Cain.conf", "게임 마스터 - 콜구예프"),
        new ScenarioInfo("{3F2E005F43DBD2F8}Missions/CAH_Briars_Coast.conf", "점령 및 사수 - 브라이어스"),
        new ScenarioInfo("{F1A1BEA67132113E}Missions/CAH_Castle.conf", "점령 및 사수 - 몽포르 성"),
        new ScenarioInfo("{589945FB9FA7B97D}Missions/CAH_Concrete_Plant.conf", "점령 및 사수 - 콘크리트 공장"),
        new ScenarioInfo("{9405201CBD22A30C}Missions/CAH_Factory.conf", "점령 및 사수 - 알마라 공장"),
        new ScenarioInfo("{1CD06B409C6FAE56}Missions/CAH_Forest.conf", "점령 및 사수 - 사이먼의 숲"),
        new ScenarioInfo("{7C491B1FCC0FF0E1}Missions/CAH_LeMoule.conf", "점령 및 사수 - 르 물"),
        new ScenarioInfo("{6EA2E454519E5869}Missions/CAH_Military_Base.conf", "점령 및 사수 - 캠프 블레이크"),
        new ScenarioInfo("{2B4183DF23E88249}Missions/CAH_Morton.conf", "점령 및 사수 - 모턴"),
        new ScenarioInfo("{C47A1A6245A13B26}Missions/SP01_ReginaV2.conf", "협동 - 엘리미네이션"),
        new ScenarioInfo("{0648CDB32D6B02B3}Missions/SP02_AirSupport.conf", "협동 - 항공 지원"),
        new ScenarioInfo("{10B8582BAD9F7040}Missions/Scenario01_Intro.conf", "오퍼레이션 오메가 01: Over The Hills And Far Away"),
        new ScenarioInfo("{1D76AF6DC4DF0577}Missions/Scenario02_Steal.conf", "오퍼레이션 오메가 02: Radio Check"),
        new ScenarioInfo("{D1647575BCEA5A05}Missions/Scenario03_Villa.conf", "오퍼레이션 오메가 03: Light In The Dark"),
        new ScenarioInfo("{6D224A109B973DD8}Missions/Scenario04_Sabotage.conf", "오퍼레이션 오메가 04: Red Silence"),
        new ScenarioInfo("{FA2AB0181129CB16}Missions/Scenario05_Hill.conf", "오퍼레이션 오메가 05: Cliffhanger"),
        new ScenarioInfo("{002AF7323E0129AF}Missions/Tutorial.conf", "튜토리얼"),
    };

    [GeneratedRegex(@"\{[0-9A-Fa-f]{16}\}[^\r\n""'<>|,]+?\.conf", RegexOptions.CultureInvariant)]
    private static partial Regex ScenarioIdRegex();

    /// <summary>Pulls every scenario id out of the server's -listScenarios output, in order, without duplicates.</summary>
    public static List<string> ParseListOutput(IEnumerable<string> lines)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var line in lines)
        {
            foreach (Match m in ScenarioIdRegex().Matches(line))
            {
                if (seen.Add(m.Value))
                    result.Add(m.Value);
            }
        }
        return result;
    }

    /// <summary>Readable name for an id that is not in the official list: "Missions/Foo.conf" → "Foo".</summary>
    public static string NameFromId(string id)
    {
        var path = id.Contains('}') ? id[(id.IndexOf('}') + 1)..] : id;
        var file = path.Split('/').Last();
        return file.EndsWith(".conf", StringComparison.OrdinalIgnoreCase) ? file[..^5] : file;
    }
}
