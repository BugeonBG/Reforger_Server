using System.Globalization;
using System.Text;

namespace RSM.Core;

/// <summary>Reads the server's own console.log as it grows.
///
/// The server's stdout is buffered when it goes to a pipe: lines reach the program in bursts, sometimes a
/// minute late (a whole mod download can arrive at once, after it is over). The log file it writes itself
/// is up to date, so live progress is read from there.</summary>
public sealed class LogFileTail
{
    private const int MaxRead = 4 * 1024 * 1024;

    private string? _path;
    private long _pos;

    /// <summary>The console.log being followed, once found.</summary>
    public string? Path => _path;

    /// <summary>New complete lines since the last call. <paramref name="logsRoot"/> is &lt;profile&gt;\logs;
    /// the run's folder is the newest "logs_yyyy-MM-dd_HH-mm-ss" made at or after <paramref name="startedLocal"/>.</summary>
    public List<string> ReadNew(string logsRoot, DateTime startedLocal)
    {
        var lines = new List<string>();
        _path ??= FindRunLog(logsRoot, startedLocal);
        if (_path == null)
            return lines;

        try
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _pos)
                _pos = 0; // a new file under the same name
            var available = fs.Length - _pos;
            if (available <= 0)
                return lines;
            if (available > MaxRead)
            {
                _pos = fs.Length - MaxRead; // fell far behind: only the recent part matters
                available = MaxRead;
            }
            fs.Seek(_pos, SeekOrigin.Begin);
            var buffer = new byte[available];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = fs.Read(buffer, read, buffer.Length - read);
                if (n <= 0)
                    break;
                read += n;
            }

            // only whole lines: a line still being written is read next time
            var end = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
            if (end < 0)
                return lines;
            var text = Encoding.UTF8.GetString(buffer, 0, end + 1);
            _pos += end + 1;
            foreach (var line in text.Split('\n'))
            {
                var l = line.TrimEnd('\r');
                if (l.Length > 0)
                    lines.Add(l);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the server holds it; try again next time
        }
        return lines;
    }

    /// <summary>"…\logs\logs_2026-11-06_17-16-16\console.log" for a server started at 17:16:15.</summary>
    public static string? FindRunLog(string logsRoot, DateTime startedLocal)
    {
        try
        {
            if (!Directory.Exists(logsRoot))
                return null;
            var best = Directory.EnumerateDirectories(logsRoot, "logs_*")
                .Select(d => (dir: d, time: FolderTime(System.IO.Path.GetFileName(d))))
                .Where(x => x.time is { } t && t >= startedLocal.AddSeconds(-10))
                .OrderByDescending(x => x.time)
                .Select(x => System.IO.Path.Combine(x.dir, "console.log"))
                .FirstOrDefault(File.Exists);
            return best;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTime? FolderTime(string name) =>
        name.Length > 5 && DateTime.TryParseExact(name[5..], "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t : null;
}
