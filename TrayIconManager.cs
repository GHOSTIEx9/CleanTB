using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CleanTB;

public class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly TaskbarController _controller;
    private readonly AppConfig _config;
    private readonly TaskbarMessageListener _listener;
    private readonly HardwareMonitor _hardwareMonitor;
    private readonly PeripheralBatteryMonitor _batteryMonitor;
    private readonly TaskbarStatsOverlay _statsOverlay;

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
    private ToolStripMenuItem _itemStatsClickThrough = null!;
    private ToolStripMenuItem _itemStatsResetPos = null!;

    // Menu items - Peripheral Battery Stats
    private ToolStripMenuItem _menuBatteryStats = null!;
    private ToolStripMenuItem _itemBatteryEnable = null!;
    private ToolStripMenuItem _itemBatteryHeadphones = null!;
    private ToolStripMenuItem _itemBatteryMouse = null!;
    private ToolStripMenuItem _itemBatteryKeyboard = null!;
    private ToolStripMenuItem _itemBatteryController = null!;
    private ToolStripMenuItem _itemBatteryPreview = null!;

    public TrayIconManager(TaskbarController controller, AppConfig config)
    {
        _controller = controller;
        _config = config;

        _contextMenu = new ContextMenuStrip
        {
            RenderMode = ToolStripRenderMode.System,
            ShowCheckMargin = false,
            ShowImageMargin = true
        };

        BuildMenu();

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

        // Hardware Monitor, Peripheral Battery Monitor & Taskbar Stats Overlay
        _hardwareMonitor = new HardwareMonitor(_config);
        _batteryMonitor = new PeripheralBatteryMonitor(_config);
        _statsOverlay = new TaskbarStatsOverlay(_config, _hardwareMonitor, _batteryMonitor, _contextMenu);

        if (_config.ShowHardwareStats || _config.ShowBatteryStats)
        {
            _statsOverlay.Show();
            _statsOverlay.CheckVisibilityAndRender();
        }

        // Window message listener for TaskbarCreated & DisplayChange
        _listener = new TaskbarMessageListener(() =>
        {
            _controller.ApplyAppearance(_config.Mode, _config.HideBorder);
            if (_config.ShowHardwareStats && !_statsOverlay.IsDisposed)
            {
                _statsOverlay.CheckVisibilityAndRender();
            }
        });

        // Apply saved configuration on startup
        EnsureCorrectStartupPath();
        UpdateMenuStates();
        _controller.ApplyAppearance(_config.Mode, _config.HideBorder);
    }

    private void BuildMenu()
    {
        _itemHeader = new ToolStripMenuItem("CleanTB v1.0 (Active)")
        {
            Enabled = false,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold)
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

        _itemStatsDownload = new ToolStripMenuItem("Show Download Speed Only", null, (s, e) =>
        {
            _config.ShowDownloadOnly = !_config.ShowDownloadOnly;
            if (_config.ShowDownloadOnly)
            {
                _config.ShowUploadSpeed = false;
            }
            _config.Save();
            UpdateMenuStates();
            _statsOverlay.CheckVisibilityAndRender();
        });

        _itemStatsUpload = new ToolStripMenuItem("Show Upload Speed", null, (s, e) =>
        {
            _config.ShowUploadSpeed = !_config.ShowUploadSpeed;
            if (_config.ShowUploadSpeed)
            {
                _config.ShowDownloadOnly = false;
            }
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
            _statsOverlay.CheckVisibilityAndRender();
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
            _statsOverlay.CheckVisibilityAndRender();
        });

        _itemBatteryHeadphones = new ToolStripMenuItem("Headphones & Earbuds", null, (s, e) =>
        {
            _config.ShowBatteryHeadphones = !_config.ShowBatteryHeadphones;
            _config.Save();
            UpdateMenuStates();
            _statsOverlay.CheckVisibilityAndRender();
        });

        _itemBatteryMouse = new ToolStripMenuItem("Wireless Mouse", null, (s, e) =>
        {
            _config.ShowBatteryMouse = !_config.ShowBatteryMouse;
            _config.Save();
            UpdateMenuStates();
            _statsOverlay.CheckVisibilityAndRender();
        });

        _itemBatteryKeyboard = new ToolStripMenuItem("Wireless Keyboard", null, (s, e) =>
        {
            _config.ShowBatteryKeyboard = !_config.ShowBatteryKeyboard;
            _config.Save();
            UpdateMenuStates();
            _statsOverlay.CheckVisibilityAndRender();
        });

        _itemBatteryController = new ToolStripMenuItem("Wireless Game Controller", null, (s, e) =>
        {
            _config.ShowBatteryController = !_config.ShowBatteryController;
            _config.Save();
            UpdateMenuStates();
            _statsOverlay.CheckVisibilityAndRender();
        });

        _itemBatteryPreview = new ToolStripMenuItem("Preview / Demo Devices", null, (s, e) =>
        {
            _config.BatteryPreviewMode = !_config.BatteryPreviewMode;
            _config.Save();
            UpdateMenuStates();
            _statsOverlay.CheckVisibilityAndRender();
        });

        _menuBatteryStats.DropDownItems.AddRange(new ToolStripItem[]
        {
            _itemBatteryEnable,
            new ToolStripSeparator(),
            _itemBatteryHeadphones,
            _itemBatteryMouse,
            _itemBatteryKeyboard,
            _itemBatteryController,
            new ToolStripSeparator(),
            _itemBatteryPreview
        });

        _contextMenu.Items.Add(_menuBatteryStats);

        _contextMenu.Items.Add(new ToolStripSeparator());

        _itemHideBorder = new ToolStripMenuItem("Hide Top Border Line", null, (s, e) =>
        {
            _config.HideBorder = !_config.HideBorder;
            _config.Save();
            UpdateMenuStates();
            _controller.ApplyAppearance(_config.Mode, _config.HideBorder);
        });

        _itemStartup = new ToolStripMenuItem("Start with Windows", null, (s, e) =>
        {
            _config.RunAtStartup = !_config.RunAtStartup;
            SetStartupRegistry(_config.RunAtStartup);
            _config.Save();
            UpdateMenuStates();
        });

        _contextMenu.Items.Add(_itemHideBorder);
        _contextMenu.Items.Add(_itemStartup);

        _contextMenu.Items.Add(new ToolStripSeparator());

        var itemRefresh = new ToolStripMenuItem("Refresh Taskbar", null, (s, e) =>
        {
            _controller.ApplyAppearance(_config.Mode, _config.HideBorder);
            _batteryMonitor.ForceRefresh();
            _statsOverlay.CheckVisibilityAndRender();
        });

        var itemAbout = new ToolStripMenuItem("About CleanTB", null, (s, e) =>
        {
            MessageBox.Show(
                "CleanTB - Ultra-lightweight, bloat-free Windows 11 Taskbar Customizer.\n\n" +
                "• Memory: ~12 MB - 15 MB active RAM\n" +
                "• CPU: < 0.1% usage\n" +
                "• Real-time Taskbar Download, Upload, CPU, RAM, & GPU HUD\n" +
                "• Move HUD to Left (empty space) or Right (tray)\n" +
                "• No telemetry, no logs, pure native Win32 APIs\n\n" +
                "Tips:\n" +
                "• Left-click tray icon to toggle Clear / Default.\n" +
                "• Double-click the taskbar stats HUD to open Task Manager.\n" +
                "• Choose Left or Right side docking from the Position menu.\n" +
                "• Drag the stats HUD to place it anywhere on screen.",
                "About CleanTB",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        });

        var itemExit = new ToolStripMenuItem("Exit CleanTB", null, (s, e) =>
        {
            _controller.RestoreDefault();
            _statsOverlay.Hide();
            Application.Exit();
        });

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

        _itemStatsDownload.Checked = _config.ShowDownloadOnly;
        _itemStatsUpload.Checked = _config.ShowUploadSpeed;
        _itemStatsNetArrows.Checked = _config.ShowNetArrows;
        _itemStatsCpu.Checked = _config.ShowCpu;
        _itemStatsRam.Checked = _config.ShowRam;
        _itemStatsGpu.Checked = _config.ShowGpu;
        _itemStatsClickThrough.Checked = _config.StatsClickThrough;

        // Battery HUD menu states
        _itemBatteryEnable.Checked = _config.ShowBatteryStats;
        _itemBatteryHeadphones.Enabled = _config.ShowBatteryStats;
        _itemBatteryMouse.Enabled = _config.ShowBatteryStats;
        _itemBatteryKeyboard.Enabled = _config.ShowBatteryStats;
        _itemBatteryController.Enabled = _config.ShowBatteryStats;
        _itemBatteryPreview.Enabled = _config.ShowBatteryStats;

        _itemBatteryHeadphones.Checked = _config.ShowBatteryHeadphones;
        _itemBatteryMouse.Checked = _config.ShowBatteryMouse;
        _itemBatteryKeyboard.Checked = _config.ShowBatteryKeyboard;
        _itemBatteryController.Checked = _config.ShowBatteryController;
        _itemBatteryPreview.Checked = _config.BatteryPreviewMode;

        _itemHideBorder.Checked = _config.HideBorder;
        _itemStartup.Checked = IsStartupEnabled();

        _itemHeader.Text = $"CleanTB (Mode: {_config.Mode})";
        _notifyIcon.Text = $"CleanTB: {_config.Mode}";
    }

    private static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue("CleanTB") != null;
        }
        catch { return false; }
    }

    private static void SetStartupRegistry(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;

            if (enable)
            {
                key.SetValue("CleanTB", $"\"{Application.ExecutablePath}\"");
            }
            else
            {
                key.DeleteValue("CleanTB", false);
            }
        }
        catch { }
    }

    private static void EnsureCorrectStartupPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;

            var currentVal = key.GetValue("CleanTB") as string;
            if (string.IsNullOrEmpty(currentVal)) return;

            string expectedVal = $"\"{Application.ExecutablePath}\"";
            if (!string.Equals(currentVal.Trim('\"'), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue("CleanTB", expectedVal);
            }
        }
        catch { }
    }

    private static Icon CreateCleanIcon()
    {
        try
        {
            string icoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (System.IO.File.Exists(icoPath))
            {
                return new Icon(icoPath, 32, 32);
            }
        }
        catch { }

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
        _statsOverlay.Hide();
        _statsOverlay.Dispose();
        _hardwareMonitor.Dispose();
        _batteryMonitor.Dispose();
        _listener.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
    }

    private class TaskbarMessageListener : NativeWindow, IDisposable
    {
        [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string lpString);

        private readonly uint _wmTaskbarCreated;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private readonly Action _onRefreshNeeded;

        public TaskbarMessageListener(Action onRefreshNeeded)
        {
            _onRefreshNeeded = onRefreshNeeded;
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
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            DestroyHandle();
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
