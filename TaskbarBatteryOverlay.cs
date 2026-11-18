using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CleanTB;

public class TaskbarBatteryOverlay : Form
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
        public POINT(int x, int y) { this.x = x; this.y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
        public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const uint ULW_ALPHA = 0x02;

    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int DWMWA_CLOAKED = 14;

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd,
        IntPtr hdcDst,
        ref POINT pptDst,
        ref SIZE psize,
        IntPtr hdcSrc,
        ref POINT pptSrc,
        uint crKey,
        [In] ref BLENDFUNCTION pblend,
        uint dwFlags
    );

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags
    );

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    private readonly WinEventDelegate _winEventProc;
    private IntPtr _hWinEventHook = IntPtr.Zero;

    private readonly AppConfig _config;
    private readonly PeripheralBatteryMonitor _batteryMonitor;
    private readonly ContextMenuStrip _contextMenu;
    private TaskbarStatsOverlay? _statsOverlay;

    private bool _isDragging;
    private Point _dragStartCursor;
    private Point _dragStartForm;

    private Font _fontRegular = null!;
    private Font _fontGlyph = null!;
    private float _dpiScale = 1.0f;
    private float _fontLineHeight = 16f;

    private float _percentBoxW;
    private float _glyphBoxW;

    private Bitmap? _cachedBmp;
    private Graphics? _cachedG;

    private readonly SolidBrush _brushVal = new(Color.FromArgb(255, 255, 255));
    private readonly SolidBrush _brushLabel = new(Color.FromArgb(175, 180, 190));
    private readonly SolidBrush _brushShadow = new(Color.FromArgb(90, 0, 0, 0));
    private readonly StringFormat _sf = new(StringFormat.GenericTypographic)
    {
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces
    };
    private readonly StringFormat _sfRight = new(StringFormat.GenericTypographic)
    {
        Alignment = StringAlignment.Far,
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces
    };

    private bool _isHiddenByFullscreen;

    public TaskbarBatteryOverlay(AppConfig config, PeripheralBatteryMonitor batteryMonitor, ContextMenuStrip contextMenu)
    {
        _config = config;
        _batteryMonitor = batteryMonitor;
        _contextMenu = contextMenu;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;

        RecomputeDpiAndDimensions();

        Size = new Size(120, Math.Max(26, (int)Math.Ceiling(_fontLineHeight + 8f)));

        // Create window handle immediately so BeginInvoke and Win32 messages work reliably from start
        CreateControl();

        _winEventProc = (hHook, evt, hwnd, idObj, idChild, thread, time) =>
        {
            if (IsHandleCreated && !IsDisposed)
            {
                try
                {
                    BeginInvoke(new Action(CheckVisibilityAndRender));
                }
                catch { }
            }
        };

        _hWinEventHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _winEventProc,
            0,
            0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS
        );

        _batteryMonitor.BatteryUpdated += devices =>
        {
            if (IsDisposed) return;
            try
            {
                if (IsHandleCreated)
                {
                    BeginInvoke(new Action(CheckVisibilityAndRender));
                }
                else
                {
                    CheckVisibilityAndRender();
                }
            }
            catch { }
        };

        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;

        DoubleClick += (s, e) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-settings:bluetooth",
                    UseShellExecute = true
                });
            }
            catch { }
        };

        MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                _contextMenu.Show(Cursor.Position);
            }
        };
    }

    public void SetStatsOverlay(TaskbarStatsOverlay statsOverlay)
    {
        _statsOverlay = statsOverlay;
    }

    private static Font CreateCleanFont(float size, FontStyle style)
    {
        string[] candidates = { "Segoe UI Variable Small", "Segoe UI Variable Text", "Segoe UI" };
        foreach (var family in candidates)
        {
            try
            {
                using var test = new FontFamily(family);
                if (string.Equals(test.Name, family, StringComparison.OrdinalIgnoreCase))
                {
                    return new Font(family, size, style, GraphicsUnit.Point);
                }
            }
            catch { }
        }
        return new Font("Segoe UI", size, style, GraphicsUnit.Point);
    }

    private static Font CreateGlyphFont(float size)
    {
        string[] candidates = { "Segoe Fluent Icons", "Segoe MDL2 Assets" };
        foreach (var family in candidates)
        {
            try
            {
                using var test = new FontFamily(family);
                if (string.Equals(test.Name, family, StringComparison.OrdinalIgnoreCase))
                {
                    return new Font(family, size, FontStyle.Regular, GraphicsUnit.Point);
                }
            }
            catch { }
        }
        return new Font("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Point);
    }

    private void RecomputeDpiAndDimensions()
    {
        uint dpi = 96;
        if (IsHandleCreated)
        {
            try
            {
                dpi = GetDpiForWindow(Handle);
            }
            catch { }
        }
        if (dpi == 0) dpi = 96;
        _dpiScale = Math.Max(0.5f, dpi / 96.0f);

        _fontRegular?.Dispose();
        _fontGlyph?.Dispose();

        const float baseFontSize = 9.0f;
        const float glyphFontSize = 9.0f;

        _fontRegular = CreateCleanFont(baseFontSize, FontStyle.Regular);
        _fontGlyph = CreateGlyphFont(glyphFontSize);

        using var measureBmp = new Bitmap(1, 1);
        using var gMeasure = Graphics.FromImage(measureBmp);
        gMeasure.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        _fontLineHeight = _fontRegular.GetHeight(gMeasure);

        _percentBoxW = Math.Max(gMeasure.MeasureString("100%", _fontRegular, PointF.Empty, _sf).Width + 2f * _dpiScale, 28f * _dpiScale);
        _glyphBoxW = Math.Max(gMeasure.MeasureString("\uE7F6", _fontGlyph, PointF.Empty, _sf).Width, 14f * _dpiScale);
    }

    private void DrawShadowText(Graphics g, string text, Font font, Brush brush, float x, float y, StringFormat format)
    {
        g.DrawString(text, font, _brushShadow, x + 1f, y + 1f, format);
        g.DrawString(text, font, brush, x, y, format);
    }

    private void DrawShadowText(Graphics g, string text, Font font, Brush brush, RectangleF rect, StringFormat format)
    {
        var shadowRect = new RectangleF(rect.X + 1f, rect.Y + 1f, rect.Width, rect.Height);
        g.DrawString(text, font, _brushShadow, shadowRect, format);
        g.DrawString(text, font, brush, rect, format);
    }

    private const int WM_DPICHANGED = 0x02E0;
    private const int WM_DEVICECHANGE = 0x0219;

    public bool IsOverlayVisible => IsHandleCreated && IsWindowVisible(Handle);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DPICHANGED)
        {
            RecomputeDpiAndDimensions();
            UpdateDockPosition(force: true);
            RenderLayeredWindow();
        }
        else if (m.Msg == WM_DEVICECHANGE)
        {
            _batteryMonitor.ForceRefresh();
        }
        base.WndProc(ref m);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE

            if (_config?.StatsClickThrough == true)
            {
                cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT
            }

            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RecomputeDpiAndDimensions();
    }

    public void SetClickThrough(bool clickThrough)
    {
        if (IsHandleCreated)
        {
            RecreateHandle();
            CheckVisibilityAndRender();
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        UpdateDockPosition(force: true);
        CheckVisibilityAndRender();
    }

    public bool HasActiveBatteryDevices()
    {
        if (!_config.ShowBatteryStats || _batteryMonitor == null) return false;
        var devices = _batteryMonitor.GetCurrentDevices();
        foreach (var d in devices)
        {
            if (!d.IsConnected) continue;
            bool ok = d.Type switch
            {
                PeripheralType.Headphones => _config.ShowBatteryHeadphones,
                PeripheralType.Mouse => _config.ShowBatteryMouse,
                PeripheralType.Keyboard => _config.ShowBatteryKeyboard,
                PeripheralType.Controller => _config.ShowBatteryController,
                _ => true
            };
            if (ok) return true;
        }
        return false;
    }

    public void CheckVisibilityAndRender()
    {
        if (IsDisposed) return;

        bool shouldShow = HasActiveBatteryDevices();

        if (!IsHandleCreated)
        {
            CreateControl();
        }

        if (!shouldShow)
        {
            if (IsOverlayVisible)
            {
                ShowWindow(Handle, SW_HIDE);
                _statsOverlay?.UpdateDockPosition(force: true);
                _statsOverlay?.CheckVisibilityAndRender();
            }
            return;
        }

        bool isFullscreen = IsFullscreenWindowActive();
        if (isFullscreen)
        {
            if (IsOverlayVisible)
            {
                _isHiddenByFullscreen = true;
                ShowWindow(Handle, SW_HIDE);
            }
            return;
        }

        IntPtr hTray = FindWindow("Shell_TrayWnd", null);
        if (hTray != IntPtr.Zero && GetWindowRect(hTray, out RECT trayRect))
        {
            if (trayRect.Height <= 4)
            {
                if (IsOverlayVisible) ShowWindow(Handle, SW_HIDE);
                return;
            }

            IntPtr hMon = MonitorFromWindow(hTray, MONITOR_DEFAULTTONEAREST);
            MONITORINFO mi = new() { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                if (trayRect.Top >= mi.rcMonitor.Bottom - 4 || trayRect.Bottom <= mi.rcMonitor.Top + 4)
                {
                    if (IsOverlayVisible) ShowWindow(Handle, SW_HIDE);
                    return;
                }
            }
        }

        if (_isHiddenByFullscreen || !IsOverlayVisible)
        {
            _isHiddenByFullscreen = false;
            ShowWindow(Handle, SW_SHOWNOACTIVATE);
            UpdateDockPosition(force: true);
        }

        UpdateDockPosition();
        RenderLayeredWindow();
        _statsOverlay?.UpdateDockPosition(force: true);
    }

    private bool IsFullscreenWindowActive()
    {
        try
        {
            IntPtr hForeground = GetForegroundWindow();
            if (hForeground == IntPtr.Zero || hForeground == Handle) return false;

            IntPtr hTray = FindWindow("Shell_TrayWnd", null);
            if (hForeground == hTray) return false;

            IntPtr hSecondaryTray = FindWindow("Shell_SecondaryTrayWnd", null);
            if (hForeground == hSecondaryTray) return false;

            IntPtr hProgman = FindWindow("Progman", null);
            IntPtr hWorkerW = FindWindow("WorkerW", null);
            if (hForeground == hProgman || hForeground == hWorkerW) return false;

            if (!IsWindowVisible(hForeground)) return false;

            if (DwmGetWindowAttribute(hForeground, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return false;

            IntPtr hMonFg = MonitorFromWindow(hForeground, MONITOR_DEFAULTTONEAREST);
            IntPtr hMonOverlay = MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST);
            if (hMonFg == IntPtr.Zero || hMonFg != hMonOverlay) return false;

            MONITORINFO mi = new() { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hMonFg, ref mi)) return false;

            if (GetWindowRect(hForeground, out RECT rcFg))
            {
                if (rcFg.Left <= mi.rcMonitor.Left &&
                    rcFg.Top <= mi.rcMonitor.Top &&
                    rcFg.Right >= mi.rcMonitor.Right &&
                    rcFg.Bottom >= mi.rcMonitor.Bottom)
                {
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    public void UpdateDockPosition(bool force = false)
    {
        if (_isDragging && !force) return;

        if (_config.BatteryCustomX.HasValue && _config.BatteryCustomY.HasValue)
        {
            Point customPt = new Point(_config.BatteryCustomX.Value, _config.BatteryCustomY.Value);
            bool onScreen = false;
            foreach (var screen in Screen.AllScreens)
            {
                if (screen.Bounds.Contains(customPt))
                {
                    onScreen = true;
                    break;
                }
            }

            if (onScreen)
            {
                Location = customPt;
                return;
            }
            else
            {
                _config.BatteryCustomX = null;
                _config.BatteryCustomY = null;
                _config.Save();
            }
        }

        IntPtr hTray = FindWindow("Shell_TrayWnd", null);
        if (hTray == IntPtr.Zero) return;

        if (GetWindowRect(hTray, out RECT trayRect))
        {
            int calculatedHeight = Math.Min(Math.Max(trayRect.Height - 4, 20), Math.Max((int)Math.Round(26f * _dpiScale), (int)Math.Ceiling(_fontLineHeight + 8f)));
            if (Height != calculatedHeight)
            {
                Height = calculatedHeight;
            }

            int targetX;
            int targetY = trayRect.Top + (trayRect.Height - Height) / 2;

            if (_config.BatteryPosition == StatsPosition.Left)
            {
                // If hardware stats is also on the left and visible (and not custom-dragged), dock immediately to its right
                if (_statsOverlay != null && _statsOverlay.IsOverlayVisible && _config.StatsPosition == StatsPosition.Left && !_config.StatsCustomX.HasValue)
                {
                    targetX = _statsOverlay.Location.X + _statsOverlay.Width + (int)Math.Round(8f * _dpiScale);
                }
                else
                {
                    int leftOffset = (int)Math.Round((IsWidgetsOnLeft() ? 165f : 16f) * _dpiScale);
                    targetX = trayRect.Left + leftOffset;
                }
            }
            else
            {
                // Dock to Right side (directly next to System Tray)
                IntPtr hNotify = FindWindowEx(hTray, IntPtr.Zero, "TrayNotifyWnd", null);
                if (hNotify != IntPtr.Zero && GetWindowRect(hNotify, out RECT notifyRect))
                {
                    targetX = notifyRect.Left - Width - (int)Math.Round(12f * _dpiScale);
                }
                else
                {
                    targetX = trayRect.Right - Width - (int)Math.Round(140f * _dpiScale);
                }
            }

            if (Location.X != targetX || Location.Y != targetY)
            {
                Location = new Point(targetX, targetY);
            }
        }
    }

    private static bool IsWidgetsOnLeft()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            if (key != null)
            {
                object? alObj = key.GetValue("TaskbarAl");
                object? daObj = key.GetValue("TaskbarDa");
                int taskbarAl = alObj is int al ? al : 1;
                int taskbarDa = daObj is int da ? da : 0;
                return taskbarAl == 1 && taskbarDa == 1;
            }
        }
        catch { }
        return false;
    }

    public void SetDockPosition(StatsPosition position)
    {
        _config.BatteryPosition = position;
        _config.BatteryCustomX = null;
        _config.BatteryCustomY = null;
        _config.Save();
        UpdateDockPosition(force: true);
        CheckVisibilityAndRender();
        _statsOverlay?.UpdateDockPosition(force: true);
        _statsOverlay?.CheckVisibilityAndRender();
    }

    public void ResetPosition()
    {
        _config.BatteryCustomX = null;
        _config.BatteryCustomY = null;
        _config.Save();
        UpdateDockPosition(force: true);
        CheckVisibilityAndRender();
        _statsOverlay?.UpdateDockPosition(force: true);
        _statsOverlay?.CheckVisibilityAndRender();
    }

    public void RenderLayeredWindow()
    {
        if (!IsHandleCreated || IsDisposed) return;

        try
        {
            var batteryItems = new List<(string glyph, string text, float glyphX, float valX)>();
            if (_config.ShowBatteryStats && _batteryMonitor != null)
            {
                var devices = _batteryMonitor.GetCurrentDevices();
                foreach (var d in devices)
                {
                    if (!d.IsConnected) continue;
                    bool include = d.Type switch
                    {
                        PeripheralType.Headphones => _config.ShowBatteryHeadphones,
                        PeripheralType.Mouse => _config.ShowBatteryMouse,
                        PeripheralType.Keyboard => _config.ShowBatteryKeyboard,
                        PeripheralType.Controller => _config.ShowBatteryController,
                        _ => true
                    };
                    if (!include) continue;

                    string glyph = d.Type switch
                    {
                        PeripheralType.Headphones => "\uE7F6",
                        PeripheralType.Mouse => "\uE962",
                        PeripheralType.Keyboard => "\uE765",
                        PeripheralType.Controller => "\uE7FC",
                        _ => "\uE83F"
                    };

                    string batText = d.BatteryPercent >= 0 ? $"{d.BatteryPercent}%" : "ON";
                    batteryItems.Add((glyph, batText, 0f, 0f));
                }
            }

            if (batteryItems.Count == 0)
            {
                if (Visible) ShowWindow(Handle, SW_HIDE);
                return;
            }

            int h = Height;
            float percentBoxW = _percentBoxW;
            float labelGap = 3f * _dpiScale;
            float moduleGap = 8f * _dpiScale;

            float curX = 2f;
            for (int i = 0; i < batteryItems.Count; i++)
            {
                var item = batteryItems[i];
                float gX = curX;
                float vX = gX + _glyphBoxW + labelGap;
                batteryItems[i] = (item.glyph, item.text, gX, vX);
                curX = vX + percentBoxW + moduleGap;
            }

            int neededWidth = Math.Max((int)Math.Ceiling(curX) + 2, 20);
            if (Width != neededWidth)
            {
                Width = neededWidth;
                UpdateDockPosition(force: true);
                _statsOverlay?.UpdateDockPosition(force: true);
            }

            int w = Width;

            if (_cachedBmp == null || _cachedBmp.Width != w || _cachedBmp.Height != h)
            {
                _cachedG?.Dispose();
                _cachedBmp?.Dispose();
                _cachedBmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                _cachedG = Graphics.FromImage(_cachedBmp);
                _cachedG.SmoothingMode = SmoothingMode.AntiAlias;
                _cachedG.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            }

            if (_cachedG == null || _cachedBmp == null) return;
            var g = _cachedG;

            g.Clear(Color.Transparent);

            float rowY = Math.Max(1f, (h - _fontLineHeight) / 2f);
            float boxH = Math.Max(_fontLineHeight + 4f, 20f * _dpiScale);

            // Vertical offset to dead-center align Segoe Fluent Icons with Segoe UI text digits
            float glyphOffsetY = (float)Math.Round(3.0f * _dpiScale);
            float glyphY = rowY + glyphOffsetY;

            foreach (var b in batteryItems)
            {
                DrawShadowText(g, b.glyph, _fontGlyph, _brushLabel, b.glyphX, glyphY, _sf);
                var rectBat = new RectangleF(b.valX, rowY, percentBoxW, boxH);
                DrawShadowText(g, b.text, _fontRegular, _brushVal, rectBat, _sfRight);
            }

            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = _cachedBmp.GetHbitmap(Color.FromArgb(0));
            IntPtr oldBitmap = SelectObject(memDc, hBitmap);
            try
            {
                POINT ptSrc = new(0, 0);
                SIZE size = new(w, h);
                POINT ptDst = new(Location.X, Location.Y);
                BLENDFUNCTION blend = new()
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA
                };

                UpdateLayeredWindow(
                    Handle,
                    screenDc,
                    ref ptDst,
                    ref size,
                    memDc,
                    ref ptSrc,
                    0,
                    ref blend,
                    ULW_ALPHA
                );
            }
            finally
            {
                SelectObject(memDc, oldBitmap);
                DeleteObject(hBitmap);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
        catch { }
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && !_config.StatsClickThrough)
        {
            _isDragging = true;
            _dragStartCursor = Cursor.Position;
            _dragStartForm = Location;
        }
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            Point diff = Point.Subtract(Cursor.Position, new Size(_dragStartCursor));
            Location = Point.Add(_dragStartForm, new Size(diff));
            RenderLayeredWindow();
        }
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            _config.BatteryCustomX = Location.X;
            _config.BatteryCustomY = Location.Y;
            _config.Save();
            RenderLayeredWindow();
            _statsOverlay?.UpdateDockPosition(force: true);
            _statsOverlay?.CheckVisibilityAndRender();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_hWinEventHook != IntPtr.Zero)
            {
                UnhookWinEvent(_hWinEventHook);
                _hWinEventHook = IntPtr.Zero;
            }

            _fontRegular?.Dispose();
            _fontGlyph?.Dispose();
            _brushVal?.Dispose();
            _brushLabel?.Dispose();
            _brushShadow?.Dispose();
            _sf?.Dispose();
            _sfRight?.Dispose();
            _cachedG?.Dispose();
            _cachedBmp?.Dispose();
        }
        base.Dispose(disposing);
    }
}
