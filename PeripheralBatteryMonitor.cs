using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Timer = System.Threading.Timer;

namespace CleanTB;

public enum PeripheralType
{
    Headphones,
    Mouse,
    Keyboard,
    Controller,
    Other
}

public readonly record struct BatteryDeviceInfo(
    string Id,
    string Name,
    PeripheralType Type,
    int BatteryPercent,
    bool IsConnected
);

public class PeripheralBatteryMonitor : IDisposable
{
    // Win32 XInput for Xbox and compatible wireless controllers
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_BATTERY_INFORMATION
    {
        public byte BatteryType;
        public byte BatteryLevel;
    }

    private const byte BATTERY_DEVTYPE_GAMEPAD = 0x00;
    private const byte BATTERY_TYPE_DISCONNECTED = 0x00;
    private const byte BATTERY_TYPE_WIRED = 0x01;

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
    private static extern int XInputGetBatteryInformation14(int dwUserIndex, byte devType, ref XINPUT_BATTERY_INFORMATION pBatteryInformation);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetBatteryInformation")]
    private static extern int XInputGetBatteryInformation91(int dwUserIndex, byte devType, ref XINPUT_BATTERY_INFORMATION pBatteryInformation);

    // Win32 Bluetooth APIs for checking Bluetooth Classic connection status
    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_ADDRESS
    {
        public ulong ullLong;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BLUETOOTH_DEVICE_INFO
    {
        public uint dwSize;
        public BLUETOOTH_ADDRESS Address;
        public uint ulClassofDevice;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fConnected;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fRemembered;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAuthenticated;
        public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
        public ushort wYearUsed, wMonthUsed, wDayOfWeekUsed, wDayUsed, wHourUsed, wMinuteUsed, wSecondUsed, wMillisecondsUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string szName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_DEVICE_SEARCH_PARAMS
    {
        public uint dwSize;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fReturnAuthenticated;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fReturnRemembered;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fReturnUnknown;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fReturnConnected;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIssueInquiry;
        public byte cTimeoutMultiplier;
        public IntPtr hRadio;
    }

    [DllImport("bluetoothapis.dll", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS psp, ref BLUETOOTH_DEVICE_INFO pbdi);

    [DllImport("bluetoothapis.dll", SetLastError = true)]
    private static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO pbdi);

    [DllImport("bluetoothapis.dll", SetLastError = true)]
    private static extern bool BluetoothFindDeviceClose(IntPtr hFind);

    // Win32 SetupAPI for reading PnP device properties (the exact source Windows Settings uses)
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr ClassGuid, string? Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDevicePropertyW(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        ref DEVPROPKEY propertyKey,
        out uint propertyType,
        byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize,
        uint flags
    );

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        [Out] StringBuilder DeviceInstanceId,
        uint DeviceInstanceIdSize,
        out uint RequiredSize
    );

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    private static readonly DEVPROPKEY PKEY_Device_FriendlyName = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
    private static readonly DEVPROPKEY PKEY_Device_DeviceDesc = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 2 };
    private static readonly DEVPROPKEY PKEY_Device_BatteryLifePercent = new() { fmtid = new Guid("104ea319-6ee2-4701-bd47-8ddbf425bbe5"), pid = 2 };
    private static readonly DEVPROPKEY PKEY_Device_IsConnected = new() { fmtid = new Guid("83da6326-97a6-4088-9453-a1923f573b29"), pid = 15 };

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_ALLCLASSES = 0x00000004;

    private readonly AppConfig _config;
    private readonly Timer _pollTimer;
    private readonly Timer _debounceTimer;
    private readonly DongleBatteryMonitor _dongleMonitor = new();
    private readonly object _lock = new();
    private List<BatteryDeviceInfo> _devices = new();
    private bool _isDisposed;
    private int _pollingActive;

    public event Action<List<BatteryDeviceInfo>>? BatteryUpdated;

