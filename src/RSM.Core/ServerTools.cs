using System.IO.Compression;
using System.Text;

namespace RSM.Core;

/// <summary>Command lines for SteamCMD, the server and the Windows firewall.</summary>
public static class ServerTools
{
    public const int StableAppId = 1874900;
    public const int ExperimentalAppId = 1890870;
    public const string SteamCmdZipUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";
    public const string FirewallRuleName = "Arma Reforger Server (RSM)";

    public static string SteamCmdExe(string steamCmdDir) => Path.Combine(steamCmdDir, "steamcmd.exe");

    /// <summary>Folder name SteamCMD uses when it installs into its own library (steamapps\common\...).</summary>
    public const string SteamFolderName = "Arma Reforger Server";

    public static int AppId(bool experimental) => experimental ? ExperimentalAppId : StableAppId;

    /// <summary>Where the server actually is: the chosen server folder, or — when SteamCMD ignored
    /// force_install_dir — SteamCMD's own library. Null when not installed anywhere we know.</summary>
    public static string? FindInstalledServer(string serverDir, string steamCmdDir)
    {
        var candidates = new[]
        {
            serverDir,
            Path.Combine(serverDir, "steamapps", "common", SteamFolderName),
            Path.Combine(steamCmdDir, "steamapps", "common", SteamFolderName),
        };
        return candidates.FirstOrDefault(d => d.Length > 0 && File.Exists(ServerExe(d)));
    }

