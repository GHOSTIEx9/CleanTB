using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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
    private const byte BATTERY_TYPE_ALKALINE = 0x02;
    private const byte BATTERY_TYPE_NIMH = 0x03;
    private const byte BATTERY_TYPE_UNKNOWN = 0xFF;

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
    private static extern int XInputGetBatteryInformation14(int dwUserIndex, byte devType, ref XINPUT_BATTERY_INFORMATION pBatteryInformation);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetBatteryInformation")]
    private static extern int XInputGetBatteryInformation91(int dwUserIndex, byte devType, ref XINPUT_BATTERY_INFORMATION pBatteryInformation);

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
                "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2",
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

            // 2. Query Windows Runtime Bluetooth Low Energy Devices (GATT Battery Service 0x180F)
            try
            {
                await PollBleDevicesAsync(found);
            }
            catch { }

            // 3. Query Windows Runtime Bluetooth Classic Devices (BTH Audio, Headsets, Mice, Keyboards)
            try
            {
                await PollClassicBluetoothDevicesAsync(found);
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
                                        string name = string.IsNullOrWhiteSpace(ble.Name) ? devInfo.Name : ble.Name;
                                        var type = ClassifyDevice(name);

                                        list.Add(new BatteryDeviceInfo(
                                            ble.DeviceId,
                                            name,
                                            type,
                                            level,
                                            true
                                        ));
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

    private async Task PollClassicBluetoothDevicesAsync(List<BatteryDeviceInfo> list)
    {
        string aqs = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        var devicesOp = DeviceInformation.FindAllAsync(aqs, new[] {
            "System.ItemNameDisplay",
            "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2",
            "System.Devices.Aep.IsConnected"
        });
        var devices = await RunWithTimeout(devicesOp, 2000);
        if (devices == null) return;

        foreach (var devInfo in devices)
        {
            try
            {
                var bthOp = BluetoothDevice.FromIdAsync(devInfo.Id);
                using var bth = await RunWithTimeout(bthOp, 1500);
                if (bth == null) continue;

                bool isConnected = (bth.ConnectionStatus == BluetoothConnectionStatus.Connected);
                if (!isConnected && devInfo.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var connVal) && connVal is bool bConn)
                {
                    isConnected = bConn;
                }

                // CRITICAL: If the device is not connected right now, ignore it!
                if (!isConnected)
                {
                    continue;
                }

                string name = string.IsNullOrWhiteSpace(bth.Name) ? devInfo.Name : bth.Name;
                var type = ClassifyDevice(name);

                // If classified as Other, check Bluetooth ClassOfDevice
                if (type == PeripheralType.Other)
                {
                    if (bth.ClassOfDevice.MajorClass == BluetoothMajorClass.AudioVideo)
                    {
                        type = PeripheralType.Headphones;
                    }
                    else if (bth.ClassOfDevice.MajorClass == BluetoothMajorClass.Peripheral)
                    {
                        uint minor = (uint)bth.ClassOfDevice.MinorClass;
                        if ((minor & 0x80) != 0) type = PeripheralType.Mouse;
                        else if ((minor & 0x40) != 0) type = PeripheralType.Keyboard;
                    }
                }

                int batteryPercent = -1;
                if (devInfo.Properties.TryGetValue("{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2", out var batVal) && batVal != null)
                {
                    if (batVal is byte b) batteryPercent = b;
                    else if (batVal is int i) batteryPercent = i;
                    else if (batVal is uint u) batteryPercent = (int)u;
                }

                // Avoid duplicate if already reported by BLE
                int existingIdx = list.FindIndex(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (existingIdx >= 0)
                {
                    if (batteryPercent >= 0 && list[existingIdx].BatteryPercent < 0)
                    {
                        list[existingIdx] = new BatteryDeviceInfo(
                            bth.DeviceId,
                            name,
                            type,
                            batteryPercent,
                            true
                        );
                    }
                }
                else
                {
                    list.Add(new BatteryDeviceInfo(
                        bth.DeviceId,
                        name,
                        type,
                        batteryPercent,
                        true
                    ));
                }
            }
            catch { }
        }
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
            lower.Contains("epomaker"))
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
            lower.Contains("hs70") || lower.Contains("hs80") || lower.Contains("quantum"))
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