    public PeripheralBatteryMonitor(AppConfig config)
    {
        _config = config;

        // Synchronous initial poll so connected devices and battery levels
        // are cached BEFORE overlays initialize and query active devices
        PollNow();

        // Debounce timer: batches connection arrival/removal bursts into a single swift scan
        _debounceTimer = new Timer(_ =>
        {
            PollNow();
        }, null, Timeout.Infinite, Timeout.Infinite);

        // Active poll timer: runs every 3 seconds to catch any silent disconnects or percentage shifts
        _pollTimer = new Timer(OnPollTick, null, 3000, 3000);
    }

    public List<BatteryDeviceInfo> GetCurrentDevices()
    {
        lock (_lock)
        {
            if (_config.BatteryPreviewMode)
            {
                var previewList = new List<BatteryDeviceInfo>();
                if (_config.ShowBatteryHeadphones)
                    previewList.Add(new BatteryDeviceInfo("demo_hp", "Wireless Headset", PeripheralType.Headphones, 93, true));
                if (_config.ShowBatteryMouse)
                    previewList.Add(new BatteryDeviceInfo("demo_mouse", "Wireless Mouse", PeripheralType.Mouse, 45, true));
                if (_config.ShowBatteryKeyboard)
                    previewList.Add(new BatteryDeviceInfo("demo_kb", "Wireless Keyboard", PeripheralType.Keyboard, 82, true));
                if (_config.ShowBatteryController)
                    previewList.Add(new BatteryDeviceInfo("demo_ctrl", "Xbox Controller", PeripheralType.Controller, 70, true));
                if (_config.ShowBatteryOther)
                    previewList.Add(new BatteryDeviceInfo("demo_other", "Generic Device", PeripheralType.Other, 60, true));
                return previewList;
            }

            return new List<BatteryDeviceInfo>(_devices);
        }
    }

    public void ForceRefresh()
    {
        if (_isDisposed) return;
        try
        {
            _dongleMonitor.InvalidateCache();
            // Debounce for 200ms to allow Bluetooth/USB connection/disconnection handshake to settle
            _debounceTimer.Change(200, Timeout.Infinite);
        }
        catch { }
    }

    private void OnPollTick(object? state)
    {
        if (_isDisposed) return;
        PollNow();
    }