    /// <summary>Build number of the installed server, from SteamCMD's appmanifest. With force_install_dir
    /// the manifest sits in &lt;server&gt;\steamapps; otherwise in SteamCMD's own steamapps.</summary>
    public static long? ReadInstalledBuildId(string serverDir, string steamCmdDir, int appId)
    {
        var name = $"appmanifest_{appId}.acf";
        var dirs = new List<string> { Path.Combine(serverDir, "steamapps") };
        // SteamCMD's own record only describes the copy in its own library; a shared SteamCMD may
        // remember an old install there that has nothing to do with the chosen server folder
        var library = Path.Combine(steamCmdDir, "steamapps", "common", SteamFolderName);
        if (string.Equals(Path.GetFullPath(serverDir).TrimEnd('\\', '/'), Path.GetFullPath(library).TrimEnd('\\', '/'),
                          OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            dirs.Add(Path.Combine(steamCmdDir, "steamapps"));
        foreach (var dir in dirs)
        {
            var file = Path.Combine(dir, name);
            if (!File.Exists(file))
                continue;
            try
            {
                var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(file), @"""buildid""\s+""(\d+)""");
                if (m.Success)
                    return long.Parse(m.Groups[1].Value);
            }
            catch (IOException)
            {
            }
        }
        return null;
    }

    public static string BuildAppInfoArgs(int appId) =>
        $"+login anonymous +app_info_update 1 +app_info_print {appId} +quit";

    /// <summary>Latest build of the public branch, out of SteamCMD's app_info_print output:
    /// "branches" { "public" { "buildid" "24870635" ... } }</summary>
    public static long? ParseLatestBuildId(IEnumerable<string> output)
    {
        var text = string.Join("\n", output);
        var branches = text.IndexOf("\"branches\"", StringComparison.OrdinalIgnoreCase);
        if (branches < 0)
            return null;
        var pub = text.IndexOf("\"public\"", branches, StringComparison.OrdinalIgnoreCase);
        if (pub < 0)
            return null;
        var m = System.Text.RegularExpressions.Regex.Match(text[pub..], @"""buildid""\s+""(\d+)""");
        return m.Success ? long.Parse(m.Groups[1].Value) : null;
    }

    public static string ServerExe(string serverDir) =>
        Path.Combine(serverDir, OperatingSystem.IsWindows() ? "ArmaReforgerServer.exe" : "ArmaReforgerServer");

    /// <summary>Quotes a path for a Windows command line. A trailing backslash would escape the
    /// closing quote (C:\dir\" → C:\dir"), so it is dropped.</summary>
    public static string Quote(string path)
    {
        var p = path.Trim();
        while (p.Length > 3 && (p.EndsWith('\\') || p.EndsWith('/')))
            p = p[..^1];
        if (p.EndsWith('\\'))
            p += "."; // drive root: C:\ → C:\.
        return "\"" + p + "\"";
    }

    /// <summary>Install locations that tend to break SteamCMD or the server, in plain Korean.
    /// Empty when the folders look fine.</summary>
    public static List<string> InstallPathWarnings(string steamCmdDir, string serverDir)
    {
        var warnings = new List<string>();
        static string Norm(string p)
        {
            try { return Path.GetFullPath(p.Trim()).TrimEnd('\\', '/'); }
            catch (Exception) { return p.Trim().TrimEnd('\\', '/'); }
        }

        var same = string.Equals(Norm(steamCmdDir), Norm(serverDir), StringComparison.OrdinalIgnoreCase);
        if (same)
            warnings.Add("SteamCMD 폴더와 서버 폴더가 같습니다. SteamCMD 파일과 서버 파일이 한 폴더에 섞입니다.");
        else if (Norm(steamCmdDir).StartsWith(Norm(serverDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            warnings.Add("서버 폴더 안에 SteamCMD 폴더가 있습니다. 이 구조에서는 SteamCMD가 서버를 서버 폴더가 아닌 SteamCMD 폴더 안(steamapps\\common)에 설치합니다. " +
                         "두 폴더를 나란히 두세요. 예: E:\\AR_SERVER\\steamcmd 와 E:\\AR_SERVER\\server");

        var folders = same
            ? new[] { ("설치 폴더", serverDir) }
            : new[] { ("SteamCMD 폴더", steamCmdDir), ("서버 폴더", serverDir) };
        foreach (var (label, dir) in folders)
        {
            if (dir.Contains("OneDrive", StringComparison.OrdinalIgnoreCase))
                warnings.Add($"{label}가 OneDrive 안에 있습니다. 서버 파일 수 GB가 클라우드로 동기화되고, 동기화 중에는 파일이 잠겨 설치·실행이 실패할 수 있습니다.");
            if (dir.Any(c => c > 127))
                warnings.Add($"{label} 경로에 영문이 아닌 글자가 있습니다. SteamCMD가 이런 경로에서 실패하는 경우가 많습니다.");
        }

        return warnings.Distinct().ToList();
    }

    /// <summary>SteamCMD's first run (and runs after a Steam update) often just updates SteamCMD itself,
    /// prints "Update complete, launching..." and exits (code 7) without touching the app.</summary>
    public static bool SteamCmdOnlyUpdatedItself(int exitCode, IEnumerable<string> output)
    {
        var lines = output.ToList();
        var updatedSelf = lines.Any(l => l.Contains("Update complete, launching", StringComparison.OrdinalIgnoreCase)
                                      || l.Contains("Update complete", StringComparison.OrdinalIgnoreCase) && l.Contains("[----]"));
        var touchedApp = lines.Any(l => l.Contains("Update state", StringComparison.OrdinalIgnoreCase) || l.Contains("App '", StringComparison.Ordinal));
        return (exitCode == 7 || updatedSelf) && !touchedApp;
    }

    /// <summary>Total size of everything under <paramref name="dir"/> (0 if missing). Skips what it cannot read.</summary>
    public static long DirectorySize(string dir)
    {
        if (!Directory.Exists(dir))
            return 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        long total = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", opts))
            {
                try { total += f.Length; }
                catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return total;
    }

    public static string BuildSteamCmdUpdateArgs(string serverDir, bool experimental, bool validate = true)
    {
        var sb = new StringBuilder();
        // force_install_dir must come before login, or SteamCMD ignores it
        sb.Append("+force_install_dir ").Append(Quote(serverDir));
        sb.Append(" +login anonymous");
        sb.Append(" +app_update ").Append(experimental ? ExperimentalAppId : StableAppId);
        if (validate)
            sb.Append(" validate");
        sb.Append(" +quit");
        return sb.ToString();
    }

    public const int LogStatsMs = 10000;

    public static string BuildServerArgs(string configPath, string profileDir, int maxFps, string? extraArgs)
    {
        var sb = new StringBuilder();
        sb.Append("-config ").Append(Quote(Path.GetFullPath(configPath)));
        sb.Append(" -profile ").Append(Quote(Path.GetFullPath(profileDir)));
        if (maxFps > 0)
            sb.Append(" -maxFPS ").Append(maxFps);
        // the server prints its FPS every 10 s with this; the "서버 현황" page reads it
        if (extraArgs == null || extraArgs.IndexOf("-logStats", StringComparison.OrdinalIgnoreCase) < 0)
            sb.Append(" -logStats ").Append(LogStatsMs);
        if (!string.IsNullOrWhiteSpace(extraArgs))
            sb.Append(' ').Append(extraArgs.Trim());
        return sb.ToString();
    }

    /// <summary>For cmd.exe /c: removes our previous rule, then allows the given UDP ports in.</summary>
    public static string BuildFirewallCommand(IEnumerable<int> udpPorts)
    {
        var ports = string.Join(",", udpPorts.Where(p => p > 0 && p <= 65535).Distinct().OrderBy(p => p));
        return $"/c netsh advfirewall firewall delete rule name=\"{FirewallRuleName}\" >nul 2>&1 & " +
               $"netsh advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow protocol=UDP localport={ports}";
    }

    public static IEnumerable<int> PortsToOpen(ServerSettings s)
    {
        yield return s.BindPort;
        if (s.PublicPort != s.BindPort)
            yield return s.PublicPort;
        yield return s.A2sPort;
        if (s.RconEnabled)
            yield return s.RconPort;
    }

    /// <summary>Downloads steamcmd.zip and unpacks it into <paramref name="steamCmdDir"/>.</summary>
    public static async Task DownloadSteamCmdAsync(HttpClient http, string steamCmdDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(steamCmdDir);
        var zip = Path.Combine(steamCmdDir, "steamcmd.zip");

        using (var resp = await http.GetAsync(SteamCmdZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var file = File.Create(zip);
            await resp.Content.CopyToAsync(file, ct);
        }

        ZipFile.ExtractToDirectory(zip, steamCmdDir, overwriteFiles: true);
        File.Delete(zip);
    }
}
