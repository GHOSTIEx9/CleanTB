using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime;
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
    int GpuPercent,
    int? CpuTempC,
    int? RamTempC,
    int? GpuTempC
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

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    // --- Native PDH for ultra-low memory GPU Monitoring ---
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_MORE_DATA = 0x800007D2;

    [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQuery(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern int PdhCollectQueryData(IntPtr hQuery);

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_DOUBLE
    {
        public uint CStatus;
        public double doubleValue;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PDH_FMT_COUNTERVALUE_ITEM_DOUBLE
    {
        public IntPtr szName;
        public PDH_FMT_COUNTERVALUE_DOUBLE TaggedValue;
    }

    [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArrayW(
        IntPtr hCounter,
        uint dwFormat,
        ref uint lpdwBufferSize,
        ref uint lpdwItemCount,
        IntPtr lpItemBuffer
    );

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern int PdhCloseQuery(IntPtr hQuery);

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

    // Native GPU query handles (zero .NET PerformanceCounter overhead)
    private IntPtr _hGpuQuery = IntPtr.Zero;
    private IntPtr _hGpuCounter = IntPtr.Zero;
    private bool _gpuInitialized = false;
    private IntPtr _gpuPdhBuffer = IntPtr.Zero;
    private uint _gpuPdhBufferSize = 0;

    // Temperature telemetry monitor (Only active when user enables temperature toggles)
    private readonly ThermalMonitor _thermalMonitor = new();
    private int _isSampling;
    private bool _tempsWereEnabled;

    public HardwareStats CurrentStats { get; private set; }
    public ThermalAccessStatus ThermalStatus => _thermalMonitor.Status;
    public event Action<HardwareStats>? StatsUpdated;

    public void TriggerRefresh()
    {
        try
        {
            _timer.Change(0, 1000);
        }
        catch { }
    }

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
        _timer = new System.Threading.Timer(OnTick, null, 500, 1000);
    }

    private void OnTick(object? state)
    {
        if (Interlocked.Exchange(ref _isSampling, 1) != 0) return;

        try
        {
            _tickCount++;

            // 1. CPU Measurement (Win32 GetSystemTimes: ~0.0001ms, 0 allocations)
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

            // 2. RAM Measurement (Win32 GlobalMemoryStatusEx: ~0.0001ms, 0 allocations)
            int ramPercent = 0;
            double ramUsedGB = 0;
            double ramTotalGB = 0;
            if (GlobalMemoryStatusEx(_memStatus))
            {
                ramPercent = (int)_memStatus.dwMemoryLoad;
                ramTotalGB = _memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
                ramUsedGB = (_memStatus.ullTotalPhys - _memStatus.ullAvailPhys) / (1024.0 * 1024.0 * 1024.0);
            }

            // 3. Network Speed Measurement (Reliable .NET NetworkInterface with caching)
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

            // 4. GPU Measurement (Native PDH Direct Wildcard Query)
            int gpuPercent = 0;
            if (_config.ShowGpu)
            {
                gpuPercent = SampleGpuUsageNative();
            }

            // 5. Temperatures Measurement (Only initialized & polled when user enables temp toggles)
            bool wantCpuTemp = _config.ShowHardwareStats && _config.ShowCpu && _config.ShowCpuTemp;
            bool wantRamTemp = _config.ShowHardwareStats && _config.ShowRam && _config.ShowRamTemp;
            bool wantGpuTemp = _config.ShowHardwareStats && _config.ShowGpu && _config.ShowGpuTemp;
            bool anyTempWanted = wantCpuTemp || wantRamTemp || wantGpuTemp;

            int? cpuTempC = null, ramTempC = null, gpuTempC = null;
            if (anyTempWanted)
            {
                _tempsWereEnabled = true;
                (cpuTempC, ramTempC, gpuTempC) = _thermalMonitor.SampleTemperatures(wantCpuTemp, wantRamTemp, wantGpuTemp);
            }
            else if (_tempsWereEnabled)
            {
                _tempsWereEnabled = false;
                _thermalMonitor.ShutdownAll();
                try
                {
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                    EmptyWorkingSet(GetCurrentProcess());
                }
                catch { }
            }

            CurrentStats = new HardwareStats(
                cpuPercent,
                ramPercent,
                Math.Round(ramUsedGB, 1),
                Math.Round(ramTotalGB, 1),
                downloadSpeedBps,
                uploadSpeedBps,
                gpuPercent,
                cpuTempC,
                ramTempC,
                gpuTempC
            );

            StatsUpdated?.Invoke(CurrentStats);

            // 6. Periodic Working Set & Memory Trimmer (Early at tick 2, post-update at tick 8, then every 10s)
            if (_tickCount == 2 || _tickCount == 8 || _tickCount % 10 == 0)
            {
                try
                {
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                    EmptyWorkingSet(GetCurrentProcess());
                }
                catch { }
            }
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _isSampling, 0);
        }
    }

    private void RefreshNetworkInterfacesIfNeeded(bool force = false)
    {
        if (force || _cachedInterfaces == null || _cachedInterfaces.Length == 0 || (DateTime.UtcNow - _lastInterfaceRefresh).TotalSeconds > 30)
        {
            try
            {
                var list = new List<NetworkInterface>();
                foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (iface.OperationalStatus == OperationalStatus.Up &&
                        iface.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        iface.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                        !iface.Description.StartsWith("WAN Miniport", StringComparison.OrdinalIgnoreCase) &&
                        !iface.Description.Contains("Filter", StringComparison.OrdinalIgnoreCase) &&
                        !iface.Description.Contains("QoS", StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(iface);
                    }
                }

                // Fallback to any active non-loopback interface if strict physical filter found none
                if (list.Count == 0)
                {
                    foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (iface.OperationalStatus == OperationalStatus.Up &&
                            iface.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                            iface.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                        {
                            list.Add(iface);
                        }
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

        RefreshNetworkInterfacesIfNeeded();
        if (_cachedInterfaces == null || _cachedInterfaces.Length == 0)
        {
            RefreshNetworkInterfacesIfNeeded(force: true);
        }

        if (_cachedInterfaces == null) return;

        foreach (var iface in _cachedInterfaces)
        {
            try
            {
                var stats = iface.GetIPStatistics();
                totalReceived += stats.BytesReceived;
                totalSent += stats.BytesSent;
            }
            catch
            {
                // Invalidate cache if an adapter state changed
                _lastInterfaceRefresh = DateTime.MinValue;
            }
        }
    }

    private int SampleGpuUsageNative()
    {
        try
        {
            if (!_gpuInitialized)
            {
                if (PdhOpenQuery(null, IntPtr.Zero, out _hGpuQuery) == 0)
                {
                    if (PdhAddEnglishCounterW(_hGpuQuery, @"\GPU Engine(*engtype_3D*)\Utilization Percentage", IntPtr.Zero, out _hGpuCounter) == 0)
                    {
                        PdhCollectQueryData(_hGpuQuery);
                        _gpuInitialized = true;
                    }
                    else
                    {
                        PdhCloseQuery(_hGpuQuery);
                        _hGpuQuery = IntPtr.Zero;
                        return 0;
                    }
                }
                else
                {
                    return 0;
                }
            }

            if (PdhCollectQueryData(_hGpuQuery) != 0) return 0;

            uint bufferSize = 0;
            uint itemCount = 0;
            int status = PdhGetFormattedCounterArrayW(_hGpuCounter, PDH_FMT_DOUBLE, ref bufferSize, ref itemCount, IntPtr.Zero);

            if (status == unchecked((int)PDH_MORE_DATA) && bufferSize > 0)
            {
                if (_gpuPdhBuffer == IntPtr.Zero || _gpuPdhBufferSize < bufferSize)
                {
                    if (_gpuPdhBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_gpuPdhBuffer);
                    _gpuPdhBufferSize = bufferSize + 1024;
                    _gpuPdhBuffer = Marshal.AllocHGlobal((int)_gpuPdhBufferSize);
                }

                uint actualSize = _gpuPdhBufferSize;
                if (PdhGetFormattedCounterArrayW(_hGpuCounter, PDH_FMT_DOUBLE, ref actualSize, ref itemCount, _gpuPdhBuffer) == 0)
                {
                    double total = 0;
                    int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>();
                    for (int i = 0; i < itemCount; i++)
                    {
                        IntPtr itemPtr = IntPtr.Add(_gpuPdhBuffer, i * itemSize);
                        var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>(itemPtr);
                        if (item.TaggedValue.CStatus == 0 && item.TaggedValue.doubleValue > 0)
                        {
                            total += item.TaggedValue.doubleValue;
                        }
                    }
                    return (int)Math.Clamp(Math.Round(total), 0, 100);
                }
            }
        }
        catch { }

        return 0;
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
        _thermalMonitor.Dispose();
        if (_gpuPdhBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_gpuPdhBuffer);
            _gpuPdhBuffer = IntPtr.Zero;
            _gpuPdhBufferSize = 0;
        }
        if (_hGpuQuery != IntPtr.Zero)
        {
            try { PdhCloseQuery(_hGpuQuery); } catch { }
            _hGpuQuery = IntPtr.Zero;
            _hGpuCounter = IntPtr.Zero;
        }
    }
}
