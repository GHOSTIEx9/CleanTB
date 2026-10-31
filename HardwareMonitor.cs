using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;

namespace CleanTB;

public readonly record struct HardwareStats(
    int CpuPercent,
    int RamPercent,
    double RamUsedGB,
    double RamTotalGB,
    long DownloadSpeedBps,
    long UploadSpeedBps,
    int GpuPercent
);

public class HardwareMonitor : IDisposable
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(
        out System.Runtime.InteropServices.ComTypes.FILETIME idleTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME userTime
    );

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    private static ulong FileTimeToUInt64(System.Runtime.InteropServices.ComTypes.FILETIME ft)
    {
        return ((ulong)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
    }

    private readonly System.Threading.Timer _timer;
    private readonly AppConfig _config;
    private readonly MEMORYSTATUSEX _memStatus = new();

    private ulong _prevIdleTime;
    private ulong _prevKernelTime;
    private ulong _prevUserTime;
    private DateTime _prevNetCheckTime = DateTime.UtcNow;
    private long _prevBytesReceived;
    private long _prevBytesSent;
    private bool _isFirstSample = true;
    private int _tickCount = 0;

    // Cached network interfaces to eliminate per-second allocations
    private NetworkInterface[]? _cachedInterfaces;
    private DateTime _lastInterfaceRefresh = DateTime.MinValue;

    // GPU Engine Counters
    private readonly List<PerformanceCounter> _gpuCounters = new();
    private DateTime _lastGpuCountersRefresh = DateTime.MinValue;
    private int _gpuFailureCount = 0;

    public HardwareStats CurrentStats { get; private set; }
    public event Action<HardwareStats>? StatsUpdated;

    public HardwareMonitor(AppConfig config)
    {
        _config = config;

        // Initialize CPU baseline
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            _prevIdleTime = FileTimeToUInt64(idle);
            _prevKernelTime = FileTimeToUInt64(kernel);
            _prevUserTime = FileTimeToUInt64(user);
        }

        // Initialize Net baseline
        RefreshNetworkInterfacesIfNeeded();
        GetTotalNetworkBytes(out _prevBytesReceived, out _prevBytesSent);
        _prevNetCheckTime = DateTime.UtcNow;

        // Poll every 1000ms
        _timer = new System.Threading.Timer(OnTick, null, 500, 1000);
    }

    private void OnTick(object? state)
    {
        try
        {
            _tickCount++;

            // 1. CPU Measurement (Win32 native GetSystemTimes - ~0.0001ms, 0 allocations)
            int cpuPercent = 0;
            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                ulong currentIdle = FileTimeToUInt64(idle);
                ulong currentKernel = FileTimeToUInt64(kernel);
                ulong currentUser = FileTimeToUInt64(user);

                ulong idleDelta = currentIdle - _prevIdleTime;
                ulong kernelDelta = currentKernel - _prevKernelTime;
                ulong userDelta = currentUser - _prevUserTime;
                ulong totalDelta = kernelDelta + userDelta;

                if (totalDelta > 0)
                {
                    double usage = (double)(totalDelta - idleDelta) * 100.0 / totalDelta;
                    cpuPercent = (int)Math.Clamp(Math.Round(usage), 0, 100);
                }

                _prevIdleTime = currentIdle;
                _prevKernelTime = currentKernel;
                _prevUserTime = currentUser;
            }

            // 2. RAM Measurement (Win32 native GlobalMemoryStatusEx - ~0.0001ms, 0 allocations)
            int ramPercent = 0;
            double ramUsedGB = 0;
            double ramTotalGB = 0;
            if (GlobalMemoryStatusEx(_memStatus))
            {
                ramPercent = (int)_memStatus.dwMemoryLoad;
                ramTotalGB = _memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
                ramUsedGB = (_memStatus.ullTotalPhys - _memStatus.ullAvailPhys) / (1024.0 * 1024.0 * 1024.0);
            }

            // 3. Network Speed Measurement (using cached interface descriptors)
            DateTime now = DateTime.UtcNow;
            double elapsedSeconds = (now - _prevNetCheckTime).TotalSeconds;
            if (elapsedSeconds < 0.2) elapsedSeconds = 1.0;

            RefreshNetworkInterfacesIfNeeded();
            GetTotalNetworkBytes(out long currentBytesReceived, out long currentBytesSent);

            long downloadSpeedBps = 0;
            long uploadSpeedBps = 0;

            if (!_isFirstSample)
            {
                long downDelta = currentBytesReceived - _prevBytesReceived;
                long upDelta = currentBytesSent - _prevBytesSent;

                if (downDelta > 0) downloadSpeedBps = (long)(downDelta / elapsedSeconds);
                if (upDelta > 0) uploadSpeedBps = (long)(upDelta / elapsedSeconds);
            }
            else
            {
                _isFirstSample = false;
            }

            _prevBytesReceived = currentBytesReceived;
            _prevBytesSent = currentBytesSent;
            _prevNetCheckTime = now;

            // 4. GPU Measurement (Multi-instance 3D engine aggregator)
            int gpuPercent = 0;
            if (_config.ShowGpu)
            {
                gpuPercent = SampleGpuUsage();
            }

            CurrentStats = new HardwareStats(
                cpuPercent,
                ramPercent,
                Math.Round(ramUsedGB, 1),
                Math.Round(ramTotalGB, 1),
                downloadSpeedBps,
                uploadSpeedBps,
                gpuPercent
            );

            StatsUpdated?.Invoke(CurrentStats);

            // 5. Lightweight garbage collection hint (safe, no hard page purging)
            if (_tickCount % 30 == 0)
            {
                try { GC.Collect(0, GCCollectionMode.Optimized, false); } catch { }
            }
        }
        catch { }
    }

    private void RefreshNetworkInterfacesIfNeeded()
    {
        if (_cachedInterfaces == null || (DateTime.UtcNow - _lastInterfaceRefresh).TotalSeconds > 60)
        {
            try
            {
                var list = new List<NetworkInterface>();
                foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (iface.OperationalStatus == OperationalStatus.Up &&
                        iface.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        iface.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    {
                        list.Add(iface);
                    }
                }
                _cachedInterfaces = list.ToArray();
                _lastInterfaceRefresh = DateTime.UtcNow;
            }
            catch { }
        }
    }

    private void GetTotalNetworkBytes(out long totalReceived, out long totalSent)
    {
        totalReceived = 0;
        totalSent = 0;

        if (_cachedInterfaces == null) return;

        foreach (var iface in _cachedInterfaces)
        {
            try
            {
                var stats = iface.GetIPStatistics();
                totalReceived += stats.BytesReceived;
                totalSent += stats.BytesSent;
            }
            catch { }
        }
    }

    private int SampleGpuUsage()
    {
        if (_gpuFailureCount > 5) return 0;

        try
        {
            if ((DateTime.UtcNow - _lastGpuCountersRefresh).TotalSeconds > 15 || _gpuCounters.Count == 0)
            {
                foreach (var c in _gpuCounters) { try { c.Dispose(); } catch { } }
                _gpuCounters.Clear();

                var category = new PerformanceCounterCategory("GPU Engine");
                var instanceNames = category.GetInstanceNames();
                var seenPids = new HashSet<string>();

                foreach (var name in instanceNames)
                {
                    if (name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    {
                        // Track one primary 3D engine per process to dramatically minimize handle and memory overhead
                        int pidStart = name.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
                        string pidKey = name;
                        if (pidStart >= 0)
                        {
                            int pidEnd = name.IndexOf('_', pidStart + 4);
                            if (pidEnd > pidStart)
                            {
                                pidKey = name.Substring(pidStart, pidEnd - pidStart);
                            }
                        }

                        if (seenPids.Add(pidKey))
                        {
                            try
                            {
                                var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true);
                                counter.NextValue();
                                _gpuCounters.Add(counter);
                            }
                            catch { }
                        }
                    }
                }
                _lastGpuCountersRefresh = DateTime.UtcNow;
            }

            double total = 0;
            for (int i = _gpuCounters.Count - 1; i >= 0; i--)
            {
                try
                {
                    float val = _gpuCounters[i].NextValue();
                    if (val > 0) total += val;
                }
                catch
                {
                    try { _gpuCounters[i].Dispose(); } catch { }
                    _gpuCounters.RemoveAt(i);
                }
            }

            _gpuFailureCount = 0;
            return (int)Math.Clamp(Math.Round(total), 0, 100);
        }
        catch
        {
            _gpuFailureCount++;
            _lastGpuCountersRefresh = DateTime.UtcNow;
            return 0;
        }
    }


    public static string FormatSpeed(long bytesPerSec)
    {
        if (bytesPerSec < 1024)
        {
            return $"{bytesPerSec} B/s";
        }
        else if (bytesPerSec < 1024 * 1024)
        {
            double kb = bytesPerSec / 1024.0;
            return kb >= 100 ? $"{kb:F0} KB/s" : $"{kb:F1} KB/s";
        }
        else if (bytesPerSec < 1024L * 1024 * 1024)
        {
            double mb = bytesPerSec / (1024.0 * 1024.0);
            return mb >= 100 ? $"{mb:F0} MB/s" : $"{mb:F1} MB/s";
        }
        else
        {
            double gb = bytesPerSec / (1024.0 * 1024.0 * 1024.0);
            return $"{gb:F1} GB/s";
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        foreach (var c in _gpuCounters)
        {
            try { c.Dispose(); } catch { }
        }
        _gpuCounters.Clear();
    }
}
