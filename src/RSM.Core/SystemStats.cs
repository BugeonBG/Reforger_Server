using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace RSM.Core;

public readonly record struct CpuTimes(ulong Idle, ulong Total);

public readonly record struct MemoryInfo(long TotalBytes, long AvailableBytes);

public readonly record struct ProcessSample(TimeSpan CpuTime, long WorkingSetBytes, int Threads);

/// <summary>Whole-PC counters (Windows for real use; Linux too, so the tests run).
/// Each call is a cheap snapshot; rates come from comparing two snapshots.</summary>
public class SystemStatsReader
{
    public virtual CpuTimes? ReadCpuTimes()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!GetSystemTimes(out var idle, out var kernel, out var user))
                    return null;
                // kernel time already includes idle time
                return new CpuTimes(idle.Value, kernel.Value + user.Value);
            }
            if (OperatingSystem.IsLinux())
            {
                var first = File.ReadLines("/proc/stat").First(); // cpu  user nice system idle iowait irq softirq steal
                var n = first.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).Select(ulong.Parse).ToArray();
                var idleAll = n[3] + (n.Length > 4 ? n[4] : 0);
                return new CpuTimes(idleAll, n.Aggregate(0UL, (a, b) => a + b));
            }
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException or InvalidOperationException)
        {
        }
        return null;
    }

    public virtual MemoryInfo? ReadMemory()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                return GlobalMemoryStatusEx(ref m) ? new MemoryInfo((long)m.ullTotalPhys, (long)m.ullAvailPhys) : null;
            }
            if (OperatingSystem.IsLinux())
            {
                long total = 0, avail = 0;
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:")) total = ParseKb(line);
                    else if (line.StartsWith("MemAvailable:")) avail = ParseKb(line);
                }
                return total > 0 ? new MemoryInfo(total, avail) : null;
            }
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
        }
        var gcTotal = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return gcTotal > 0 ? new MemoryInfo(gcTotal, -1) : null;
    }

    /// <summary>Bytes sent and received so far by every connected, non-loopback adapter.</summary>
    public virtual (long Sent, long Received)? ReadNetworkTotals()
    {
        try
        {
            long sent = 0, recv = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                var s = nic.GetIPStatistics();
                sent += s.BytesSent;
                recv += s.BytesReceived;
            }
            return (sent, recv);
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    public virtual ProcessSample? ReadProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited)
                return null;
            p.Refresh();
            return new ProcessSample(p.TotalProcessorTime, p.WorkingSet64, p.Threads.Count);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null; // gone, or not ours to look at
        }
    }

    private static long ParseKb(string line) =>
        long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) * 1024;

    //---------------------------------------------------------------------------- Windows API
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint Low;
        public uint High;
        public ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}
