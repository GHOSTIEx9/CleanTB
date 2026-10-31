using System;
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

    private PerformanceCounter? _gpuCounter;
    private bool _gpuCounterFailed;

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
        GetTotalNetworkBytes(out _prevBytesReceived, out _prevBytesSent);
        _prevNetCheckTime = DateTime.UtcNow;

        // Poll every 1000ms
        _timer = new System.Threading.Timer(OnTick, null, 1000, 1000);
    }

    private void OnTick(object? state)
    {
        try
        {
            // 1. CPU Measurement (Win32 native GetSystemTimes - ~0.0001ms)
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

            // 2. RAM Measurement (Win32 native GlobalMemoryStatusEx - ~0.0001ms)
            int ramPercent = 0;
            double ramUsedGB = 0;
            double ramTotalGB = 0;
            if (GlobalMemoryStatusEx(_memStatus))
            {
                ramPercent = (int)_memStatus.dwMemoryLoad;
                ramTotalGB = _memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
                ramUsedGB = (_memStatus.ullTotalPhys - _memStatus.ullAvailPhys) / (1024.0 * 1024.0 * 1024.0);
            }

            // 3. Network Speed Measurement
            DateTime now = DateTime.UtcNow;
            double elapsedSeconds = (now - _prevNetCheckTime).TotalSeconds;
            if (elapsedSeconds < 0.2) elapsedSeconds = 1.0;

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

            // 4. GPU Measurement (Optional, only if enabled)
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
        }
        catch { }
    }

    private static void GetTotalNetworkBytes(out long totalReceived, out long totalSent)
    {
        totalReceived = 0;
        totalSent = 0;

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var iface in interfaces)
            {
                if (iface.OperationalStatus == OperationalStatus.Up &&
                    iface.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    iface.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                {
                    var stats = iface.GetIPStatistics();
                    totalReceived += stats.BytesReceived;
                    totalSent += stats.BytesSent;
                }
            }
        }
        catch { }
    }

    private int SampleGpuUsage()
    {
        if (_gpuCounterFailed) return 0;

        try
        {
            if (_gpuCounter == null)
            {
                var category = new PerformanceCounterCategory("GPU Engine");
                var instanceNames = category.GetInstanceNames();
                foreach (var name in instanceNames)
                {
                    if (name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    {
                        _gpuCounter = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true);
                        break;
                    }
                }

                if (_gpuCounter == null)
                {
                    _gpuCounterFailed = true;
                    return 0;
                }
            }

            float val = _gpuCounter.NextValue();
            return (int)Math.Clamp(Math.Round(val), 0, 100);
        }
        catch
        {
            _gpuCounterFailed = true;
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
        _gpuCounter?.Dispose();
    }
}
