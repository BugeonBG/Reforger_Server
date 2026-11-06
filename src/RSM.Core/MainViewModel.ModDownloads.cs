namespace RSM.Core;

// Mod downloads while the server starts: which mod, how far, how fast, and whether anything is moving
// at all — read from the server's own console.log (its stdout arrives in late bursts), the addons folder
// size and the PC's download rate.
public sealed partial class MainViewModel
{
    private CancellationTokenSource? _modDownloadCts;
    private bool _modDownloadFromFile;
    private int _modDownloadRowsVersion = -1;

    /// <summary>The download box in the sidebar and the per-mod texts on the mods page.</summary>
    public ModDownloadProgress ModDownloads { get; } = new();

    /// <summary>Mods the server will most likely download now: not on disk, or on disk in another version
    /// than the one asked for (pinned, or the newest known for unpinned ones).</summary>
    private List<(string Id, string Name)> ExpectedModDownloads() =>
        Mods.Where(m => m.IsEnabled && ModEntry.IsValidId(m.ModId))
            .Where(m => m.Installed is not { } inst
                        || (m.Version.Length > 0 && inst.Version.Length > 0 && ModVersion.Compare(m.Version, inst.Version) != 0)
                        || (m.Version.Length == 0 && inst.Version.Length > 0 && ModVersion.IsNewer(m.LatestVersion, inst.Version)))
            .Select(m => (m.ModId, m.Name.Length > 0 ? m.Name : m.ModId))
            .ToList();

    private void BeginModDownloadWatch()
    {
        _modDownloadCts?.Cancel();
        _modDownloadFromFile = false;
        ModDownloads.Begin(ExpectedModDownloads(), DateTime.UtcNow);
        UpdateModDownloadRows();

        var cts = _modDownloadCts = new CancellationTokenSource();
        _ = WatchModDownloadsAsync(DateTime.Now, Path.Combine(ProfileDir, "logs"), AddonsDir, cts.Token);
    }

    private void EndModDownloadWatch()
    {
        ModDownloads.End(DateTime.UtcNow);
        UpdateModDownloadRows();
        // keep watching only while the "done" summary is still on screen
        if (ModDownloads.IsFinished)
            _modDownloadCts?.Cancel();
    }

    private async Task WatchModDownloadsAsync(DateTime startedLocal, string logsRoot, string addonsDir, CancellationToken ct)
    {
        var tail = new LogFileTail();
        var tick = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { return; }

            List<string> lines;
            long size = -1;
            try
            {
                lines = await Task.Run(() => tail.ReadNew(logsRoot, startedLocal), ct);
                if (tick++ % 3 == 0)
                    size = await Task.Run(() => ServerTools.DirectorySize(addonsDir), ct);
            }
            catch (OperationCanceledException) { return; }

            var net = Monitor.NetDownBytesPerSec;
            var finished = false;
            Ui(() =>
            {
                if (ct.IsCancellationRequested)
                    return;
                var now = DateTime.UtcNow;
                if (tail.Path != null)
                    _modDownloadFromFile = true;
                foreach (var line in lines)
                    ModDownloads.OnLine(line, now);
                if (size >= 0)
                    ModDownloads.OnDiskSample(size, now);
                ModDownloads.OnNetworkRate(net, now);
                ModDownloads.Tick(now);
                UpdateModDownloadRows();
                finished = ModDownloads.IsFinished;
            });
            if (finished)
                return;
        }
    }

    /// <summary>Server output line (stdout). Used only until the log file is found: stdout lags behind.</summary>
    private void OnServerOutputForDownloads(string line)
    {
        if (_modDownloadFromFile)
            return;
        Ui(() =>
        {
            if (ModDownloads.OnLine(line, DateTime.UtcNow))
                UpdateModDownloadRows();
        });
    }

    private void UpdateModDownloadRows()
    {
        if (ModDownloads.Version == _modDownloadRowsVersion && ModDownloads.IsVisible)
            return;
        _modDownloadRowsVersion = ModDownloads.Version;
        foreach (var m in Mods)
            m.DownloadText = ModDownloads.RowText(m.ModId);
        if (ModListFilter == ModListFilter.Downloading)
            RaiseModFilterChanged();
    }
}
