using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace RSM.Core;

/// <summary>A newer RSM published on GitHub.</summary>
public sealed record AppRelease(Version Version, string Tag, string Notes, string ExeUrl, string? ShaUrl, long Size, string PageUrl)
{
    public string VersionText => $"v{Version.ToString(3)}";
}

/// <summary>What this build of RSM is: its version, and which GitHub repository its updates come from.
/// The release workflow bakes both in (-p:Version=… -p:UpdateRepository=owner/repo); a build made at
/// home with build.bat has no repository and relies on the one typed in the program's settings.</summary>
public static class AppInfo
{
    public static Version Version { get; } = ReadVersion();

    public static string BakedUpdateRepository { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value?.Trim() ?? "";

    private static Version ReadVersion()
    {
        var asm = Assembly.GetEntryAssembly();
        var info = asm?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (info != null && AppUpdater.ParseVersion(info.Split('+')[0]) is { } v)
            return v;
        return asm?.GetName().Version is { } n ? new Version(n.Major, n.Minor, Math.Max(0, n.Build)) : new Version(0, 0, 0);
    }
}

/// <summary>Self-update from GitHub Releases: asks the API for the latest release, downloads the RSM.exe
/// attached to it (checking its SHA-256 when the release has RSM.exe.sha256), and swaps it in. A running
/// .exe cannot be overwritten on Windows but can be renamed, so: RSM.exe → RSM.exe.old, new → RSM.exe,
/// start the new one, quit; the next start deletes RSM.exe.old.</summary>
public sealed class AppUpdater
{
    public const string ExeAssetName = "RSM.exe";
    public const string ShaAssetName = "RSM.exe.sha256";

    private readonly HttpClient _http;

    public AppUpdater(HttpClient http, string repository, Version current)
    {
        _http = http;
        Repository = repository.Trim().Trim('/');
        Current = current;
    }

    public string Repository { get; }
    public Version Current { get; }

    /// <summary>"owner/repo", also accepting a pasted github.com link.</summary>
    public static string NormalizeRepository(string? text)
    {
        var s = (text ?? "").Trim();
        var i = s.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
            s = s[(i + "github.com/".Length)..];
        var parts = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return "";
        var repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        return $"{parts[0]}/{repo}";
    }

    /// <summary>"v0.14.0" / "0.14" / "0.14.0-beta" → 0.14.0.</summary>
    public static Version? ParseVersion(string? text)
    {
        var s = (text ?? "").Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0)
            s = s[..cut];
        if (!Version.TryParse(s.Contains('.') ? s : s + ".0", out var v))
            return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>The latest release if it is newer than this build and has RSM.exe attached; otherwise null
    /// (the workflow attaches the exe a few minutes after the release is published).</summary>
    public async Task<AppRelease?> CheckAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        req.Headers.UserAgent.ParseAdd("RSM-updater");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null; // no release yet (or the repository is private/missing)
        resp.EnsureSuccessStatusCode();

        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)) as JsonObject;
        if (json == null || json["draft"]?.GetValue<bool>() == true || json["prerelease"]?.GetValue<bool>() == true)
            return null;
        var tag = json["tag_name"]?.GetValue<string>() ?? "";
        if (ParseVersion(tag) is not { } version || version <= Current)
            return null;

        string? exe = null, sha = null;
        long size = 0;
        foreach (var a in (json["assets"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var name = a["name"]?.GetValue<string>() ?? "";
            var url = a["browser_download_url"]?.GetValue<string>();
            if (string.Equals(name, ExeAssetName, StringComparison.OrdinalIgnoreCase))
            {
                exe = url;
                size = a["size"]?.GetValue<long>() ?? 0;
            }
            else if (string.Equals(name, ShaAssetName, StringComparison.OrdinalIgnoreCase))
                sha = url;
        }
        if (exe == null)
            return null;

        return new AppRelease(version, tag, json["body"]?.GetValue<string>()?.Trim() ?? "", exe, sha, size,
                              json["html_url"]?.GetValue<string>() ?? $"https://github.com/{Repository}/releases");
    }

    /// <summary>Downloads the new exe next to <paramref name="exePath"/> as RSM.exe.new and checks it.
    /// Returns the path of the downloaded file.</summary>
    public async Task<string> DownloadAsync(AppRelease release, string exePath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var target = exePath + ".new";
        using (var resp = await _http.GetAsync(release.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? release.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(target);
            var buffer = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (total > 0)
                    progress?.Report((double)done / total);
            }
        }

        var actual = new FileInfo(target).Length;
        if (release.Size > 0 && actual != release.Size)
            throw Fail(target, $"받은 파일 크기가 다릅니다 ({actual:N0} / {release.Size:N0} 바이트).");
        if (actual < 1024 * 1024)
            throw Fail(target, "받은 파일이 너무 작습니다. RSM.exe가 아닌 것 같습니다.");

        if (release.ShaUrl != null)
        {
            var expected = (await _http.GetStringAsync(release.ShaUrl, ct)).Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();
            string hash;
            // close the file before a possible delete: Windows will not delete a file that is still open
            await using (var f = File.OpenRead(target))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant();
            if (hash != expected)
                throw Fail(target, "받은 파일의 체크섬(SHA-256)이 릴리스와 다릅니다. 다운로드가 깨졌을 수 있습니다.");
        }
        return target;
    }

    private static InvalidDataException Fail(string file, string message)
    {
        try { File.Delete(file); } catch (IOException) { }
        return new InvalidDataException(message);
    }

    /// <summary>Puts the downloaded exe in place of the running one (renaming works while it runs).</summary>
    public static void Swap(string exePath, string newPath)
    {
        var old = exePath + ".old";
        if (File.Exists(old))
            File.Delete(old);
        File.Move(exePath, old);
        try
        {
            File.Move(newPath, exePath);
        }
        catch
        {
            File.Move(old, exePath); // put the working one back
            throw;
        }
    }

    /// <summary>Removes what an earlier update left behind (RSM.exe.old, an unfinished RSM.exe.new).</summary>
    public static void CleanupLeftovers(string exePath)
    {
        foreach (var f in new[] { exePath + ".old", exePath + ".new" })
            try
            {
                if (File.Exists(f))
                    File.Delete(f);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the old process may not have exited yet; next start tries again
            }
    }
}
