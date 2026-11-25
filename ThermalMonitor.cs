using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace CleanTB;

public enum ThermalAccessStatus
{
    DirectPawnIo,
    ExternalBridge,
    NeedsPawnIo,
    NeedsAdmin,
    NeedsPawnIoAndAdmin
}

public sealed class ThermalMonitor : IDisposable
{
    private readonly object _lock = new();
    private bool _disposed;

    // --- LibreHardwareMonitorLib (Direct MSR / RAM SPD via PawnIO) ---
    private Computer? _lhmComputer;
    private bool _lhmInitAttempted;
    private bool _lhmMemoryEnabled;
    private int? _cachedLhmRamTempC;
    private int _ramSampleCounter;

    // Reusable unmanaged buffers to prevent per-second native heap allocations
    private IntPtr _d3dAdaptersPtr = IntPtr.Zero;
    private uint _d3dAdaptersCap = 0;
    private IntPtr _d3dTypePtr = IntPtr.Zero;
    private IntPtr _d3dPerfPtr = IntPtr.Zero;
    private IntPtr _nvBufPtr = IntPtr.Zero;

    // --- D3DKMT (Native Windows User-Mode GPU Thermal Telemetry) ---
    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTERINFO
    {
        public uint hAdapter;
        public LUID AdapterLuid;
        public uint NumOfSources;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bPresentMoveRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ENUMADAPTERS2
    {
        public uint NumAdapters;
        public IntPtr pAdapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER
    {
        public uint hAdapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTER_PERFDATA
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOC;
        public ulong MemoryBandwidth;
        public ulong PCIEBandwidth;
        public uint FanRPM;
        public uint Power;
        public uint Temperature; // tenths of Celsius
        public byte PowerStateOverride;
    }

    private const int KMTQAITYPE_ADAPTERTYPE = 15;
    private const int KMTQAITYPE_ADAPTERPERFDATA = 62;
    private const uint ADAPTERTYPE_RENDER_SUPPORTED = 0x0001;
    private const uint ADAPTERTYPE_SOFTWARE_DEVICE = 0x0004;
    private const uint ADAPTERTYPE_HYBRID_DISCRETE = 0x0010;

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 pEnum);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO pQuery);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER pClose);

    // --- NVAPI (NVIDIA User-Mode Fallback) ---
    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr NvAPI_QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiInitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiUnloadDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiEnumPhysicalGpusDelegate([Out] IntPtr[] handles, out int count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiGpuGetThermalSettingsDelegate(IntPtr hPhysicalGpu, int sensorIndex, IntPtr pThermalSettings);

    private bool _nvChecked;
    private bool _nvAvailable;
    private NvApiEnumPhysicalGpusDelegate? _nvEnumGpus;
    private NvApiGpuGetThermalSettingsDelegate? _nvGetThermal;
    private NvApiUnloadDelegate? _nvUnload;

    // --- AMD ADL2 (Radeon / APU User-Mode Fallback) ---
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr AdlMainMallocCallback(int size);

    private static readonly AdlMainMallocCallback s_adlMalloc = size => Marshal.AllocHGlobal(size);

    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Main_Control_Create(AdlMainMallocCallback callback, int enumConnectedAdapters, out IntPtr context);

    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Main_Control_Destroy(IntPtr context);

    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr context, ref int numAdapters);

    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_New_QueryPMLogData_Get(IntPtr context, int adapterIndex, out AdlPmLogDataOutput dataOutput);

    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_OverdriveN_Temperature_Get(IntPtr context, int adapterIndex, int tempType, ref int tempMilliC);

    [DllImport("atiadlxx.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Overdrive5_Temperature_Get(IntPtr context, int adapterIndex, int thermalControllerIndex, ref AdlTemperature temp);

    [StructLayout(LayoutKind.Sequential)]
    private struct AdlSingleSensorData
    {
        public int Supported;
        public int Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdlPmLogDataOutput
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public AdlSingleSensorData[] Sensors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdlTemperature
    {
        public int Size;
        public int Temperature; // millidegrees Celsius
    }

    private bool _adlChecked;
    private bool _adlAvailable;
    private IntPtr _adlContext = IntPtr.Zero;

    // --- Native Win32 Shared Memory (Zero-exception probe) ---
    private const uint FILE_MAP_READ = 0x0004;

    [DllImport("kernel32.dll", EntryPoint = "OpenFileMappingW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenFileMapping(uint dwDesiredAccess, bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public UIntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, UIntPtr dwLength);

    private static ulong GetMappedRegionSize(IntPtr lpBaseAddress)
    {
        if (lpBaseAddress == IntPtr.Zero) return 0;
        if (VirtualQuery(lpBaseAddress, out MEMORY_BASIC_INFORMATION mbi, (UIntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == UIntPtr.Zero)
        {
            return 0;
        }
        return mbi.RegionSize.ToUInt64();
    }

    // Core Temp shared memory struct (x86/x64 4-byte packed SDK layout)
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 4)]
    private struct CoreTempSharedDataEx
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public uint[] uiLoad;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
        public uint[] uiTjMax;
        public uint uiCoreCnt;
        public uint uiCPUCnt;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public float[] fTemp;
        public float fVID;
        public float fCPUSpeed;
        public float fFSBSpeed;
        public float fMultiplier;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 100)]
        public string sCPUName;
        public byte ucFahrenheit;
        public byte ucDeltaToTjMax;
    }

    // WMI fallback cooldown for LibreHardwareMonitor / OpenHardwareMonitor
    private DateTime _lastWmiCheckUtc = DateTime.MinValue;
    private string? _activeWmiNamespace;

    public ThermalAccessStatus Status { get; private set; } = DetermineBaseStatus();

    public static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsPawnIoInstalled()
    {
        try
        {
            if (LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled)
                return true;
        }
        catch { }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO", false);
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    private static ThermalAccessStatus DetermineBaseStatus()
    {
        bool isAdmin = IsRunningAsAdministrator();
        bool hasPawnIo = IsPawnIoInstalled();
        if (isAdmin && hasPawnIo) return ThermalAccessStatus.DirectPawnIo;
        if (isAdmin) return ThermalAccessStatus.NeedsPawnIo;
        if (hasPawnIo) return ThermalAccessStatus.NeedsAdmin;
        return ThermalAccessStatus.NeedsPawnIoAndAdmin;
    }

    public (int? cpuTempC, int? ramTempC, int? gpuTempC) SampleTemperatures(bool wantCpu, bool wantRam, bool wantGpu)
    {
        lock (_lock)
        {
            if (_disposed) return (null, null, null);

            int? cpuTemp = null;
            int? ramTemp = null;
            int? gpuTemp = null;

            bool isAdmin = IsRunningAsAdministrator();
            bool hasPawnIo = IsPawnIoInstalled();

            // 1. Direct Kernel Sensors via LibreHardwareMonitorLib + PawnIO (only when CPU/RAM temp enabled & Admin + PawnIO active)
            if ((wantCpu || wantRam) && isAdmin && hasPawnIo)
            {
                SampleFromLhm(wantCpu, wantRam, out cpuTemp, out ramTemp);
            }
            else if (!wantCpu && !wantRam && _lhmComputer != null)
            {
                ReleaseLhmEngine();
            }

            // 2. Native User-Mode GPU Temperature (WDDM D3DKMT -> Optimus-safe)
            bool dGPUAsleepInD3Cold = false;
            if (wantGpu)
            {
                gpuTemp = SampleGpuTempD3dkmt(out dGPUAsleepInD3Cold);
            }

            // 3. External Zero-Admin Bridges (HWiNFO64, Core Temp, AIDA64, LibreHardwareMonitor WMI)
            bool needCpu = wantCpu && !cpuTemp.HasValue;
            bool needRam = wantRam && !ramTemp.HasValue;
            bool needGpu = wantGpu && !gpuTemp.HasValue && !dGPUAsleepInD3Cold;

            bool bridgeProvidedCpuOrRam = false;
            if (needCpu || needRam || needGpu)
            {
                TryReadExternalBridges(needCpu, needRam, needGpu, ref cpuTemp, ref ramTemp, ref gpuTemp, out bridgeProvidedCpuOrRam);
            }

            // 4. Native Vendor User-Mode Fallbacks for GPU (only if D3DKMT did not find sleeping discrete GPU)
            if (wantGpu && !gpuTemp.HasValue && !dGPUAsleepInD3Cold)
            {
                gpuTemp = SampleGpuTempNvApi() ?? SampleGpuTempAdl2();
            }

            // Update diagnostic status cleanly for the tray UI
            if (isAdmin && hasPawnIo)
            {
                Status = ThermalAccessStatus.DirectPawnIo;
            }
            else if (bridgeProvidedCpuOrRam)
            {
                Status = ThermalAccessStatus.ExternalBridge;
            }
            else if (isAdmin)
            {
                Status = ThermalAccessStatus.NeedsPawnIo;
            }
            else
            {
                Status = hasPawnIo ? ThermalAccessStatus.NeedsAdmin : ThermalAccessStatus.NeedsPawnIoAndAdmin;
            }

            return (cpuTemp, ramTemp, gpuTemp);
        }
    }

    private void SampleFromLhm(bool wantCpu, bool wantRam, out int? cpuTemp, out int? ramTemp)
    {
        cpuTemp = null;
        ramTemp = null;

        try
        {
            if (!_lhmInitAttempted)
            {
                _lhmInitAttempted = true;
                _lhmMemoryEnabled = wantRam;
                _lhmComputer = new Computer
                {
                    IsCpuEnabled = true,
                    IsMemoryEnabled = wantRam,
                    IsGpuEnabled = false,
                    IsMotherboardEnabled = false,
                    IsStorageEnabled = false,
                    IsNetworkEnabled = false,
                    IsControllerEnabled = false,
                    IsPsuEnabled = false,
                    IsBatteryEnabled = false
                };
                _lhmComputer.Open();
            }
            else if (_lhmComputer != null && wantRam && !_lhmMemoryEnabled)
            {
                _lhmMemoryEnabled = true;
                _lhmComputer.IsMemoryEnabled = true;
            }

            if (_lhmComputer == null) return;

            _ramSampleCounter++;
            bool pollRamThisTick = wantRam && (_ramSampleCounter == 1 || _ramSampleCounter % 2 == 0);

            float? packageTemp = null;
            float? coreMaxTemp = null;
            float? tctlTemp = null;
            float? maxIndividualCore = null;
            float? maxRamTemp = null;

            foreach (var hw in _lhmComputer.Hardware)
            {
                if (hw.HardwareType == HardwareType.Cpu && wantCpu)
                {
                    hw.Update();
                    foreach (var s in hw.Sensors)
                    {
                        if (s.SensorType != SensorType.Temperature || !s.Value.HasValue) continue;
                        float val = s.Value.Value;
                        if (val < 5f || val > 130f) continue;

                        string name = s.Name;
                        if (name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase)) continue;

                        if (name.Equals("Core Max", StringComparison.OrdinalIgnoreCase))
                        {
                            coreMaxTemp = val;
                        }
                        else if (name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase) ||
                                 name.Equals("Package", StringComparison.OrdinalIgnoreCase))
                        {
                            packageTemp = val;
                        }
                        else if (name.Contains("Tctl/Tdie", StringComparison.OrdinalIgnoreCase) ||
                                 name.Contains("Tdie", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!tctlTemp.HasValue || val > tctlTemp.Value) tctlTemp = val;
                        }
                        else
                        {
                            if (!maxIndividualCore.HasValue || val > maxIndividualCore.Value) maxIndividualCore = val;
                        }
                    }
                }
                else if (hw.HardwareType == HardwareType.Memory && pollRamThisTick)
                {
                    hw.Update();
                    ExtractMemoryTempFromHardware(hw, ref maxRamTemp);
                    foreach (var sub in hw.SubHardware)
                    {
                        sub.Update();
                        ExtractMemoryTempFromHardware(sub, ref maxRamTemp);
                    }
                }
            }

            float? chosenCpu = packageTemp ?? tctlTemp ?? coreMaxTemp ?? maxIndividualCore;
            if (chosenCpu.HasValue)
            {
                cpuTemp = (int)Math.Round(chosenCpu.Value);
            }

            if (pollRamThisTick)
            {
                _cachedLhmRamTempC = maxRamTemp.HasValue ? (int)Math.Round(maxRamTemp.Value) : null;
            }
            if (wantRam)
            {
                ramTemp = _cachedLhmRamTempC;
            }
        }
        catch
        {
            // Keep running cleanly without crashing if driver resets or is unloaded
        }
    }

    private static bool IsStaticThermalMetadataName(string name)
    {
        return name.Contains("Limit", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Resolution", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Critical", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Threshold", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Warning", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Offset", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("High", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Low", StringComparison.OrdinalIgnoreCase);
    }

    private static void ExtractMemoryTempFromHardware(IHardware hw, ref float? maxRamTemp)
    {
        foreach (var s in hw.Sensors)
        {
            if (s.SensorType != SensorType.Temperature || !s.Value.HasValue) continue;
            if (IsStaticThermalMetadataName(s.Name ?? "")) continue;

            float val = s.Value.Value;
            if (val >= 5f && val <= 115f)
            {
                if (!maxRamTemp.HasValue || val > maxRamTemp.Value) maxRamTemp = val;
            }
        }
    }

    private int? SampleGpuTempD3dkmt(out bool discreteAdapterAsleep)
    {
        discreteAdapterAsleep = false;
        try
        {
            var enumArgs = new D3DKMT_ENUMADAPTERS2 { NumAdapters = 0, pAdapters = IntPtr.Zero };
            int st = D3DKMTEnumAdapters2(ref enumArgs);
            if (enumArgs.NumAdapters == 0) return null;

            int itemSize = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
            if (_d3dAdaptersPtr == IntPtr.Zero || _d3dAdaptersCap < enumArgs.NumAdapters)
            {
                if (_d3dAdaptersPtr != IntPtr.Zero) Marshal.FreeHGlobal(_d3dAdaptersPtr);
                _d3dAdaptersCap = enumArgs.NumAdapters + 4;
                _d3dAdaptersPtr = Marshal.AllocHGlobal(itemSize * (int)_d3dAdaptersCap);
            }
            enumArgs.pAdapters = _d3dAdaptersPtr;

            st = D3DKMTEnumAdapters2(ref enumArgs);
            if (st != 0 || enumArgs.NumAdapters == 0) return null;

            int? discreteTemp = null;
            int? fallbackTemp = null;

            int perfSize = Marshal.SizeOf<D3DKMT_ADAPTER_PERFDATA>();
            if (_d3dTypePtr == IntPtr.Zero) _d3dTypePtr = Marshal.AllocHGlobal(4);
            if (_d3dPerfPtr == IntPtr.Zero) _d3dPerfPtr = Marshal.AllocHGlobal(perfSize);

            for (int i = 0; i < enumArgs.NumAdapters; i++)
            {
                var info = Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(IntPtr.Add(_d3dAdaptersPtr, i * itemSize));
                try
                {
                    Marshal.WriteInt32(_d3dTypePtr, 0);
                    var qType = new D3DKMT_QUERYADAPTERINFO
                    {
                        hAdapter = info.hAdapter,
                        Type = KMTQAITYPE_ADAPTERTYPE,
                        pPrivateDriverData = _d3dTypePtr,
                        PrivateDriverDataSize = 4
                    };

                    uint typeFlags = 0;
                    if (D3DKMTQueryAdapterInfo(ref qType) == 0)
                    {
                        typeFlags = (uint)Marshal.ReadInt32(_d3dTypePtr);
                    }

                    // Skip software adapters (Microsoft Basic Render Driver) and non-render adapters
                    if ((typeFlags & ADAPTERTYPE_RENDER_SUPPORTED) == 0 || (typeFlags & ADAPTERTYPE_SOFTWARE_DEVICE) != 0)
                    {
                        continue;
                    }

                    bool isDiscrete = (typeFlags & ADAPTERTYPE_HYBRID_DISCRETE) != 0;

                    for (int b = 0; b < perfSize; b++) Marshal.WriteByte(_d3dPerfPtr, b, 0);
                    var qPerf = new D3DKMT_QUERYADAPTERINFO
                    {
                        hAdapter = info.hAdapter,
                        Type = KMTQAITYPE_ADAPTERPERFDATA,
                        pPrivateDriverData = _d3dPerfPtr,
                        PrivateDriverDataSize = (uint)perfSize
                    };

                    int perfSt = D3DKMTQueryAdapterInfo(ref qPerf);
                    if (perfSt == 0)
                    {
                        var perf = Marshal.PtrToStructure<D3DKMT_ADAPTER_PERFDATA>(_d3dPerfPtr);
                        if (perf.Temperature > 0 && perf.Temperature < 1500)
                        {
                            int tempC = (int)Math.Round(perf.Temperature / 10.0);
                            if (isDiscrete)
                            {
                                if (!discreteTemp.HasValue || tempC > discreteTemp.Value) discreteTemp = tempC;
                            }
                            else
                            {
                                if (!fallbackTemp.HasValue || tempC > fallbackTemp.Value) fallbackTemp = tempC;
                            }
                        }
                        else if (isDiscrete && perf.Temperature == 0)
                        {
                            // Discrete adapter supports D3DKMT perf data but is sleeping (Optimus D3Cold)
                            discreteAdapterAsleep = true;
                        }
                    }
                }
                finally
                {
                    var close = new D3DKMT_CLOSEADAPTER { hAdapter = info.hAdapter };
                    D3DKMTCloseAdapter(ref close);
                }
            }

            if (discreteTemp.HasValue)
            {
                discreteAdapterAsleep = false;
                return discreteTemp;
            }
            return fallbackTemp;
        }
        catch
        {
            return null;
        }
    }

    private int? SampleGpuTempNvApi()
    {
        if (!Environment.Is64BitProcess) return null;

        try
        {
            if (!_nvChecked)
            {
                _nvChecked = true;
                if (NativeLibrary.TryLoad("nvapi64.dll", out _))
                {
                    IntPtr pInit = NvAPI_QueryInterface(0x0150E828);
                    IntPtr pEnum = NvAPI_QueryInterface(0xE5AC921F);
                    IntPtr pTherm = NvAPI_QueryInterface(0xE3640A56);
                    IntPtr pUnload = NvAPI_QueryInterface(0xD22BDD7E);

                    if (pInit != IntPtr.Zero && pEnum != IntPtr.Zero && pTherm != IntPtr.Zero)
                    {
                        var init = Marshal.GetDelegateForFunctionPointer<NvApiInitializeDelegate>(pInit);
                        if (init() == 0)
                        {
                            _nvEnumGpus = Marshal.GetDelegateForFunctionPointer<NvApiEnumPhysicalGpusDelegate>(pEnum);
                            _nvGetThermal = Marshal.GetDelegateForFunctionPointer<NvApiGpuGetThermalSettingsDelegate>(pTherm);
                            if (pUnload != IntPtr.Zero)
                            {
                                _nvUnload = Marshal.GetDelegateForFunctionPointer<NvApiUnloadDelegate>(pUnload);
                            }
                            _nvAvailable = true;
                        }
                    }
                }
            }

            if (!_nvAvailable || _nvEnumGpus == null || _nvGetThermal == null) return null;

            var handles = new IntPtr[64];
            if (_nvEnumGpus(handles, out int count) != 0 || count <= 0) return null;

            if (_nvBufPtr == IntPtr.Zero) _nvBufPtr = Marshal.AllocHGlobal(68);
            int? maxTemp = null;
            for (int i = 0; i < count; i++)
            {
                for (int b = 0; b < 68; b++) Marshal.WriteByte(_nvBufPtr, b, 0);
                Marshal.WriteInt32(_nvBufPtr, 0, 68 | (2 << 16)); // NV_GPU_THERMAL_SETTINGS_VER_2

                if (_nvGetThermal(handles[i], 15, _nvBufPtr) == 0)
                {
                    int sensorCount = Math.Min(Marshal.ReadInt32(_nvBufPtr, 4), 3);
                    for (int s = 0; s < sensorCount; s++)
                    {
                        int curTemp = Marshal.ReadInt32(_nvBufPtr, 8 + s * 20 + 12);
                        if (curTemp > 5 && curTemp < 140)
                        {
                            if (!maxTemp.HasValue || curTemp > maxTemp.Value) maxTemp = curTemp;
                        }
                    }
                }
            }
            return maxTemp;
        }
        catch
        {
            return null;
        }
    }

    private int? SampleGpuTempAdl2()
    {
        if (!Environment.Is64BitProcess) return null;

        try
        {
            if (!_adlChecked)
            {
                _adlChecked = true;
                if (NativeLibrary.TryLoad("atiadlxx.dll", out _))
                {
                    if (ADL2_Main_Control_Create(s_adlMalloc, 1, out _adlContext) == 0 && _adlContext != IntPtr.Zero)
                    {
                        _adlAvailable = true;
                    }
                }
            }

            if (!_adlAvailable || _adlContext == IntPtr.Zero) return null;

            int numAdapters = 0;
            if (ADL2_Adapter_NumberOfAdapters_Get(_adlContext, ref numAdapters) != 0 || numAdapters <= 0)
                return null;

            int? bestTemp = null;
            for (int i = 0; i < numAdapters; i++)
            {
                try
                {
                    var pmLog = new AdlPmLogDataOutput { Size = Marshal.SizeOf<AdlPmLogDataOutput>() };
                    if (ADL2_New_QueryPMLogData_Get(_adlContext, i, out pmLog) == 0 && pmLog.Sensors != null)
                    {
                        // 8 = PMLOG_TEMPERATURE_EDGE, 28 = PMLOG_TEMPERATURE_GFX
                        int[] indices = { 8, 28 };
                        foreach (int idx in indices)
                        {
                            if (idx < pmLog.Sensors.Length && pmLog.Sensors[idx].Supported != 0)
                            {
                                int t = pmLog.Sensors[idx].Value;
                                if (t > 5 && t < 140)
                                {
                                    if (!bestTemp.HasValue || t > bestTemp.Value) bestTemp = t;
                                }
                            }
                        }
                    }
                }
                catch { }

                if (!bestTemp.HasValue)
                {
                    try
                    {
                        int milliC = 0;
                        if (ADL2_OverdriveN_Temperature_Get(_adlContext, i, 1, ref milliC) == 0 && milliC > 5000)
                        {
                            int t = (int)Math.Round(milliC / 1000.0);
                            if (t > 5 && t < 140) bestTemp = t;
                        }
                    }
                    catch { }
                }

                if (!bestTemp.HasValue)
                {
                    try
                    {
                        var od5 = new AdlTemperature { Size = Marshal.SizeOf<AdlTemperature>() };
                        if (ADL2_Overdrive5_Temperature_Get(_adlContext, i, 0, ref od5) == 0 && od5.Temperature > 5000)
                        {
                            int t = (int)Math.Round(od5.Temperature / 1000.0);
                            if (t > 5 && t < 140) bestTemp = t;
                        }
                    }
                    catch { }
                }
            }

            return bestTemp;
        }
        catch
        {
            return null;
        }
    }

    private void TryReadExternalBridges(
        bool needCpu,
        bool needRam,
        bool needGpu,
        ref int? cpuTemp,
        ref int? ramTemp,
        ref int? gpuTemp,
        out bool bridgeProvidedCpuOrRam)
    {
        bridgeProvidedCpuOrRam = false;

        // 1. HWiNFO64 Shared Memory (supports CPU, RAM DIMM, and GPU)
        if (TryReadHwInfo(needCpu, needRam, needGpu, out int? hwCpu, out int? hwRam, out int? hwGpu))
        {
            if (needCpu && hwCpu.HasValue) { cpuTemp = hwCpu; needCpu = false; bridgeProvidedCpuOrRam = true; }
            if (needRam && hwRam.HasValue) { ramTemp = hwRam; needRam = false; bridgeProvidedCpuOrRam = true; }
            if (needGpu && hwGpu.HasValue) { gpuTemp = hwGpu; needGpu = false; }
        }

        // 2. Core Temp Shared Memory (CPU)
        if (needCpu && TryReadCoreTemp(out int? ctCpu) && ctCpu.HasValue)
        {
            cpuTemp = ctCpu;
            needCpu = false;
            bridgeProvidedCpuOrRam = true;
        }

        // 3. AIDA64 Shared Memory (CPU, RAM DIMM, GPU)
        if ((needCpu || needRam || needGpu) && TryReadAida64(needCpu, needRam, needGpu, out int? aCpu, out int? aRam, out int? aGpu))
        {
            if (needCpu && aCpu.HasValue) { cpuTemp = aCpu; needCpu = false; bridgeProvidedCpuOrRam = true; }
            if (needRam && aRam.HasValue) { ramTemp = aRam; needRam = false; bridgeProvidedCpuOrRam = true; }
            if (needGpu && aGpu.HasValue) { gpuTemp = aGpu; needGpu = false; }
        }

        // 4. LibreHardwareMonitor / OpenHardwareMonitor WMI Bridge (throttled discovery every 15s when inactive)
        if (needCpu || needRam || needGpu)
        {
            if (TryReadLhmWmi(needCpu, needRam, needGpu, out int? wCpu, out int? wRam, out int? wGpu))
            {
                if (needCpu && wCpu.HasValue) { cpuTemp = wCpu; bridgeProvidedCpuOrRam = true; }
                if (needRam && wRam.HasValue) { ramTemp = wRam; bridgeProvidedCpuOrRam = true; }
                if (needGpu && wGpu.HasValue) { gpuTemp = wGpu; }
            }
        }
    }

    private static bool TryReadHwInfo(
        bool wantCpu,
        bool wantRam,
        bool wantGpu,
        out int? cpuTemp,
        out int? ramTemp,
        out int? gpuTemp)
    {
        cpuTemp = null;
        ramTemp = null;
        gpuTemp = null;

        IntPtr hMap = OpenFileMapping(FILE_MAP_READ, false, "Global\\HWiNFO_SENS_SM2");
        if (hMap == IntPtr.Zero) return false;

        IntPtr pBase = IntPtr.Zero;
        try
        {
            pBase = MapViewOfFile(hMap, FILE_MAP_READ, 0, 0, UIntPtr.Zero);
            if (pBase == IntPtr.Zero) return false;

            ulong regionSize = GetMappedRegionSize(pBase);
            if (regionSize < 44) return false;

            uint magic = (uint)Marshal.ReadInt32(pBase, 0);
            if (magic != 0x53695748) return false; // "SiWH"

            uint sensorOffset = (uint)Marshal.ReadInt32(pBase, 20);
            uint sensorElemSize = (uint)Marshal.ReadInt32(pBase, 24);
            uint sensorCount = (uint)Marshal.ReadInt32(pBase, 28);
            uint entryOffset = (uint)Marshal.ReadInt32(pBase, 32);
            uint entryElemSize = (uint)Marshal.ReadInt32(pBase, 36);
            uint entryCount = (uint)Marshal.ReadInt32(pBase, 40);

            if (sensorCount > 512 || entryCount > 8192 || sensorElemSize < 264 || entryElemSize < 292)
                return false;

            ulong sensorEnd = (ulong)sensorOffset + (ulong)sensorCount * sensorElemSize;
            ulong entryEnd = (ulong)entryOffset + (ulong)entryCount * entryElemSize;
            if (sensorEnd > regionSize || entryEnd > regionSize)
                return false;

            var scratchBuf = new byte[256];
            var sensorNames = new string[sensorCount];
            for (uint s = 0; s < sensorCount; s++)
            {
                IntPtr sPtr = IntPtr.Add(pBase, (int)(sensorOffset + s * sensorElemSize));
                string user = ReadNullTerminatedAnsi(IntPtr.Add(sPtr, 136), 128, scratchBuf);
                sensorNames[s] = string.IsNullOrEmpty(user) ? ReadNullTerminatedAnsi(IntPtr.Add(sPtr, 8), 128, scratchBuf) : user;
            }

            double? cpuPkg = null, cpuMaxCore = null;
            double? maxRam = null;
            double? maxGpu = null;

            for (uint i = 0; i < entryCount; i++)
            {
                IntPtr ePtr = IntPtr.Add(pBase, (int)(entryOffset + i * entryElemSize));
                int tReading = Marshal.ReadInt32(ePtr, 0);
                if (tReading != 1) continue; // SENSOR_TYPE_TEMP = 1

                uint sensorIdx = (uint)Marshal.ReadInt32(ePtr, 4);
                string sensorName = sensorIdx < sensorCount ? sensorNames[sensorIdx] : "";
                string labelOrig = ReadNullTerminatedAnsi(IntPtr.Add(ePtr, 12), 128, scratchBuf);
                string labelUser = ReadNullTerminatedAnsi(IntPtr.Add(ePtr, 140), 128, scratchBuf);
                string label = string.IsNullOrEmpty(labelUser) ? labelOrig : labelUser;
                string unit = ReadNullTerminatedAnsi(IntPtr.Add(ePtr, 268), 16, scratchBuf);

                long valBits = Marshal.ReadInt64(ePtr, 284);
                double val = BitConverter.Int64BitsToDouble(valBits);
                if (double.IsNaN(val) || double.IsInfinity(val)) continue;

                // Normalize Fahrenheit to Celsius if HWiNFO is configured in °F
                if (unit.EndsWith("F", StringComparison.OrdinalIgnoreCase))
                {
                    val = (val - 32.0) * 5.0 / 9.0;
                }

                if (val < 5.0 || val > 135.0) continue;

                bool isGpuSensor = sensorName.Contains("GPU", StringComparison.OrdinalIgnoreCase) ||
                                   sensorName.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                                   sensorName.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
                                   sensorName.Contains("Arc", StringComparison.OrdinalIgnoreCase);

                if (wantCpu && !isGpuSensor &&
                               (sensorName.Contains("CPU", StringComparison.OrdinalIgnoreCase) ||
                                sensorName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase) ||
                                sensorName.Contains("Intel Core", StringComparison.OrdinalIgnoreCase)))
                {
                    if (label.Contains("Distance to", StringComparison.OrdinalIgnoreCase) ||
                        label.Contains("Throttle", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (label.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                        label.Contains("Tctl/Tdie", StringComparison.OrdinalIgnoreCase) ||
                        label.Contains("Core Max", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!cpuPkg.HasValue || val > cpuPkg.Value) cpuPkg = val;
                    }
                    else if (label.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
                             label.Equals("CPU", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!cpuMaxCore.HasValue || val > cpuMaxCore.Value) cpuMaxCore = val;
                    }
                }
                else if (wantRam && !isGpuSensor &&
                                    !label.Contains("GPU", StringComparison.OrdinalIgnoreCase) &&
                                    !IsStaticThermalMetadataName(label) &&
                                    (sensorName.Contains("DIMM", StringComparison.OrdinalIgnoreCase) ||
                                     sensorName.Contains("DDR", StringComparison.OrdinalIgnoreCase) ||
                                     sensorName.Contains("SPD", StringComparison.OrdinalIgnoreCase) ||
                                     label.Contains("DIMM", StringComparison.OrdinalIgnoreCase) ||
                                     label.Contains("SPD Hub", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!maxRam.HasValue || val > maxRam.Value) maxRam = val;
                }
                else if (wantGpu && isGpuSensor)
                {
                    if (label.Equals("GPU Temperature", StringComparison.OrdinalIgnoreCase) ||
                        label.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) ||
                        label.Contains("GPU Temp", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!maxGpu.HasValue || val > maxGpu.Value) maxGpu = val;
                    }
                }
            }

            if (wantCpu && (cpuPkg ?? cpuMaxCore).HasValue) cpuTemp = (int)Math.Round((cpuPkg ?? cpuMaxCore)!.Value);
            if (wantRam && maxRam.HasValue) ramTemp = (int)Math.Round(maxRam.Value);
            if (wantGpu && maxGpu.HasValue) gpuTemp = (int)Math.Round(maxGpu.Value);

            return cpuTemp.HasValue || ramTemp.HasValue || gpuTemp.HasValue;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (pBase != IntPtr.Zero) UnmapViewOfFile(pBase);
            CloseHandle(hMap);
        }
    }

    private static bool TryReadCoreTemp(out int? cpuTemp)
    {
        cpuTemp = null;
        IntPtr hMap = OpenFileMapping(FILE_MAP_READ, false, "CoreTempMappingObjectEx");
        if (hMap == IntPtr.Zero)
        {
            hMap = OpenFileMapping(FILE_MAP_READ, false, "CoreTempMappingObject");
        }
        if (hMap == IntPtr.Zero) return false;

        IntPtr pBase = IntPtr.Zero;
        try
        {
            pBase = MapViewOfFile(hMap, FILE_MAP_READ, 0, 0, UIntPtr.Zero);
            if (pBase == IntPtr.Zero) return false;

            ulong regionSize = GetMappedRegionSize(pBase);
            if (regionSize < (ulong)Marshal.SizeOf<CoreTempSharedDataEx>()) return false;

            var data = Marshal.PtrToStructure<CoreTempSharedDataEx>(pBase);
            int totalCores = (int)Math.Clamp(data.uiCoreCnt * Math.Max(1u, data.uiCPUCnt), 1u, 256u);

            float? maxTempC = null;
            for (int i = 0; i < totalCores; i++)
            {
                float t = data.fTemp[i];
                if (float.IsNaN(t) || float.IsInfinity(t)) continue;

                if (data.ucDeltaToTjMax != 0)
                {
                    int cpuIdx = Math.Clamp(i / (int)Math.Max(1u, data.uiCoreCnt), 0, 127);
                    float tjMax = data.uiTjMax[cpuIdx];
                    if (tjMax > 0) t = tjMax - t;
                }

                if (data.ucFahrenheit != 0)
                {
                    t = (t - 32f) * 5f / 9f;
                }

                if (t >= 5f && t <= 130f)
                {
                    if (!maxTempC.HasValue || t > maxTempC.Value) maxTempC = t;
                }
            }

            if (maxTempC.HasValue)
            {
                cpuTemp = (int)Math.Round(maxTempC.Value);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (pBase != IntPtr.Zero) UnmapViewOfFile(pBase);
            CloseHandle(hMap);
        }
    }

    private static bool TryReadAida64(
        bool wantCpu,
        bool wantRam,
        bool wantGpu,
        out int? cpuTemp,
        out int? ramTemp,
        out int? gpuTemp)
    {
        cpuTemp = null;
        ramTemp = null;
        gpuTemp = null;

        IntPtr hMap = OpenFileMapping(FILE_MAP_READ, false, "AIDA64_SensorValues");
        if (hMap == IntPtr.Zero) return false;

        IntPtr pBase = IntPtr.Zero;
        try
        {
            pBase = MapViewOfFile(hMap, FILE_MAP_READ, 0, 0, UIntPtr.Zero);
            if (pBase == IntPtr.Zero) return false;

            ulong regionSize = GetMappedRegionSize(pBase);
            int maxRead = (int)Math.Min(regionSize, 65536UL);
            if (maxRead <= 0) return false;

            string rawXml = ReadNullTerminatedAnsi(pBase, maxRead);
            if (string.IsNullOrWhiteSpace(rawXml)) return false;

            var doc = XDocument.Parse($"<AIDA64>{rawXml}</AIDA64>");
            double? cVal = null, cPkg = null, rVal = null, gVal = null;

            foreach (var node in doc.Root?.Elements("temp") ?? Array.Empty<XElement>())
            {
                string id = node.Element("id")?.Value ?? "";
                string label = node.Element("label")?.Value ?? "";
                string valStr = node.Element("value")?.Value ?? "";

                if (!double.TryParse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
                    continue;

                if (val < 5.0 || val > 135.0) continue;

                if (wantCpu)
                {
                    if (id.Equals("TCPUPKG", StringComparison.OrdinalIgnoreCase) ||
                        label.Contains("CPU Package", StringComparison.OrdinalIgnoreCase))
                    {
                        cPkg = val;
                    }
                    else if (id.Equals("TCPU", StringComparison.OrdinalIgnoreCase) ||
                             id.Equals("TCPUDIO", StringComparison.OrdinalIgnoreCase) ||
                             label.Equals("CPU", StringComparison.OrdinalIgnoreCase) ||
                             label.Contains("CPU Diode", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!cVal.HasValue || val > cVal.Value) cVal = val;
                    }
                }

                if (wantRam && (id.StartsWith("TDIMM", StringComparison.OrdinalIgnoreCase) ||
                                label.Contains("DIMM", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!rVal.HasValue || val > rVal.Value) rVal = val;
                }

                if (wantGpu && (id.StartsWith("TGPU", StringComparison.OrdinalIgnoreCase) ||
                                label.Equals("GPU", StringComparison.OrdinalIgnoreCase) ||
                                label.Contains("GPU Diode", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!gVal.HasValue || val > gVal.Value) gVal = val;
                }
            }

            if (wantCpu && (cPkg ?? cVal).HasValue) cpuTemp = (int)Math.Round((cPkg ?? cVal)!.Value);
            if (wantRam && rVal.HasValue) ramTemp = (int)Math.Round(rVal.Value);
            if (wantGpu && gVal.HasValue) gpuTemp = (int)Math.Round(gVal.Value);

            return cpuTemp.HasValue || ramTemp.HasValue || gpuTemp.HasValue;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (pBase != IntPtr.Zero) UnmapViewOfFile(pBase);
            CloseHandle(hMap);
        }
    }

    private bool TryReadLhmWmi(
        bool wantCpu,
        bool wantRam,
        bool wantGpu,
        out int? cpuTemp,
        out int? ramTemp,
        out int? gpuTemp)
    {
        cpuTemp = null;
        ramTemp = null;
        gpuTemp = null;

        DateTime now = DateTime.UtcNow;
        if (_activeWmiNamespace == null && (now - _lastWmiCheckUtc).TotalSeconds < 20)
        {
            return false;
        }
        _lastWmiCheckUtc = now;

        string[] namespaces = _activeWmiNamespace != null
            ? new[] { _activeWmiNamespace }
            : new[] { @"root\LibreHardwareMonitor", @"root\OpenHardwareMonitor" };

        foreach (string ns in namespaces)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(ns, "SELECT Name, Identifier, Value FROM Sensor WHERE SensorType='Temperature'");
                using var results = searcher.Get();
                bool anyRow = false;

                double? cpuPkg = null, cpuCoreMax = null;
                double? maxRam = null;
                double? maxGpu = null;

                foreach (ManagementObject obj in results)
                {
                    anyRow = true;
                    string name = obj["Name"]?.ToString() ?? "";
                    string id = obj["Identifier"]?.ToString() ?? "";
                    if (obj["Value"] == null) continue;
                    double val = Convert.ToDouble(obj["Value"], CultureInfo.InvariantCulture);
                    if (val < 5.0 || val > 135.0) continue;

                    if (wantCpu && id.Contains("cpu", StringComparison.OrdinalIgnoreCase))
                    {
                        if (name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase)) continue;
                        if (name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Core Max", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Tctl/Tdie", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!cpuPkg.HasValue || val > cpuPkg.Value) cpuPkg = val;
                        }
                        else if (!cpuCoreMax.HasValue || val > cpuCoreMax.Value)
                        {
                            cpuCoreMax = val;
                        }
                    }
                    else if (wantRam && !IsStaticThermalMetadataName(name) &&
                                         (id.Contains("ram", StringComparison.OrdinalIgnoreCase) ||
                                          id.Contains("memory", StringComparison.OrdinalIgnoreCase) ||
                                          name.Contains("DIMM", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (!maxRam.HasValue || val > maxRam.Value) maxRam = val;
                    }
                    else if (wantGpu && id.Contains("gpu", StringComparison.OrdinalIgnoreCase))
                    {
                        if (name.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Edge", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("GPU", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!maxGpu.HasValue || val > maxGpu.Value) maxGpu = val;
                        }
                    }
                }

                if (anyRow)
                {
                    _activeWmiNamespace = ns;
                    if (wantCpu && (cpuPkg ?? cpuCoreMax).HasValue) cpuTemp = (int)Math.Round((cpuPkg ?? cpuCoreMax)!.Value);
                    if (wantRam && maxRam.HasValue) ramTemp = (int)Math.Round(maxRam.Value);
                    if (wantGpu && maxGpu.HasValue) gpuTemp = (int)Math.Round(maxGpu.Value);
                    return cpuTemp.HasValue || ramTemp.HasValue || gpuTemp.HasValue;
                }
            }
            catch
            {
                if (_activeWmiNamespace == ns) _activeWmiNamespace = null;
            }
        }

        return false;
    }

    private static string ReadNullTerminatedAnsi(IntPtr ptr, int maxBytes, byte[]? reuseBuffer = null)
    {
        int size = Math.Min(maxBytes, 65536);
        byte[] buffer = reuseBuffer != null && reuseBuffer.Length >= size ? reuseBuffer : new byte[size];
        Marshal.Copy(ptr, buffer, 0, size);
        int len = Array.IndexOf(buffer, (byte)0, 0, size);
        if (len < 0) len = size;
        return len == 0 ? "" : Encoding.Latin1.GetString(buffer, 0, len);
    }

    private void ReleaseLhmEngine()
    {
        if (_lhmComputer != null)
        {
            try { _lhmComputer.Close(); } catch { }
            _lhmComputer = null;
        }
        _lhmInitAttempted = false;
        _lhmMemoryEnabled = false;
        _cachedLhmRamTempC = null;
        _ramSampleCounter = 0;
    }

    public void ShutdownAll()
    {
        lock (_lock)
        {
            if (_disposed) return;

            ReleaseLhmEngine();

            if (_nvAvailable && _nvUnload != null)
            {
                try { _nvUnload(); } catch { }
                _nvAvailable = false;
                _nvChecked = false;
                _nvEnumGpus = null;
                _nvGetThermal = null;
                _nvUnload = null;
            }

            if (_adlAvailable && _adlContext != IntPtr.Zero)
            {
                try { ADL2_Main_Control_Destroy(_adlContext); } catch { }
                _adlContext = IntPtr.Zero;
                _adlAvailable = false;
                _adlChecked = false;
            }

            if (_d3dAdaptersPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_d3dAdaptersPtr); _d3dAdaptersPtr = IntPtr.Zero; _d3dAdaptersCap = 0; }
            if (_d3dTypePtr != IntPtr.Zero) { Marshal.FreeHGlobal(_d3dTypePtr); _d3dTypePtr = IntPtr.Zero; }
            if (_d3dPerfPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_d3dPerfPtr); _d3dPerfPtr = IntPtr.Zero; }
            if (_nvBufPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_nvBufPtr); _nvBufPtr = IntPtr.Zero; }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            ReleaseLhmEngine();

            if (_d3dAdaptersPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_d3dAdaptersPtr); _d3dAdaptersPtr = IntPtr.Zero; }
            if (_d3dTypePtr != IntPtr.Zero) { Marshal.FreeHGlobal(_d3dTypePtr); _d3dTypePtr = IntPtr.Zero; }
            if (_d3dPerfPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_d3dPerfPtr); _d3dPerfPtr = IntPtr.Zero; }
            if (_nvBufPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_nvBufPtr); _nvBufPtr = IntPtr.Zero; }

            if (_nvAvailable && _nvUnload != null)
            {
                try { _nvUnload(); } catch { }
                _nvAvailable = false;
            }

            if (_adlAvailable && _adlContext != IntPtr.Zero)
            {
                try { ADL2_Main_Control_Destroy(_adlContext); } catch { }
                _adlContext = IntPtr.Zero;
                _adlAvailable = false;
            }
        }
    }
}
