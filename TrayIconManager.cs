using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CleanTB;

public class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly StickyContextMenuStrip _contextMenu;
    private readonly TaskbarController _controller;
    private readonly AppConfig _config;
    private readonly TaskbarMessageListener _listener;
    private readonly HardwareMonitor _hardwareMonitor;
    private readonly PeripheralBatteryMonitor _batteryMonitor;
    private readonly TaskbarStatsOverlay _statsOverlay;
    private readonly TaskbarBatteryOverlay _batteryOverlay;

    // Menu items - Appearance
    private ToolStripMenuItem _itemHeader = null!;
    private ToolStripMenuItem _itemClear = null!;
    private ToolStripMenuItem _itemBlur = null!;
    private ToolStripMenuItem _itemAcrylic = null!;
    private ToolStripMenuItem _itemTintDark = null!;
    private ToolStripMenuItem _itemTintLight = null!;
    private ToolStripMenuItem _itemNormal = null!;
    private ToolStripMenuItem _itemHideBorder = null!;
    private ToolStripMenuItem _itemStartup = null!;

    // Menu items - Hardware Stats
    private ToolStripMenuItem _menuHardwareStats = null!;
    private ToolStripMenuItem _itemStatsEnable = null!;
    private ToolStripMenuItem _itemStatsPosLeft = null!;
    private ToolStripMenuItem _itemStatsPosRight = null!;
    private ToolStripMenuItem _itemStatsDownload = null!;
    private ToolStripMenuItem _itemStatsUpload = null!;
    private ToolStripMenuItem _itemStatsNetArrows = null!;
    private ToolStripMenuItem _itemStatsCpu = null!;
    private ToolStripMenuItem _itemStatsRam = null!;
    private ToolStripMenuItem _itemStatsGpu = null!;
    private ToolStripMenuItem _menuStatsTemps = null!;
    private ToolStripMenuItem _itemTempCpu = null!;
    private ToolStripMenuItem _itemTempRam = null!;
    private ToolStripMenuItem _itemTempGpu = null!;
    private ToolStripMenuItem _itemTempAccents = null!;
    private ToolStripMenuItem _itemTempCelsius = null!;
    private ToolStripMenuItem _itemTempFahrenheit = null!;
    private ToolStripMenuItem _itemTempStatus = null!;
    private ToolStripMenuItem _itemStatsClickThrough = null!;
    private ToolStripMenuItem _itemStatsResetPos = null!;
    private bool _cachedScheduledTaskEnabled;

    // Menu items - Peripheral Battery Stats
    private ToolStripMenuItem _menuBatteryStats = null!;
    private ToolStripMenuItem _itemBatteryEnable = null!;
    private ToolStripMenuItem _itemBatteryPosLeft = null!;
    private ToolStripMenuItem _itemBatteryPosRight = null!;
    private ToolStripMenuItem _itemBatteryHeadphones = null!;
    private ToolStripMenuItem _itemBatteryMouse = null!;
    private ToolStripMenuItem _itemBatteryKeyboard = null!;
    private ToolStripMenuItem _itemBatteryController = null!;
    private ToolStripMenuItem _itemBatteryOther = null!;
    private ToolStripMenuItem _itemBatteryPreview = null!;
    private ToolStripMenuItem _itemBatteryResetPos = null!;

    // Menu items - Updates
    private ToolStripMenuItem _itemCheckUpdates = null!;
    private ToolStripMenuItem _itemAutoUpdate = null!;
    private System.Windows.Forms.Timer? _autoUpdateTimer;

    public TrayIconManager(TaskbarController controller, AppConfig config)
    {
        _controller = controller;
        _config = config;

        _contextMenu = new StickyContextMenuStrip();

        BuildMenu();
        _contextMenu.AttachStickySubmenus(_contextMenu.Items);
        _contextMenu.Opening += (s, e) => UpdateMenuStates();

        _notifyIcon = new NotifyIcon
        {
            Icon = CreateCleanIcon(),
            Text = "CleanTB - Pure Taskbar Customizer",
            ContextMenuStrip = _contextMenu,
            Visible = true
        };

        _notifyIcon.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                // Toggle between Clear and Normal on left click
                var nextMode = (_config.Mode == TaskbarMode.Clear) ? TaskbarMode.Normal : TaskbarMode.Clear;
                SetMode(nextMode);
            }
        };

        // Hardware Monitor, Peripheral Battery Monitor & Taskbar Overlays
        _hardwareMonitor = new HardwareMonitor(_config);
        _batteryMonitor = new PeripheralBatteryMonitor(_config);
        _batteryOverlay = new TaskbarBatteryOverlay(_config, _batteryMonitor, _contextMenu);
        _statsOverlay = new TaskbarStatsOverlay(_config, _hardwareMonitor, _contextMenu, _batteryOverlay);
        _batteryOverlay.SetStatsOverlay(_statsOverlay);

        if (_config.ShowBatteryStats)
        {
            _batteryOverlay.CheckVisibilityAndRender();
        }

        if (_config.ShowHardwareStats)
        {
            _statsOverlay.CheckVisibilityAndRender();
        }

        // Window message listener for TaskbarCreated, DisplayChange, and DeviceChange (instant wireless plug/unplug)
        _listener = new TaskbarMessageListener(
            onRefreshNeeded: () =>
            {
                _controller.ApplyAppearance(_config.Mode, _config.HideBorder);
                ReassertOverlaysOnTop();
            },
            onDeviceChange: () =>
            {
                _batteryMonitor.ForceRefresh();
            }
        );

        // Apply saved configuration on startup
        _cachedScheduledTaskEnabled = QueryScheduledTaskExists();
        EnsureCorrectStartupPath();
        UpdateMenuStates();
        _controller.ApplyAppearance(_config.Mode, _config.HideBorder);
        ReassertOverlaysOnTop();

        if (_config.AutoCheckUpdates)
        {
            // Delay background check by 6 seconds so CleanTB boots instantly without network waiting
            _autoUpdateTimer = new System.Windows.Forms.Timer { Interval = 6000 };
            _autoUpdateTimer.Tick += async (s, e) =>
            {
                _autoUpdateTimer.Stop();
                _autoUpdateTimer.Dispose();
                _autoUpdateTimer = null;
                await UpdateManager.CheckForUpdatesAsync(_config, isManualCheck: false);
            };
            _autoUpdateTimer.Start();
        }
    }

    private void BuildMenu()
    {
        _itemHeader = new ToolStripMenuItem("CleanTB v1.0 (Active)")
        {
            Enabled = false,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
            Image = CreateMenuHeaderLogo()
        };
        _contextMenu.Items.Add(_itemHeader);
        _contextMenu.Items.Add(new ToolStripSeparator());

        _itemClear = new ToolStripMenuItem("Clear (100% Transparent)", null, (s, e) => SetMode(TaskbarMode.Clear));
        _itemBlur = new ToolStripMenuItem("Blur (Fluent Glass)", null, (s, e) => SetMode(TaskbarMode.Blur));
        _itemAcrylic = new ToolStripMenuItem("Acrylic", null, (s, e) => SetMode(TaskbarMode.Acrylic));
        _itemTintDark = new ToolStripMenuItem("Tinted Dark (40%)", null, (s, e) => SetMode(TaskbarMode.TintedDark));
        _itemTintLight = new ToolStripMenuItem("Tinted Light (40%)", null, (s, e) => SetMode(TaskbarMode.TintedLight));
        _itemNormal = new ToolStripMenuItem("Normal (Windows Default)", null, (s, e) => SetMode(TaskbarMode.Normal));

        _contextMenu.Items.AddRange(new ToolStripItem[]
        {
            _itemClear,
            _itemBlur,
            _itemAcrylic,
            _itemTintDark,
            _itemTintLight,
            _itemNormal
        });

        _contextMenu.Items.Add(new ToolStripSeparator());

        // --- Hardware Stats Submenu ---
        _menuHardwareStats = new ToolStripMenuItem("Taskbar Hardware Stats (CPU/RAM/NET)");

        _itemStatsEnable = new ToolStripMenuItem("Show on Taskbar", null, (s, e) =>
        {
            _config.ShowHardwareStats = !_config.ShowHardwareStats;
            _config.Save();
            UpdateMenuStates();
            if (_config.ShowHardwareStats)
            {
                _statsOverlay.Show();
                _statsOverlay.CheckVisibilityAndRender();
            }
            else
            {
                _statsOverlay.Hide();
            }
        });

        // Dock Position Items
        _itemStatsPosLeft = new ToolStripMenuItem("Dock to Left Side (Empty Taskbar)", null, (s, e) =>
        {
            _statsOverlay.SetDockPosition(StatsPosition.Left);
            UpdateMenuStates();
        });
        _itemStatsPosRight = new ToolStripMenuItem("Dock to Right Side (System Tray)", null, (s, e) =>
        {
            _statsOverlay.SetDockPosition(StatsPosition.Right);
            UpdateMenuStates();
        });

        _itemStatsDownload = new ToolStripMenuItem("Show Download Speed", null, (s, e) =>
        {
            _config.ShowDownloadSpeed = !_config.ShowDownloadSpeed;
            _config.Save();
            UpdateMenuStates();
            _statsOverlay.CheckVisibilityAndRender();
        });

        _itemStatsUpload = new ToolStripMenuItem("Show Upload Speed", null, (s, e) =>
        {
            _config.ShowUploadSpeed = !_config.ShowUploadSpeed;
            _config.Save();
            UpdateMenuStates();
            _statsOverlay.CheckVisibilityAndRender();
        });

        _itemStatsNetArrows = new ToolStripMenuItem("Show Direction Arrows (▲ / ▼)", null, (s, e) =>
        {
            _config.ShowNetArrows = !_config.ShowNetArrows;
            _config.Save();
            UpdateMenuStates();
            _statsOverlay.CheckVisibilityAndRender();
        });

        _itemStatsCpu = new ToolStripMenuItem("Show CPU Usage", null, (s, e) =>
        {
            _config.ShowCpu = !_config.ShowCpu;
            _config.Save();
            UpdateMenuStates();
            _statsOverlay.CheckVisibilityAndRender();
        });

        _itemStatsRam = new ToolStripMenuItem("Show RAM Usage", null, (s, e) =>
        {
            _config.ShowRam = !_config.ShowRam;
            _config.Save();
            UpdateMenuStates();
            _statsOverlay.CheckVisibilityAndRender();
        });

        _itemStatsGpu = new ToolStripMenuItem("Show GPU Usage", null, (s, e) =>
        {
            _config.ShowGpu = !_config.ShowGpu;
            _config.Save();
            UpdateMenuStates();
            _hardwareMonitor.TriggerRefresh();
            _statsOverlay.CheckVisibilityAndRender();
        });

        // --- Temperatures Submenu ---
        _menuStatsTemps = new ToolStripMenuItem("Temperatures (CPU / RAM / GPU)");

        _itemTempCpu = new ToolStripMenuItem("Show CPU Temperature", null, async (s, e) =>
        {
            _config.ShowCpuTemp = !_config.ShowCpuTemp;
            _config.Save();
            UpdateMenuStates();
            _hardwareMonitor.TriggerRefresh();
            _statsOverlay.CheckVisibilityAndRender();
            await HandleDirectSensorLifecycleChangeAsync(justEnabled: _config.ShowCpuTemp);
        });

        _itemTempRam = new ToolStripMenuItem("Show RAM Temperature (When Available)", null, async (s, e) =>
        {
            _config.ShowRamTemp = !_config.ShowRamTemp;
            _config.Save();
            UpdateMenuStates();
            _hardwareMonitor.TriggerRefresh();
            _statsOverlay.CheckVisibilityAndRender();
            await HandleDirectSensorLifecycleChangeAsync(justEnabled: _config.ShowRamTemp);
        });

        _itemTempGpu = new ToolStripMenuItem("Show GPU Temperature", null, (s, e) =>
        {
            _config.ShowGpuTemp = !_config.ShowGpuTemp;
            _config.Save();
            UpdateMenuStates();
            _hardwareMonitor.TriggerRefresh();
            _statsOverlay.CheckVisibilityAndRender();
        });

        _itemTempAccents = new ToolStripMenuItem("Smart Thermal Warning Accents", null, (s, e) =>
        {
            _config.ThermalWarningAccents = !_config.ThermalWarningAccents;
            _config.Save();
            UpdateMenuStates();
            _statsOverlay.CheckVisibilityAndRender();
        });

        _itemTempCelsius = new ToolStripMenuItem("Unit: Celsius (°C)", null, (s, e) =>
        {
            if (_config.UseFahrenheit)
            {
                _config.UseFahrenheit = false;
                _config.Save();
                UpdateMenuStates();
                _statsOverlay.RefreshLayoutMetrics();
            }
        });

        _itemTempFahrenheit = new ToolStripMenuItem("Unit: Fahrenheit (°F)", null, (s, e) =>
        {
            if (!_config.UseFahrenheit)
            {
                _config.UseFahrenheit = true;
                _config.Save();
                UpdateMenuStates();
                _statsOverlay.RefreshLayoutMetrics();
            }
        });

        _itemTempStatus = new ToolStripMenuItem("Unlock CPU & RAM Temps...", null, async (s, e) =>
        {
            _contextMenu.DismissMenu();
            await HandleTempStatusClickAsync();
        });
        _contextMenu.RegisterCloseOnClickItem(_itemTempStatus);

        _menuStatsTemps.DropDownItems.AddRange(new ToolStripItem[]
        {
            _itemTempCpu,
            _itemTempRam,
            _itemTempGpu,
            new ToolStripSeparator(),
            _itemTempAccents,
            _itemTempCelsius,
            _itemTempFahrenheit,
            new ToolStripSeparator(),
            _itemTempStatus
        });

        _itemStatsClickThrough = new ToolStripMenuItem("Click-Through Mode", null, (s, e) =>
        {
            _statsOverlay.SetClickThrough(!_config.StatsClickThrough);
            UpdateMenuStates();
        });

        _itemStatsResetPos = new ToolStripMenuItem("Reset Dock Position", null, (s, e) =>
        {
            _statsOverlay.ResetPosition();
        });

        _menuHardwareStats.DropDownItems.AddRange(new ToolStripItem[]
        {
            _itemStatsEnable,
            new ToolStripSeparator(),
            _itemStatsPosLeft,
            _itemStatsPosRight,
            new ToolStripSeparator(),
            _itemStatsDownload,
            _itemStatsUpload,
            _itemStatsNetArrows,
            new ToolStripSeparator(),
            _itemStatsCpu,
            _itemStatsRam,
            _itemStatsGpu,
            _menuStatsTemps,
            new ToolStripSeparator(),
            _itemStatsClickThrough,
            _itemStatsResetPos
        });

        _contextMenu.Items.Add(_menuHardwareStats);

        // --- Wireless Device Battery HUD Menu ---
        _menuBatteryStats = new ToolStripMenuItem("Wireless Device Batteries");

        _itemBatteryEnable = new ToolStripMenuItem("Show Device Batteries on Taskbar", null, (s, e) =>
        {
            _config.ShowBatteryStats = !_config.ShowBatteryStats;
            _config.Save();
            UpdateMenuStates();
            _batteryOverlay.CheckVisibilityAndRender();
            _statsOverlay.UpdateDockPosition(force: true);
            _statsOverlay.CheckVisibilityAndRender();
        });

        _itemBatteryPosLeft = new ToolStripMenuItem("Dock to Left Side (Empty Taskbar)", null, (s, e) =>
        {
            _batteryOverlay.SetDockPosition(StatsPosition.Left);
            UpdateMenuStates();
        });

        _itemBatteryPosRight = new ToolStripMenuItem("Dock to Right Side (System Tray)", null, (s, e) =>
        {
            _batteryOverlay.SetDockPosition(StatsPosition.Right);
            UpdateMenuStates();
        });

        _itemBatteryHeadphones = new ToolStripMenuItem("Headphones & Earbuds", null, (s, e) =>
        {
            _config.ShowBatteryHeadphones = !_config.ShowBatteryHeadphones;
            _config.Save();
            UpdateMenuStates();
            _batteryOverlay.CheckVisibilityAndRender();
        });

        _itemBatteryMouse = new ToolStripMenuItem("Wireless Mouse", null, (s, e) =>
        {
            _config.ShowBatteryMouse = !_config.ShowBatteryMouse;
            _config.Save();
            UpdateMenuStates();
            _batteryOverlay.CheckVisibilityAndRender();
        });

        _itemBatteryKeyboard = new ToolStripMenuItem("Wireless Keyboard", null, (s, e) =>
        {
            _config.ShowBatteryKeyboard = !_config.ShowBatteryKeyboard;
            _config.Save();
            UpdateMenuStates();
            _batteryOverlay.CheckVisibilityAndRender();
        });

        _itemBatteryController = new ToolStripMenuItem("Wireless Game Controller", null, (s, e) =>
        {
            _config.ShowBatteryController = !_config.ShowBatteryController;
            _config.Save();
            UpdateMenuStates();
            _batteryOverlay.CheckVisibilityAndRender();
        });

        _itemBatteryOther = new ToolStripMenuItem("Other Wireless Devices", null, (s, e) =>
        {
            _config.ShowBatteryOther = !_config.ShowBatteryOther;
            _config.Save();
            UpdateMenuStates();
            _batteryOverlay.CheckVisibilityAndRender();
        });

        _itemBatteryPreview = new ToolStripMenuItem("Preview / Demo Devices", null, (s, e) =>
        {
            _config.BatteryPreviewMode = !_config.BatteryPreviewMode;
            _config.Save();
            UpdateMenuStates();
            _batteryOverlay.CheckVisibilityAndRender();
        });

        _itemBatteryResetPos = new ToolStripMenuItem("Reset Dock Position", null, (s, e) =>
        {
            _batteryOverlay.ResetPosition();
            UpdateMenuStates();
        });

        _menuBatteryStats.DropDownItems.AddRange(new ToolStripItem[]
        {
            _itemBatteryEnable,
            new ToolStripSeparator(),
            _itemBatteryPosLeft,
            _itemBatteryPosRight,
            new ToolStripSeparator(),
            _itemBatteryHeadphones,
            _itemBatteryMouse,
            _itemBatteryKeyboard,
            _itemBatteryController,
            _itemBatteryOther,
            new ToolStripSeparator(),
            _itemBatteryPreview,
            new ToolStripSeparator(),
            _itemBatteryResetPos
        });

        _contextMenu.Items.Add(_menuBatteryStats);

        _contextMenu.Items.Add(new ToolStripSeparator());

        _itemHideBorder = new ToolStripMenuItem("Hide Top Border Line", null, (s, e) =>
        {
            _config.HideBorder = !_config.HideBorder;
            _config.Save();
            UpdateMenuStates();
            _controller.ApplyAppearance(_config.Mode, _config.HideBorder);
            ReassertOverlaysOnTop();
        });

        _itemStartup = new ToolStripMenuItem("Start with Windows", null, (s, e) =>
        {
            bool nextState = !IsStartupEnabled();
            _config.RunAtStartup = nextState;
            SetStartupRegistry(nextState);
            _config.Save();
            UpdateMenuStates();
        });

        _contextMenu.Items.Add(_itemHideBorder);
        _contextMenu.Items.Add(_itemStartup);

        _contextMenu.Items.Add(new ToolStripSeparator());

        _itemCheckUpdates = new ToolStripMenuItem("Check for Updates...", null, async (s, e) =>
        {
            _contextMenu.DismissMenu();
            await UpdateManager.CheckForUpdatesAsync(_config, isManualCheck: true);
        });
        _contextMenu.RegisterCloseOnClickItem(_itemCheckUpdates);

        _itemAutoUpdate = new ToolStripMenuItem("Auto-Check for Updates", null, (s, e) =>
        {
            _config.AutoCheckUpdates = !_config.AutoCheckUpdates;
            _config.Save();
            UpdateMenuStates();
        });

        _contextMenu.Items.Add(_itemCheckUpdates);
        _contextMenu.Items.Add(_itemAutoUpdate);

        _contextMenu.Items.Add(new ToolStripSeparator());

        var itemRefresh = new ToolStripMenuItem("Refresh Taskbar", null, (s, e) =>
        {
            _controller.ApplyAppearance(_config.Mode, _config.HideBorder);
            _batteryMonitor.ForceRefresh();
            ReassertOverlaysOnTop();
        });

        var itemAbout = new ToolStripMenuItem("About CleanTB", null, (s, e) =>
        {
            _contextMenu.DismissMenu();
            MessageBox.Show(
                "CleanTB - Ultra-lightweight, bloat-free Windows 11 Taskbar Customizer.\n\n" +
                "• Memory: ~12 MB - 18 MB active RAM\n" +
                "• CPU: < 0.1% usage\n" +
                "• Real-time Download, Upload, CPU, RAM, GPU & Temperature HUD\n" +
                "• Move HUD to Left (empty space) or Right (tray)\n" +
                "• No telemetry, no logs, pure native Win32 APIs\n\n" +
                "Tips:\n" +
                "• Left-click tray icon to toggle Clear / Default.\n" +
                "• Double-click the taskbar stats HUD to open Task Manager.\n" +
                "• Double-click the battery HUD to open Bluetooth Settings.\n" +
                "• Choose Left or Right side docking independently for Hardware Stats and Battery HUD.\n" +
                "• Drag either HUD to place it anywhere on screen.",
                "About CleanTB",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        });
        _contextMenu.RegisterCloseOnClickItem(itemAbout);

        var itemExit = new ToolStripMenuItem("Exit CleanTB", null, (s, e) =>
        {
            _contextMenu.DismissMenu();
            _controller.RestoreDefault();
            _statsOverlay.Hide();
            _batteryOverlay.Hide();
            Application.Exit();
        });
        _contextMenu.RegisterCloseOnClickItem(itemExit);

        _contextMenu.Items.Add(itemRefresh);
        _contextMenu.Items.Add(itemAbout);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(itemExit);
    }

    private void SetMode(TaskbarMode mode)
    {
        _config.Mode = mode;
        _config.Save();
        UpdateMenuStates();
        _controller.ApplyAppearance(_config.Mode, _config.HideBorder);
        ReassertOverlaysOnTop();
    }

    private void ReassertOverlaysOnTop()
    {
        if (_config.ShowBatteryStats && !_batteryOverlay.IsDisposed)
        {
            _batteryOverlay.CheckVisibilityAndRender();
            _batteryOverlay.BringToTopMost();
        }
        if (_config.ShowHardwareStats && !_statsOverlay.IsDisposed)
        {
            _statsOverlay.CheckVisibilityAndRender();
            _statsOverlay.BringToTopMost();
        }

        // Explorer's DirectComposition and XAML tree updates asynchronously after setting taskbar appearance.
        // Schedule staggered re-assertions so HUD overlays stay on top across all modes (Blur, Acrylic, Tinted, Normal).
        int[] delays = { 50, 150, 300, 600 };
        foreach (int delay in delays)
        {
            Task.Delay(delay).ContinueWith(_ =>
            {
                if (!_batteryOverlay.IsDisposed) _batteryOverlay.BringToTopMost();
                if (!_statsOverlay.IsDisposed) _statsOverlay.BringToTopMost();
            });
        }
    }

    private void UpdateMenuStates()
    {
        _itemClear.Checked = _config.Mode == TaskbarMode.Clear;
        _itemBlur.Checked = _config.Mode == TaskbarMode.Blur;
        _itemAcrylic.Checked = _config.Mode == TaskbarMode.Acrylic;
        _itemTintDark.Checked = _config.Mode == TaskbarMode.TintedDark;
        _itemTintLight.Checked = _config.Mode == TaskbarMode.TintedLight;
        _itemNormal.Checked = _config.Mode == TaskbarMode.Normal;

        _itemStatsEnable.Checked = _config.ShowHardwareStats;
        _itemStatsPosLeft.Enabled = _config.ShowHardwareStats;
        _itemStatsPosRight.Enabled = _config.ShowHardwareStats;

        if (_config.StatsCustomX.HasValue)
        {
            _itemStatsPosLeft.Checked = false;
            _itemStatsPosRight.Checked = false;
        }
        else
        {
            _itemStatsPosLeft.Checked = (_config.StatsPosition == StatsPosition.Left);
            _itemStatsPosRight.Checked = (_config.StatsPosition == StatsPosition.Right);
        }

        _itemStatsDownload.Checked = _config.ShowDownloadSpeed;
        _itemStatsUpload.Checked = _config.ShowUploadSpeed;
        _itemStatsNetArrows.Checked = _config.ShowNetArrows;
        _itemStatsCpu.Checked = _config.ShowCpu;
        _itemStatsRam.Checked = _config.ShowRam;
        _itemStatsGpu.Checked = _config.ShowGpu;

        _menuStatsTemps.Enabled = _config.ShowHardwareStats;
        _itemTempCpu.Enabled = _config.ShowCpu;
        _itemTempRam.Enabled = _config.ShowRam;
        _itemTempGpu.Enabled = _config.ShowGpu;
        _itemTempCpu.Checked = _config.ShowCpuTemp;
        _itemTempRam.Checked = _config.ShowRamTemp;
        _itemTempGpu.Checked = _config.ShowGpuTemp;
        _itemTempAccents.Checked = _config.ThermalWarningAccents;
        _itemTempCelsius.Checked = !_config.UseFahrenheit;
        _itemTempFahrenheit.Checked = _config.UseFahrenheit;

        switch (_hardwareMonitor.ThermalStatus)
        {
            case ThermalAccessStatus.DirectPawnIo:
                _itemTempStatus.Text = "✓ Direct CPU & RAM Sensors Active";
                _itemTempStatus.Enabled = false;
                break;
            case ThermalAccessStatus.ExternalBridge:
                _itemTempStatus.Text = "✓ CPU/RAM Active via Background Monitor";
                _itemTempStatus.Enabled = true;
                break;
            case ThermalAccessStatus.NeedsPawnIo:
                _itemTempStatus.Text = "Setup PawnIO Driver for CPU & RAM Temps...";
                _itemTempStatus.Enabled = true;
                break;
            case ThermalAccessStatus.NeedsAdmin:
                _itemTempStatus.Text = "Unlock CPU & RAM Temps (Restart as Admin)...";
                _itemTempStatus.Enabled = true;
                break;
            default:
                _itemTempStatus.Text = "Unlock CPU & RAM Temps (Setup Sensors)...";
                _itemTempStatus.Enabled = true;
                break;
        }

        _itemStatsClickThrough.Checked = _config.StatsClickThrough;

        // Battery HUD menu states
        _itemBatteryEnable.Checked = _config.ShowBatteryStats;
        _itemBatteryPosLeft.Enabled = _config.ShowBatteryStats;
        _itemBatteryPosRight.Enabled = _config.ShowBatteryStats;
        _itemBatteryHeadphones.Enabled = _config.ShowBatteryStats;
        _itemBatteryMouse.Enabled = _config.ShowBatteryStats;
        _itemBatteryKeyboard.Enabled = _config.ShowBatteryStats;
        _itemBatteryController.Enabled = _config.ShowBatteryStats;
        _itemBatteryOther.Enabled = _config.ShowBatteryStats;
        _itemBatteryPreview.Enabled = _config.ShowBatteryStats;

        bool isCustomBattery = _config.BatteryCustomX.HasValue && _config.BatteryCustomY.HasValue;
        _itemBatteryResetPos.Enabled = _config.ShowBatteryStats && isCustomBattery;

        if (isCustomBattery)
        {
            _itemBatteryPosLeft.Checked = false;
            _itemBatteryPosRight.Checked = false;
        }
        else
        {
            _itemBatteryPosLeft.Checked = (_config.BatteryPosition == StatsPosition.Left);
            _itemBatteryPosRight.Checked = (_config.BatteryPosition == StatsPosition.Right);
        }

        _itemBatteryHeadphones.Checked = _config.ShowBatteryHeadphones;
        _itemBatteryMouse.Checked = _config.ShowBatteryMouse;
        _itemBatteryKeyboard.Checked = _config.ShowBatteryKeyboard;
        _itemBatteryController.Checked = _config.ShowBatteryController;
        _itemBatteryOther.Checked = _config.ShowBatteryOther;
        _itemBatteryPreview.Checked = _config.BatteryPreviewMode;

        _itemHideBorder.Checked = _config.HideBorder;
        _itemStartup.Checked = IsStartupEnabled();
        _itemAutoUpdate.Checked = _config.AutoCheckUpdates;

        _itemHeader.Text = $"CleanTB v{UpdateManager.CurrentVersion} (Mode: {_config.Mode})";
        _notifyIcon.Text = $"CleanTB: {_config.Mode}";
    }

    private bool IsStartupEnabled()
    {
        if (_cachedScheduledTaskEnabled) return true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue("CleanTB") != null;
        }
        catch { return false; }
    }

    private void SetStartupRegistry(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);

            if (enable)
            {
                if (ThermalMonitor.IsRunningAsAdministrator() && CreateOrUpdateScheduledTask(Application.ExecutablePath))
                {
                    key?.DeleteValue("CleanTB", false);
                    _cachedScheduledTaskEnabled = true;
                    return;
                }

                DeleteScheduledTask();
                _cachedScheduledTaskEnabled = false;
                key?.SetValue("CleanTB", $"\"{Application.ExecutablePath}\"");
            }
            else
            {
                key?.DeleteValue("CleanTB", false);
                DeleteScheduledTask();
                _cachedScheduledTaskEnabled = false;
            }
        }
        catch { }
    }

    private void EnsureCorrectStartupPath()
    {
        try
        {
            bool isAdmin = ThermalMonitor.IsRunningAsAdministrator();
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            var currentVal = key?.GetValue("CleanTB") as string;

            if (!string.IsNullOrEmpty(currentVal))
            {
                if (isAdmin && CreateOrUpdateScheduledTask(Application.ExecutablePath))
                {
                    key?.DeleteValue("CleanTB", false);
                    _cachedScheduledTaskEnabled = true;
                    return;
                }

                string expectedVal = $"\"{Application.ExecutablePath}\"";
                if (!string.Equals(currentVal.Trim('\"'), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                {
                    key?.SetValue("CleanTB", expectedVal);
                }
            }
            else if (isAdmin && _cachedScheduledTaskEnabled)
            {
                CreateOrUpdateScheduledTask(Application.ExecutablePath);
            }
        }
        catch { }
    }

    private static bool QueryScheduledTaskExists()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/Query /TN \"CleanTB\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            p?.WaitForExit(1500);
            return p?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool CreateOrUpdateScheduledTask(string exePath)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Create /TN \"CleanTB\" /TR \"\\\"{exePath}\\\"\" /SC ONLOGON /RL HIGHEST /F",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteScheduledTask()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/Delete /TN \"CleanTB\" /F",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            p?.WaitForExit(2000);
        }
        catch { }
    }

    private async Task HandleDirectSensorLifecycleChangeAsync(bool justEnabled)
    {
        if (!justEnabled) return;

        // Wait briefly for TriggerRefresh() to sample active direct or zero-admin external bridges
        await Task.Delay(250);
        if (_hardwareMonitor.ThermalStatus == ThermalAccessStatus.ExternalBridge)
        {
            return;
        }

        if (!ThermalMonitor.IsRunningAsAdministrator() || !ThermalMonitor.IsPawnIoInstalled())
        {
            await HandleTempStatusClickAsync();
        }
    }

    private async Task HandleTempStatusClickAsync()
    {
        bool isAdmin = ThermalMonitor.IsRunningAsAdministrator();
        bool hasPawnIo = ThermalMonitor.IsPawnIoInstalled();

        if (isAdmin && hasPawnIo)
        {
            return;
        }

        _contextMenu.DismissMenu();

        if (_hardwareMonitor.ThermalStatus == ThermalAccessStatus.ExternalBridge)
        {
            var bridgeRes = MessageBox.Show(
                "CPU/RAM temperatures are currently being read seamlessly via your running background hardware monitor.\n\n" +
                "Would you also like to set up standalone direct sensors (PawnIO driver + Administrator mode) so CleanTB can read CPU & RAM temperatures independently?",
                "CleanTB Temperature Sensors",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information
            );
            if (bridgeRes != DialogResult.Yes) return;
        }

        if (hasPawnIo && !isAdmin)
        {
            var res = MessageBox.Show(
                "GPU temperature works in standard mode, while Windows requires Administrator privileges to read CPU & RAM hardware sensors directly.\n\n" +
                "Restart CleanTB as Administrator now to unlock CPU & RAM temperatures?",
                "CleanTB - Unlock CPU & RAM Temperatures",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );
            if (res == DialogResult.Yes)
            {
                RestartAsAdministrator();
            }
            return;
        }

        string driverPrompt = isAdmin
            ? "CleanTB is running as Administrator, and needs the signed PawnIO driver (~100 KB, modern Defender-safe replacement for WinRing0) to read CPU & DDR4/DDR5 RAM temperatures directly.\n\nDownload and install PawnIO now?"
            : "GPU temperature works out of the box with zero extra drivers.\n\n" +
              "To read CPU & DDR4/DDR5 RAM temperatures directly, CleanTB uses the official signed PawnIO driver (modern Defender-safe replacement for WinRing0) together with Administrator privileges.\n\n" +
              "(Tip: You can also keep CleanTB non-admin by running HWiNFO64, LibreHardwareMonitor, Core Temp, or AIDA64 in the background.)\n\n" +
              "Download and install the signed PawnIO driver (~100 KB) now?";

        var setupRes = MessageBox.Show(
            driverPrompt,
            "CleanTB - Unlock CPU & RAM Temperatures",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information
        );

        if (setupRes != DialogResult.Yes) return;

        bool installed = await TryInstallPawnIoAsync();
        if (!installed)
        {
            installed = ThermalMonitor.IsPawnIoInstalled();
        }

        if (installed)
        {
            if (!ThermalMonitor.IsRunningAsAdministrator())
            {
                RestartAsAdministrator();
            }
            else
            {
                _hardwareMonitor.TriggerRefresh();
                UpdateMenuStates();
            }
        }
        else
        {
            MessageBox.Show(
                "PawnIO driver installation was not completed. GPU temperature will continue running normally.\n\nYou can retry anytime from Taskbar Hardware Stats → Temperatures.",
                "CleanTB",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }
    }

    private static async Task<bool> TryInstallPawnIoAsync()
    {
        // First try winget if available (clean official Microsoft-hosted manifest for namazso.PawnIO)
        try
        {
            using var wg = Process.Start(new ProcessStartInfo
            {
                FileName = "winget.exe",
                Arguments = "install -e --id namazso.PawnIO --silent --accept-package-agreements --accept-source-agreements",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (wg != null)
            {
                await wg.WaitForExitAsync();
                if (ThermalMonitor.IsPawnIoInstalled()) return true;
            }
        }
        catch
        {
            // Fallback to direct official GitHub release installer below
        }

        // Fallback: Download official PawnIO_setup.exe from GitHub releases and run installer
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CleanTB-App", UpdateManager.CurrentVersion.ToString()));

            var resp = await client.GetAsync("https://api.github.com/repos/namazso/PawnIO.Setup/releases/latest");
            if (!resp.IsSuccessStatusCode) return false;

            string json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            string setupUrl = "";

            if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.TryGetProperty("name", out var nameProp))
                    {
                        string assetName = nameProp.GetString() ?? "";
                        if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                            asset.TryGetProperty("browser_download_url", out var urlProp))
                        {
                            setupUrl = urlProp.GetString() ?? "";
                            break;
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(setupUrl)) return false;

            string tempExe = Path.Combine(Path.GetTempPath(), "PawnIO_setup.exe");
            byte[] installerBytes = await client.GetByteArrayAsync(setupUrl);
            await File.WriteAllBytesAsync(tempExe, installerBytes);

            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = tempExe,
                    UseShellExecute = true,
                    Verb = "runas"
                });
                if (proc != null)
                {
                    await proc.WaitForExitAsync();
                }
            }
            finally
            {
                try { File.Delete(tempExe); } catch { }
            }

            return ThermalMonitor.IsPawnIoInstalled();
        }
        catch
        {
            return false;
        }
    }

    private void RestartAsAdministrator()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                Arguments = $"--wait-pid {Environment.ProcessId}",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
            Process.Start(startInfo);

            _controller.RestoreDefault();
            _statsOverlay.Hide();
            _batteryOverlay.Hide();
            _notifyIcon.Visible = false;
            Application.Exit();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User declined UAC prompt; keep running normally in standard mode
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not restart CleanTB as Administrator:\n{ex.Message}",
                "CleanTB",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
        }
    }

    private static Icon? LoadLogoIcon(int width, int height)
    {
        try
        {
            string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (File.Exists(icoPath))
            {
                return new Icon(icoPath, width, height);
            }
        }
        catch { }

        try
        {
            using var resStream = typeof(TrayIconManager).Assembly.GetManifestResourceStream("CleanTB.app.ico");
            if (resStream != null)
            {
                return new Icon(resStream, width, height);
            }
        }
        catch { }

        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                return Icon.ExtractAssociatedIcon(exePath);
            }
        }
        catch { }

        return null;
    }

    private static Image? CreateMenuHeaderLogo()
    {
        try
        {
            Size sz = SystemInformation.SmallIconSize;
            using var ico = LoadLogoIcon(sz.Width, sz.Height);
            if (ico != null)
            {
                return ico.ToBitmap();
            }
        }
        catch { }
        return null;
    }

    private static Icon CreateCleanIcon()
    {
        var loaded = LoadLogoIcon(32, 32);
        if (loaded != null) return loaded;

        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var screenPen = new Pen(Color.FromArgb(180, 255, 255, 255), 1.5f);
            g.DrawRoundedRectangle(screenPen, 3, 3, 26, 26, 4, 4);

            using var dockBrush = new SolidBrush(Color.FromArgb(160, 0, 162, 237));
            g.FillRoundedRectangle(dockBrush, 6, 21, 20, 5, 2, 2);

            using var dotBrush = new SolidBrush(Color.White);
            g.FillEllipse(dotBrush, 9, 22.5f, 2, 2);
            g.FillEllipse(dotBrush, 15, 22.5f, 2, 2);
            g.FillEllipse(dotBrush, 21, 22.5f, 2, 2);
        }

        IntPtr hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    public void Dispose()
    {
        _autoUpdateTimer?.Stop();
        _autoUpdateTimer?.Dispose();
        _autoUpdateTimer = null;
        _statsOverlay.Hide();
        _statsOverlay.Dispose();
        _batteryOverlay.Hide();
        _batteryOverlay.Dispose();
        _hardwareMonitor.Dispose();
        _batteryMonitor.Dispose();
        _listener.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _itemHeader?.Image?.Dispose();
        _contextMenu.Dispose();
    }

    private class TaskbarMessageListener : NativeWindow, IDisposable
    {
        [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string lpString);

        private readonly uint _wmTaskbarCreated;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int WM_DEVICECHANGE = 0x0219;
        private readonly Action _onRefreshNeeded;
        private readonly Action _onDeviceChange;

        public TaskbarMessageListener(Action onRefreshNeeded, Action onDeviceChange)
        {
            _onRefreshNeeded = onRefreshNeeded;
            _onDeviceChange = onDeviceChange;
            _wmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");

            CreateHandle(new CreateParams
            {
                Caption = "CleanTB_Listener",
                Style = 0
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == _wmTaskbarCreated || m.Msg == WM_DISPLAYCHANGE)
            {
                _onRefreshNeeded?.Invoke();
            }
            else if (m.Msg == WM_DEVICECHANGE)
            {
                _onDeviceChange?.Invoke();
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            DestroyHandle();
        }
    }

    internal sealed class StickyContextMenuStrip : ContextMenuStrip, IMessageFilter
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private bool _forceAllowClose;
        private bool _filterInstalled;
        private readonly System.Collections.Generic.HashSet<ToolStripItem> _closeOnClickItems = new();
        private readonly System.Collections.Generic.HashSet<ToolStripDropDown> _hookedDropDowns = new();

        public StickyContextMenuStrip()
        {
            RenderMode = ToolStripRenderMode.System;
            ShowCheckMargin = false;
            ShowImageMargin = true;

            Closing += OnAnyDropDownClosing;
            Opened += OnRootOpened;
            Closed += OnRootClosed;
        }

        public void RegisterCloseOnClickItem(ToolStripItem item) => _closeOnClickItems.Add(item);

        public void AttachStickySubmenus(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                if (item is ToolStripMenuItem menuItem && menuItem.HasDropDownItems)
                {
                    HookDropDown(menuItem.DropDown);
                    AttachStickySubmenus(menuItem.DropDownItems);
                }
            }
        }

        private void HookDropDown(ToolStripDropDown dropDown)
        {
            if (_hookedDropDowns.Add(dropDown))
            {
                dropDown.Closing += OnAnyDropDownClosing;
                dropDown.ItemClicked += OnAnyItemClicked;
            }
        }

        private void OnRootOpened(object? sender, EventArgs e)
        {
            _forceAllowClose = false;
            if (IsHandleCreated)
            {
                SetForegroundWindow(Handle);
            }
            if (!_filterInstalled)
            {
                Application.AddMessageFilter(this);
                _filterInstalled = true;
            }
        }

        private void OnRootClosed(object? sender, ToolStripDropDownClosedEventArgs e)
        {
            _forceAllowClose = false;
            if (_filterInstalled)
            {
                Application.RemoveMessageFilter(this);
                _filterInstalled = false;
            }
        }

        public void DismissMenu()
        {
            _forceAllowClose = true;
            if (Visible)
            {
                Close(ToolStripDropDownCloseReason.CloseCalled);
            }
        }

        private void OnAnyItemClicked(object? sender, ToolStripItemClickedEventArgs e)
        {
            if (e.ClickedItem != null && _closeOnClickItems.Contains(e.ClickedItem))
            {
                _forceAllowClose = true;
            }
        }

        protected override void OnItemClicked(ToolStripItemClickedEventArgs e)
        {
            if (e.ClickedItem != null && _closeOnClickItems.Contains(e.ClickedItem))
            {
                _forceAllowClose = true;
            }
            base.OnItemClicked(e);
        }

        private void OnAnyDropDownClosing(object? sender, ToolStripDropDownClosingEventArgs e)
        {
            if (!_forceAllowClose && e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
            {
                e.Cancel = true;
            }
        }

        public bool PreFilterMessage(ref Message m)
        {
            const int WM_KEYDOWN = 0x0100;
            const int WM_SYSKEYDOWN = 0x0104;

            if ((m.Msg == WM_KEYDOWN || m.Msg == WM_SYSKEYDOWN) && (int)m.WParam == (int)Keys.Return && Visible)
            {
                BeginInvoke(DismissMenu);
                return true;
            }
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (_filterInstalled)
            {
                Application.RemoveMessageFilter(this);
                _filterInstalled = false;
            }
            base.Dispose(disposing);
        }
    }
}

public static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics g, Brush brush, float x, float y, float width, float height, float radiusX, float radiusY)
    {
        using var path = CreateRoundedRectanglePath(x, y, width, height, radiusX);
        g.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics g, Pen pen, float x, float y, float width, float height, float radiusX, float radiusY)
    {
        using var path = CreateRoundedRectanglePath(x, y, width, height, radiusX);
        g.DrawPath(pen, path);
    }

    private static GraphicsPath CreateRoundedRectanglePath(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath();
        float diameter = radius * 2;
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