    private void PollNow()
    {
        if (_isDisposed) return;
        if (Interlocked.Exchange(ref _pollingActive, 1) != 0)
            return;

        try
        {
            var found = new List<BatteryDeviceInfo>();

            // 1. Query XInput Wireless Controllers (Xbox 360, One, Series, etc.)
            PollXInputControllers(found);

            // 2. Query SetupAPI PnP Devices with strict connected validation
            // Instantly captures all Bluetooth headphones, headsets (HFP / hands-free), Bluetooth keyboards, mice, earbuds, styluses, etc.
            PollSetupDiDevices(found);

            // 3. Query 2.4 GHz USB Dongles & HID Wireless Receivers (Logitech HID++, Razer HyperSpeed, Sony DualSense/DS4, SteelSeries, Corsair, HyperX, HID 0x85)
            PollDongleDevices(found);

            bool changed;
            lock (_lock)
            {
                changed = !DevicesEqual(_devices, found);
                _devices = found;
            }

            if (changed)
            {
                BatteryUpdated?.Invoke(GetCurrentDevices());
            }
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _pollingActive, 0);
        }
    }

    private void PollDongleDevices(List<BatteryDeviceInfo> list)
    {
        foreach (var dongleDev in _dongleMonitor.PollHidDongles())
        {
            int existingIdx = list.FindIndex(d =>
                string.Equals(d.Id, dongleDev.Id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Name, dongleDev.Name, StringComparison.OrdinalIgnoreCase) ||
                (dongleDev.Type == PeripheralType.Controller && d.Type == PeripheralType.Controller &&
                 ((dongleDev.Name.Contains("DualSense", StringComparison.OrdinalIgnoreCase) && d.Name.Contains("DualSense", StringComparison.OrdinalIgnoreCase)) ||
                  (dongleDev.Name.Contains("DualShock", StringComparison.OrdinalIgnoreCase) && d.Name.Contains("DualShock", StringComparison.OrdinalIgnoreCase)))));

            // Replace dummy 100% wired XInput fallback when GameSir / FlyDigi / BEITONG HID gives true wireless %
            if (existingIdx < 0 && dongleDev.Type == PeripheralType.Controller &&
                (dongleDev.Id.StartsWith("GAMESIR_", StringComparison.OrdinalIgnoreCase) ||
                 dongleDev.Id.StartsWith("FLYDIGI_", StringComparison.OrdinalIgnoreCase) ||
                 dongleDev.Id.StartsWith("BEKEN_", StringComparison.OrdinalIgnoreCase) ||
                 dongleDev.Id.StartsWith("BEITONG_", StringComparison.OrdinalIgnoreCase)))
            {
                int xinputCount = list.Count(d => d.Id.StartsWith("xinput_", StringComparison.OrdinalIgnoreCase));
                if (xinputCount == 1)
                    existingIdx = list.FindIndex(d => d.Id.StartsWith("xinput_", StringComparison.OrdinalIgnoreCase));
            }

            if (existingIdx >= 0)
            {
                if (list[existingIdx].BatteryPercent <= 0 ||
                    list[existingIdx].Id.StartsWith("xinput_", StringComparison.OrdinalIgnoreCase))
                {
                    list[existingIdx] = dongleDev;
                }
            }
            else
            {
                list.Add(dongleDev);
            }
        }
    }

    private static bool DevicesEqual(List<BatteryDeviceInfo> a, List<BatteryDeviceInfo> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Id != b[i].Id ||
                a[i].BatteryPercent != b[i].BatteryPercent ||
                a[i].IsConnected != b[i].IsConnected ||
                a[i].Type != b[i].Type)
            {
                return false;
            }
        }
        return true;
    }

    private void PollXInputControllers(List<BatteryDeviceInfo> list)
    {
        for (int i = 0; i < 4; i++)
        {
            try
            {
                var info = new XINPUT_BATTERY_INFORMATION();
                int res = -1;
                try
                {
                    res = XInputGetBatteryInformation14(i, BATTERY_DEVTYPE_GAMEPAD, ref info);
                }
                catch
                {
                    try
                    {
                        res = XInputGetBatteryInformation91(i, BATTERY_DEVTYPE_GAMEPAD, ref info);
                    }
                    catch { }
                }

                if (res == 0 && info.BatteryType != BATTERY_TYPE_DISCONNECTED)
                {
                    int pct;
                    if (info.BatteryType == BATTERY_TYPE_WIRED)
                    {
                        pct = 100;
                    }
                    else
                    {
                        pct = info.BatteryLevel switch
                        {
                            0 => 15,
                            1 => 40,
                            2 => 70,
                            3 => 100,
                            _ => 50
                        };
                    }

                    list.Add(new BatteryDeviceInfo(
                        $"xinput_{i}",
                        $"Xbox Controller {i + 1}",
                        PeripheralType.Controller,
                        pct,
                        true
                    ));
                }
            }
            catch { }
        }
    }

    private static HashSet<string> GetConnectedClassicBluetoothMacs()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var search = new BLUETOOTH_DEVICE_SEARCH_PARAMS();
            search.dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_SEARCH_PARAMS));
            search.fReturnRemembered = true;
            search.fReturnAuthenticated = true;
            search.fReturnConnected = true;

            var info = new BLUETOOTH_DEVICE_INFO();
            info.dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_INFO));

            IntPtr hFind = BluetoothFindFirstDevice(ref search, ref info);
            if (hFind != IntPtr.Zero)
            {
                try
                {
                    do
                    {
                        if (info.fConnected)
                        {
                            string mac = info.Address.ullLong.ToString("X12");
                            set.Add(mac);
                        }
                        info = new BLUETOOTH_DEVICE_INFO();
                        info.dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_INFO));
                    } while (BluetoothFindNextDevice(hFind, ref info));
                }
                finally
                {
                    BluetoothFindDeviceClose(hFind);
                }
            }
        }
        catch { }
        return set;
    }

    private static string ExtractMacFromInstanceId(string instanceId)
    {
        var m = Regex.Match(instanceId, @"(?:DEV_|&)([0-9a-fA-F]{12})(?:\\|_|$)");
        if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
        var mAny = Regex.Match(instanceId, @"([0-9a-fA-F]{12})");
        if (mAny.Success) return mAny.Groups[1].Value.ToUpperInvariant();
        return string.Empty;
    }

    private void PollSetupDiDevices(List<BatteryDeviceInfo> list)
    {
        var classicConnectedMacs = GetConnectedClassicBluetoothMacs();

        IntPtr hDevInfo = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (hDevInfo == IntPtr.Zero || hDevInfo.ToInt64() == -1)
            return;

        try
        {
            var devData = new SP_DEVINFO_DATA();
            devData.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
            uint index = 0;

            while (SetupDiEnumDeviceInfo(hDevInfo, index++, ref devData))
            {
                var batKey = PKEY_Device_BatteryLifePercent;
                if (!SetupDiGetDevicePropertyW(hDevInfo, ref devData, ref batKey, out uint propType, null, 0, out uint reqSize, 0))
                {
                    if (Marshal.GetLastWin32Error() == 122 && reqSize > 0)
                    {
                        byte[] buf = new byte[reqSize];
                        if (SetupDiGetDevicePropertyW(hDevInfo, ref devData, ref batKey, out propType, buf, reqSize, out reqSize, 0))
                        {
                            int battery = buf.Length > 0 ? (int)buf[0] : -1;
                            if (battery < 0 || battery > 100) continue;

                            var sbId = new StringBuilder(512);
                            SetupDiGetDeviceInstanceIdW(hDevInfo, ref devData, sbId, (uint)sbId.Capacity, out uint idReq);
                            string instanceId = sbId.ToString();

                            bool isBle = instanceId.StartsWith("BTHLE", StringComparison.OrdinalIgnoreCase);
                            bool isBthEnum = instanceId.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase);

                            // CRITICAL CONNECTION CHECK:
                            // 1. For BLE devices (e.g. Bluetooth Keyboard): query DEVPKEY_Device_IsConnected property.
                            // When disconnected, Windows sets this property to False while leaving cached battery at 100%.
                            if (isBle)
                            {
                                var connKey = PKEY_Device_IsConnected;
                                byte[] connBuf = new byte[4];
                                bool isConnected = false;
                                if (SetupDiGetDevicePropertyW(hDevInfo, ref devData, ref connKey, out uint connType, connBuf, (uint)connBuf.Length, out uint connReq, 0))
                                {
                                    isConnected = (connBuf.Length > 0 && connBuf[0] != 0);
                                }
                                if (!isConnected)
                                {
                                    continue; // Skip disconnected BLE device!
                                }
                            }
                            // 2. For Bluetooth Classic devices (e.g. G435 Headset): verify device MAC is actively connected.
                            else if (isBthEnum)
                            {
                                string mac = ExtractMacFromInstanceId(instanceId);
                                if (!string.IsNullOrEmpty(mac) && !classicConnectedMacs.Contains(mac))
                                {
                                    continue; // Skip disconnected Bluetooth Classic device!
                                }
                            }

                            string name = GetSetupDiString(hDevInfo, ref devData, PKEY_Device_FriendlyName);
                            if (string.IsNullOrEmpty(name))
                                name = GetSetupDiString(hDevInfo, ref devData, PKEY_Device_DeviceDesc);
                            if (string.IsNullOrEmpty(name))
                                name = "Wireless Device";

                            string cleanName = CleanDeviceName(name);
                            var type = ClassifyDevice(cleanName);
                            string devMac = ExtractMacFromInstanceId(instanceId);

                            // Deduplicate: check if this device is already in the list
                            int existingIdx = list.FindIndex(d =>
                                (!string.IsNullOrEmpty(devMac) && d.Id.Contains(devMac, StringComparison.OrdinalIgnoreCase)) ||
                                string.Equals(d.Name, cleanName, StringComparison.OrdinalIgnoreCase)
                            );

                            if (existingIdx >= 0)
                            {
                                if (list[existingIdx].BatteryPercent < 0 && battery >= 0)
                                {
                                    list[existingIdx] = new BatteryDeviceInfo(
                                        instanceId,
                                        cleanName,
                                        type,
                                        battery,
                                        true
                                    );
                                }
                            }
                            else
                            {
                                list.Add(new BatteryDeviceInfo(
                                    instanceId,
                                    cleanName,
                                    type,
                                    battery,
                                    true
                                ));
                            }
                        }
                    }
                }
            }
        }
        catch { }
        finally
        {
            SetupDiDestroyDeviceInfoList(hDevInfo);
        }
    }

    private static string GetSetupDiString(IntPtr hDevInfo, ref SP_DEVINFO_DATA devData, DEVPROPKEY key)
    {
        if (!SetupDiGetDevicePropertyW(hDevInfo, ref devData, ref key, out uint propType, null, 0, out uint reqSize, 0))
        {
            if (Marshal.GetLastWin32Error() == 122 && reqSize > 0)
            {
                byte[] buf = new byte[reqSize];
                if (SetupDiGetDevicePropertyW(hDevInfo, ref devData, ref key, out propType, buf, reqSize, out reqSize, 0))
                {
                    return Encoding.Unicode.GetString(buf).TrimEnd('\0');
                }
            }
        }
        return string.Empty;
    }

    public static string CleanDeviceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Wireless Device";
        string clean = Regex.Replace(name, @"\s+(Hands-Free\s*AG|Hands-Free|Avrcp\s*Transport|Audio)$", "", RegexOptions.IgnoreCase).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "Wireless Device" : clean;
    }

    public static PeripheralType ClassifyDevice(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return PeripheralType.Other;

        string lower = name.ToLowerInvariant();

        // Mouse detection
        if (lower.Contains("mouse") || lower.Contains("viper") || lower.Contains("deathadder") ||
            lower.Contains("basilisk") || lower.Contains("g304") || lower.Contains("g502") ||
            lower.Contains("g pro") || lower.Contains("g305") || lower.Contains("g703") ||
            lower.Contains("g903") || lower.Contains("superlight") || lower.Contains("mx master") ||
            lower.Contains("mx anywhere") || lower.Contains("trackball") || lower.Contains("touchpad") ||
            lower.Contains("pulsar") || lower.Contains("aerox") || lower.Contains("scimitar") ||
            lower.Contains("gladius") || lower.Contains("orochi") || lower.Contains("katar") ||
            lower.Contains("harpoon") || lower.Contains("dark core") || lower.Contains("ironclaw") ||
            lower.Contains("mamba") || lower.Contains("rival") || lower.Contains("prime") ||
            lower.Contains("naga") || lower.Contains("cobra") || lower.Contains("pro click") ||
            lower.Contains("gladius") || lower.Contains("harpe") || lower.Contains("keris") ||
            lower.Contains("chakram") || lower.Contains("spatha") || lower.Contains("pugio") ||
            lower.Contains("lamzu") || lower.Contains("vaxee") || lower.Contains("finalmouse") ||
            lower.Contains("ultralightx") || lower.Contains("dareu") || lower.Contains("ajazz") ||
            lower.Contains("mchose") || lower.Contains("inphic") || lower.Contains("hitscan") ||
            lower.Contains("rapoo") || lower.Contains("attack shark"))
        {
            return PeripheralType.Mouse;
        }

        // Keyboard detection
        if (lower.Contains("keyboard") || lower.Contains("keychron") || lower.Contains("k380") ||
            lower.Contains("k8") || lower.Contains("k2") || lower.Contains("blackwidow") ||
            lower.Contains("huntsman") || lower.Contains("g915") || lower.Contains("g815") ||
            lower.Contains("mx keys") || lower.Contains("apex") || lower.Contains("ducky") ||
            lower.Contains("filco") || lower.Contains("k70") || lower.Contains("k65") ||
            lower.Contains("k100") || lower.Contains("deathstalker") || lower.Contains("anne pro") ||
            lower.Contains("rk61") || lower.Contains("rk84") || lower.Contains("nuphy") ||
            lower.Contains("epomaker") || lower.Contains("keypad") ||
            lower.Contains("azoth") || lower.Contains("falchion") || lower.Contains("scope ii") ||
            lower.Contains("morph 96") || lower.Contains("aula"))
        {
            return PeripheralType.Keyboard;
        }

        // Headphone / Headset / Earbuds detection
        if (lower.Contains("headset") || lower.Contains("headphone") || lower.Contains("earphone") ||
            lower.Contains("buds") || lower.Contains("airpod") || lower.Contains("wh-1000") ||
            lower.Contains("wf-1000") || lower.Contains("soundcore") || lower.Contains("g435") ||
            lower.Contains("arctis") || lower.Contains("nova") || lower.Contains("audio") || lower.Contains("speaker") ||
            lower.Contains("soundbar") || lower.Contains("freebuds") || lower.Contains("shokz") ||
            lower.Contains("airopes") || lower.Contains("airdopes") || lower.Contains("luna") ||
            lower.Contains("astra") || lower.Contains("earbuds") || lower.Contains("qc35") ||
            lower.Contains("qc45") || lower.Contains("bose") || lower.Contains("jbl") ||
            lower.Contains("sennheiser") || lower.Contains("beats") || lower.Contains("hyperx") ||
            lower.Contains("cloud") || lower.Contains("kraken") || lower.Contains("blackshark") ||
            lower.Contains("barracuda") || lower.Contains("virtuoso") || lower.Contains("void") ||
            lower.Contains("hs70") || lower.Contains("hs80") || lower.Contains("quantum") ||
            lower.Contains("wh-") || lower.Contains("wf-") ||
            lower.Contains("stealth") || lower.Contains("atlas air") || lower.Contains("strix go") ||
            lower.Contains("cetra") || lower.Contains("delta ii") || lower.Contains("epos") ||
            lower.Contains("h3pro") || lower.Contains("maxwell") || lower.Contains("audeze") ||
            lower.Contains("inzone") || lower.Contains("plyr") || lower.Contains("astro a50") ||
            lower.Contains("g pro x") || lower.Contains("g533") || lower.Contains("g535") ||
            lower.Contains("g633") || lower.Contains("g733") || lower.Contains("g933"))
        {
            return PeripheralType.Headphones;
        }

        // Controller detection
        if (lower.Contains("controller") || lower.Contains("gamepad") || lower.Contains("xbox") ||
            lower.Contains("dualsense") || lower.Contains("dualshock") || lower.Contains("joy-con") ||
            lower.Contains("switch pro") || lower.Contains("stadia") || lower.Contains("flydigi") ||
            lower.Contains("gamesir") || lower.Contains("8bitdo") || lower.Contains("gulikit") ||
            lower.Contains("wolverine") || lower.Contains("scuf") || lower.Contains("beitong") ||
            lower.Contains("kp40") || lower.Contains("vader"))
        {
            return PeripheralType.Controller;
        }

        return PeripheralType.Other;
    }

    public void Dispose()
    {
        _isDisposed = true;
        _pollTimer.Dispose();
        _debounceTimer.Dispose();
        _dongleMonitor.Dispose();
    }
}
