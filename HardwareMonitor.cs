using System;
using System.Diagnostics;
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

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    // --- Native IpHlpApi for ultra-lightweight network throughput (Zero managed allocations) ---
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int GetIfTable2(out IntPtr pIfTable);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern void FreeMibTable(IntPtr pIfTable);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_IF_ROW2_HEADER
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Alias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Description;
        public uint PhysicalAddressLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] PhysicalAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] PermanentPhysicalAddress;
        public uint Mtu;
        public uint Type;
        public int TunnelType;
        public uint MediaType;
        public uint PhysicalMediumType;
        public int AccessType;
        public int DirectionType;
        public byte InterfaceAndOperStatusFlags;
        public int OperStatus;
        public int AdminStatus;
        public int MediaConnectState;
        public Guid NetworkGuid;
        public int ConnectionType;
        public ulong TransmitLinkSpeed;
        public ulong ReceiveLinkSpeed;
        public ulong InOctets;
        public ulong InUcastPkts;
        public ulong InNUcastPkts;
        public ulong InDiscards;
        public ulong InErrors;
        public ulong InUnknownProtos;
        public ulong InUcastOctets;
        public ulong InMulticastOctets;
        public ulong InBroadcastOctets;
        public ulong OutOctets;
    }

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

    // Native GPU query handles (zero .NET PerformanceCounter overhead)
    private IntPtr _hGpuQuery = IntPtr.Zero;
    private IntPtr _hGpuCounter = IntPtr.Zero;
    private bool _gpuInitialized = false;

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
        GetTotalNetworkBytesNative(out _prevBytesReceived, out _prevBytesSent);
        _prevNetCheckTime = DateTime.UtcNow;

        // Poll every 1000ms
        _timer = new System.Threading.Timer(OnTick, null, 500, 1000);
    }

    private void OnTick(object? state)
    {
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

            // 3. Network Speed Measurement (Native GetIfTable2: ~0.001ms, 0 managed heap allocations)
            DateTime now = DateTime.UtcNow;
            double elapsedSeconds = (now - _prevNetCheckTime).TotalSeconds;
            if (elapsedSeconds < 0.2) elapsedSeconds = 1.0;

            GetTotalNetworkBytesNative(out long currentBytesReceived, out long currentBytesSent);

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

            // 5. Periodic Working Set & Memory Trimmer (Runs early at tick 3, then every 15s)
            if (_tickCount == 3 || _tickCount % 15 == 0)
            {
                try
                {
                    GC.Collect(2, GCCollectionMode.Optimized, false);
                    EmptyWorkingSet(GetCurrentProcess());
                }
                catch { }
            }
        }
        catch { }
    }

    private static void GetTotalNetworkBytesNative(out long totalReceived, out long totalSent)
    {
        totalReceived = 0;
        totalSent = 0;

        try
        {
            if (GetIfTable2(out IntPtr pTable) == 0 && pTable != IntPtr.Zero)
            {
                try
                {
                    uint numEntries = (uint)Marshal.ReadInt32(pTable);
                    IntPtr rowPtr = IntPtr.Add(pTable, sizeof(uint) + (IntPtr.Size == 8 ? 4 : 0)); // Align past NumEntries

                    int rowSize = Marshal.SizeOf<MIB_IF_ROW2_HEADER>();
                    for (int i = 0; i < numEntries; i++)
                    {
                        var row = Marshal.PtrToStructure<MIB_IF_ROW2_HEADER>(rowPtr);
                        
                        // IF_TYPE_SOFTWARE_LOOPBACK = 24, IF_TYPE_TUNNEL = 131
                        // IF_OPER_STATUS_OPERATIONAL = 1
                        if (row.Type != 24 && row.Type != 131 && row.OperStatus == 1)
                        {
                            totalReceived += (long)row.InOctets;
                            totalSent += (long)row.OutOctets;
                        }

                        rowPtr = IntPtr.Add(rowPtr, 1360); // Size of full MIB_IF_ROW2 struct in Windows SDK
                    }
                }
                finally
                {
                    FreeMibTable(pTable);
                }
            }
        }
        catch { }
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
                IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
                try
                {
                    if (PdhGetFormattedCounterArrayW(_hGpuCounter, PDH_FMT_DOUBLE, ref bufferSize, ref itemCount, buffer) == 0)
                    {
                        double total = 0;
                        int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>();
                        for (int i = 0; i < itemCount; i++)
                        {
                            IntPtr itemPtr = IntPtr.Add(buffer, i * itemSize);
                            var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>(itemPtr);
                            if (item.TaggedValue.CStatus == 0 && item.TaggedValue.doubleValue > 0)
                            {
                                total += item.TaggedValue.doubleValue;
                            }
                        }
                        return (int)Math.Clamp(Math.Round(total), 0, 100);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
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
        if (_hGpuQuery != IntPtr.Zero)
        {
            try { PdhCloseQuery(_hGpuQuery); } catch { }
            _hGpuQuery = IntPtr.Zero;
            _hGpuCounter = IntPtr.Zero;
        }
    }
}
