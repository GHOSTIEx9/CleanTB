using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Timer = System.Threading.Timer;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Storage.Streams;

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

    // Win32 SetupAPI for reading PnP device properties (same source Windows Settings uses for DEVPKEY_Device_BatteryLifePercent)
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
        char[] DeviceInstanceId,
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

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_ALLCLASSES = 0x00000004;

    private readonly AppConfig _config;
    private readonly System.Threading.Timer _pollTimer;
    private readonly System.Threading.Timer _debounceTimer;
    private readonly object _lock = new();
    private List<BatteryDeviceInfo> _devices = new();
    private bool _isDisposed;
    private int _pollingActive;

    private DeviceWatcher? _bthWatcher;
    private DeviceWatcher? _bleWatcher;

    public event Action<List<BatteryDeviceInfo>>? BatteryUpdated;

    public PeripheralBatteryMonitor(AppConfig config)
    {
        _config = config;

        // Debounce timer: batches connection arrival/removal bursts into a single swift scan
        _debounceTimer = new Timer(_ =>
        {
            _ = PollAsync();
        }, null, Timeout.Infinite, Timeout.Infinite);

        // Active poll timer: runs every 3 seconds to catch any silent disconnects or percentage shifts
        _pollTimer = new Timer(OnPollTick, null, 500, 3000);

        // Real-time Windows DeviceWatchers for immediate Bluetooth & BLE connection events
        StartDeviceWatchers();
    }

    private void StartDeviceWatchers()
    {
        try
        {
            string bthAqs = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            _bthWatcher = DeviceInformation.CreateWatcher(bthAqs, new[] {
                "System.ItemNameDisplay",
                "System.Devices.Aep.IsConnected"
            });
            _bthWatcher.Added += (w, info) => ForceRefresh();
            _bthWatcher.Updated += (w, update) => ForceRefresh();
            _bthWatcher.Removed += (w, update) => ForceRefresh();
            _bthWatcher.Start();
        }
        catch { }

        try
        {
            string bleAqs = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
            _bleWatcher = DeviceInformation.CreateWatcher(bleAqs, new[] {
                "System.ItemNameDisplay",
                "System.Devices.Aep.IsConnected"
            });
            _bleWatcher.Added += (w, info) => ForceRefresh();
            _bleWatcher.Updated += (w, update) => ForceRefresh();
            _bleWatcher.Removed += (w, update) => ForceRefresh();
            _bleWatcher.Start();
        }
        catch { }
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
            // Debounce for 350ms to allow Bluetooth connection/disconnection handshake to settle
            _debounceTimer.Change(350, Timeout.Infinite);
        }
        catch { }
    }

    private void OnPollTick(object? state)
    {
        if (_isDisposed) return;
        _ = PollAsync();
    }

    private static async Task<T?> RunWithTimeout<T>(IAsyncOperation<T> op, int timeoutMs = 1500)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            return await op.AsTask(cts.Token);
        }
        catch
        {
            return default;
        }
    }

    private async Task PollAsync()
    {
        if (Interlocked.Exchange(ref _pollingActive, 1) != 0)
            return;

        try
        {
            var found = new List<BatteryDeviceInfo>();

            // 1. Query XInput Wireless Controllers (Xbox 360, One, Series, etc.)
            PollXInputControllers(found);

            // 2. Query SetupAPI PnP Devices (this is the EXACT source Windows Settings uses for DEVPKEY_Device_BatteryLifePercent)
            // Instantly captures all Bluetooth headphones, headsets (HFP / hands-free), Bluetooth keyboards, mice, earbuds, styluses, etc.
            PollSetupDiDevices(found);

            // 3. Query Windows Runtime Bluetooth Low Energy Devices (GATT Battery Service 0x180F) for low-energy peripherals
            try
            {
                await PollBleDevicesAsync(found);
            }
            catch { }

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

    private void PollSetupDiDevices(List<BatteryDeviceInfo> list)
    {
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

                            string name = GetSetupDiString(hDevInfo, ref devData, PKEY_Device_FriendlyName);
                            if (string.IsNullOrEmpty(name))
                                name = GetSetupDiString(hDevInfo, ref devData, PKEY_Device_DeviceDesc);
                            if (string.IsNullOrEmpty(name))
                                name = "Wireless Device";

                            string instanceId = GetSetupDiInstanceId(hDevInfo, ref devData);
                            string cleanName = CleanDeviceName(name);
                            var type = ClassifyDevice(cleanName);

                            string mac = "";
                            var m = Regex.Match(instanceId, @"([0-9a-fA-F]{12})");
                            if (m.Success)
                            {
                                mac = m.Groups[1].Value.ToUpperInvariant();
                            }

                            // Deduplicate: check if this device is already in the list
                            int existingIdx = list.FindIndex(d =>
                                (!string.IsNullOrEmpty(mac) && d.Id.Contains(mac, StringComparison.OrdinalIgnoreCase)) ||
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

    private async Task PollBleDevicesAsync(List<BatteryDeviceInfo> list)
    {
        string aqs = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
        var devicesOp = DeviceInformation.FindAllAsync(aqs, new[] {
            "System.ItemNameDisplay",
            "System.Devices.Aep.IsConnected"
        });
        var devices = await RunWithTimeout(devicesOp, 2000);
        if (devices == null) return;

        foreach (var devInfo in devices)
        {
            try
            {
                bool isConnected = false;
                if (devInfo.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var connVal) && connVal is bool bConn)
                {
                    isConnected = bConn;
                }

                var bleOp = BluetoothLEDevice.FromIdAsync(devInfo.Id);
                using var ble = await RunWithTimeout(bleOp, 1500);
                if (ble == null) continue;

                if (ble.ConnectionStatus == BluetoothConnectionStatus.Connected)
                {
                    isConnected = true;
                }

                // CRITICAL: If the device is not connected right now, ignore it!
                if (!isConnected || ble.ConnectionStatus != BluetoothConnectionStatus.Connected)
                {
                    continue;
                }

                // Query GATT Battery Service
                var sResult = await RunWithTimeout(ble.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached), 1500);
                if (sResult == null || sResult.Status != GattCommunicationStatus.Success || sResult.Services.Count == 0)
                {
                    sResult = await RunWithTimeout(ble.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached), 1000);
                }

                if (sResult != null && sResult.Status == GattCommunicationStatus.Success && sResult.Services.Count > 0)
                {
                    foreach (var service in sResult.Services)
                    {
                        using (service)
                        {
                            var cResult = await RunWithTimeout(service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel), 1000);
                            if (cResult != null && cResult.Status == GattCommunicationStatus.Success && cResult.Characteristics.Count > 0)
                            {
                                var characteristic = cResult.Characteristics[0];
                                var rResult = await RunWithTimeout(characteristic.ReadValueAsync(BluetoothCacheMode.Uncached), 1000);
                                if (rResult == null || rResult.Status != GattCommunicationStatus.Success)
                                {
                                    rResult = await RunWithTimeout(characteristic.ReadValueAsync(BluetoothCacheMode.Cached), 500);
                                }

                                if (rResult != null && rResult.Status == GattCommunicationStatus.Success && rResult.Value != null)
                                {
                                    using var reader = DataReader.FromBuffer(rResult.Value);
                                    byte level = reader.ReadByte();
                                    if (level <= 100)
                                    {
                                        string rawName = string.IsNullOrWhiteSpace(ble.Name) ? devInfo.Name : ble.Name;
                                        string cleanName = CleanDeviceName(rawName);
                                        var type = ClassifyDevice(cleanName);

                                        string mac = "";
                                        var m = Regex.Match(ble.DeviceId, @"([0-9a-fA-F]{12})");
                                        if (m.Success)
                                        {
                                            mac = m.Groups[1].Value.ToUpperInvariant();
                                        }

                                        int existingIdx = list.FindIndex(d =>
                                            (!string.IsNullOrEmpty(mac) && d.Id.Contains(mac, StringComparison.OrdinalIgnoreCase)) ||
                                            string.Equals(d.Name, cleanName, StringComparison.OrdinalIgnoreCase)
                                        );

                                        if (existingIdx >= 0)
                                        {
                                            if (list[existingIdx].BatteryPercent < 0 && level >= 0)
                                            {
                                                list[existingIdx] = new BatteryDeviceInfo(
                                                    ble.DeviceId,
                                                    cleanName,
                                                    type,
                                                    level,
                                                    true
                                                );
                                            }
                                        }
                                        else
                                        {
                                            list.Add(new BatteryDeviceInfo(
                                                ble.DeviceId,
                                                cleanName,
                                                type,
                                                level,
                                                true
                                            ));
                                        }
                                        break; // Captured battery for this device
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
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
                    return System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0');
                }
            }
        }
        return string.Empty;
    }

    private static string GetSetupDiInstanceId(IntPtr hDevInfo, ref SP_DEVINFO_DATA devData)
    {
        char[] buf = new char[512];
        if (SetupDiGetDeviceInstanceIdW(hDevInfo, ref devData, buf, (uint)buf.Length, out uint reqSize) && reqSize > 1)
        {
            return new string(buf, 0, (int)reqSize - 1);
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
            lower.Contains("mamba") || lower.Contains("rival") || lower.Contains("prime"))
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
            lower.Contains("epomaker") || lower.Contains("keypad"))
        {
            return PeripheralType.Keyboard;
        }

        // Headphone / Headset / Earbuds detection
        if (lower.Contains("headset") || lower.Contains("headphone") || lower.Contains("earphone") ||
            lower.Contains("buds") || lower.Contains("airpod") || lower.Contains("wh-1000") ||
            lower.Contains("wf-1000") || lower.Contains("soundcore") || lower.Contains("g435") ||
            lower.Contains("arctis") || lower.Contains("audio") || lower.Contains("speaker") ||
            lower.Contains("soundbar") || lower.Contains("freebuds") || lower.Contains("shokz") ||
            lower.Contains("airopes") || lower.Contains("airdopes") || lower.Contains("luna") ||
            lower.Contains("astra") || lower.Contains("earbuds") || lower.Contains("qc35") ||
            lower.Contains("qc45") || lower.Contains("bose") || lower.Contains("jbl") ||
            lower.Contains("sennheiser") || lower.Contains("beats") || lower.Contains("hyperx") ||
            lower.Contains("cloud") || lower.Contains("kraken") || lower.Contains("blackshark") ||
            lower.Contains("barracuda") || lower.Contains("virtuoso") || lower.Contains("void") ||
            lower.Contains("hs70") || lower.Contains("hs80") || lower.Contains("quantum") ||
            lower.Contains("wh-") || lower.Contains("wf-"))
        {
            return PeripheralType.Headphones;
        }

        // Controller detection
        if (lower.Contains("controller") || lower.Contains("gamepad") || lower.Contains("xbox") ||
            lower.Contains("dualsense") || lower.Contains("dualshock") || lower.Contains("joy-con") ||
            lower.Contains("switch pro") || lower.Contains("stadia") || lower.Contains("flydigi") ||
            lower.Contains("gamesir") || lower.Contains("8bitdo") || lower.Contains("gulikit") ||
            lower.Contains("wolverine") || lower.Contains("scuf"))
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

        if (_bthWatcher != null)
        {
            try
            {
                if (_bthWatcher.Status == DeviceWatcherStatus.Started || _bthWatcher.Status == DeviceWatcherStatus.EnumerationCompleted)
                {
                    _bthWatcher.Stop();
                }
            }
            catch { }
        }

        if (_bleWatcher != null)
        {
            try
            {
                if (_bleWatcher.Status == DeviceWatcherStatus.Started || _bleWatcher.Status == DeviceWatcherStatus.EnumerationCompleted)
                {
                    _bleWatcher.Stop();
                }
            }
            catch { }
        }
    }
}
