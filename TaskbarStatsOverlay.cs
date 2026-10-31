using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CleanTB;

public class TaskbarStatsOverlay : Form
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
    private const int GWLP_HWNDPARENT = -8;

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

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        if (IntPtr.Size == 8)
            return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
        return new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
    }

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
    private readonly HardwareMonitor _monitor;
    private readonly ContextMenuStrip _contextMenu;
    private HardwareStats _latestStats;

    private bool _isDragging;
    private Point _dragStartCursor;
    private Point _dragStartForm;

    private readonly Font _fontRegular;
    private readonly Font _fontBold;

    // Cached GDI tools to eliminate per-second allocations
    private readonly SolidBrush _brushVal = new(Color.FromArgb(255, 255, 255));
    private readonly SolidBrush _brushLabel = new(Color.FromArgb(175, 180, 190));
    private readonly SolidBrush _brushNetDown = new(Color.FromArgb(0, 225, 255));
    private readonly SolidBrush _brushNetUp = new(Color.FromArgb(255, 185, 80));
    private readonly Pen _penDivider = new(Color.FromArgb(60, 255, 255, 255), 1f);
    private readonly StringFormat _sf = new(StringFormat.GenericTypographic)
    {
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces
    };

    private bool _isHiddenByFullscreen;
    private const int OVERLAY_HEIGHT = 26;

    public TaskbarStatsOverlay(AppConfig config, HardwareMonitor monitor, ContextMenuStrip contextMenu)
    {
        _config = config;
        _monitor = monitor;
        _contextMenu = contextMenu;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;

        // Owned by Shell_TrayWnd rather than global TopMost, ensuring it hides behind fullscreen apps
        TopMost = false;

        // Clean typography matching Windows 11 system tray clock font size exactly (9pt Segoe UI Variable)
        _fontRegular = TryCreateFont("Segoe UI Variable Text", 9.0f, FontStyle.Regular);
        _fontBold = TryCreateFont("Segoe UI Variable Text", 9.0f, FontStyle.Regular);

        Size = new Size(200, OVERLAY_HEIGHT);

        // Foreground window event hook: instantly hide/unhide when apps enter or exit fullscreen
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

        _monitor.StatsUpdated += stats =>
        {
            _latestStats = stats;
            if (IsHandleCreated && !IsDisposed)
            {
                try
                {
                    BeginInvoke(new Action(CheckVisibilityAndRender));
                }
                catch { }
            }
        };

        // Wire mouse interactions
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        DoubleClick += (s, e) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "taskmgr.exe",
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

    private static Font TryCreateFont(string familyName, float defaultSize, FontStyle style)
    {
        float size = defaultSize;
        try
        {
            if (SystemFonts.MenuFont != null && SystemFonts.MenuFont.SizeInPoints > 0)
            {
                size = SystemFonts.MenuFont.SizeInPoints;
            }
        }
        catch { }

        try
        {
            return new Font(familyName, size, style, GraphicsUnit.Point);
        }
        catch
        {
            return new Font("Segoe UI", size, style, GraphicsUnit.Point);
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00080000; // WS_EX_LAYERED (True per-pixel alpha rendering)
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW (Hidden from Alt+Tab)
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE (Does not steal focus)

            if (_config?.StatsClickThrough == true)
            {
                cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT (Click through to taskbar)
            }

            // Bind ownership directly to the Windows taskbar window
            IntPtr hTray = FindWindow("Shell_TrayWnd", null);
            if (hTray != IntPtr.Zero)
            {
                cp.Parent = hTray;
            }

            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        IntPtr hTray = FindWindow("Shell_TrayWnd", null);
        if (hTray != IntPtr.Zero)
        {
            SetWindowLongPtr(Handle, GWLP_HWNDPARENT, hTray);
        }
    }

    public void SetClickThrough(bool clickThrough)
    {
        _config.StatsClickThrough = clickThrough;
        _config.Save();
        if (IsHandleCreated)
        {
            RecreateHandle();
            CheckVisibilityAndRender();
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        CheckVisibilityAndRender();
        UpdateDockPosition(force: true);
    }

    public void CheckVisibilityAndRender()
    {
        if (!IsHandleCreated || IsDisposed) return;

        // 1. If stats overlay is disabled in settings, ensure hidden
        if (!_config.ShowHardwareStats)
        {
            if (IsWindowVisible(Handle))
            {
                ShowWindow(Handle, SW_HIDE);
            }
            return;
        }

        // 2. Check if primary taskbar is active and visible
        IntPtr hTray = FindWindow("Shell_TrayWnd", null);
        if (hTray == IntPtr.Zero || !IsWindowVisible(hTray))
        {
            if (IsWindowVisible(Handle))
            {
                ShowWindow(Handle, SW_HIDE);
            }
            return;
        }

        if (GetWindowRect(hTray, out RECT trayRect))
        {
            if (trayRect.Height <= 4)
            {
                if (IsWindowVisible(Handle))
                {
                    ShowWindow(Handle, SW_HIDE);
                }
                return;
            }

            IntPtr hMon = MonitorFromWindow(hTray, MONITOR_DEFAULTTONEAREST);
            MONITORINFO mi = new() { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                // Auto-hidden taskbar slides off screen, leaving only <= 4px peeking
                if (trayRect.Top >= mi.rcMonitor.Bottom - 4 || trayRect.Bottom <= mi.rcMonitor.Top + 4)
                {
                    if (IsWindowVisible(Handle))
                    {
                        ShowWindow(Handle, SW_HIDE);
                    }
                    return;
                }
            }
        }

        // 3. Fullscreen check: hide when a fullscreen app/game is active on the same monitor
        bool isFullscreen = IsFullscreenWindowActive();
        if (isFullscreen)
        {
            if (IsWindowVisible(Handle))
            {
                _isHiddenByFullscreen = true;
                ShowWindow(Handle, SW_HIDE);
            }
            return;
        }

        // 4. Restore visibility if coming back from fullscreen
        if (_isHiddenByFullscreen || !IsWindowVisible(Handle))
        {
            _isHiddenByFullscreen = false;
            ShowWindow(Handle, SW_SHOWNOACTIVATE);
            UpdateDockPosition(force: true);
        }

        UpdateDockPosition();
        RenderLayeredWindow();
    }

    private bool IsFullscreenWindowActive()
    {
        try
        {
            IntPtr hForeground = GetForegroundWindow();
            if (hForeground == IntPtr.Zero || hForeground == Handle)
            {
                return false;
            }

            // Exclude taskbars and desktop shell surfaces
            IntPtr hTray = FindWindow("Shell_TrayWnd", null);
            if (hForeground == hTray)
            {
                return false;
            }

            IntPtr hSecondaryTray = FindWindow("Shell_SecondaryTrayWnd", null);
            if (hForeground == hSecondaryTray)
            {
                return false;
            }

            IntPtr hProgman = FindWindow("Progman", null);
            IntPtr hWorkerW = FindWindow("WorkerW", null);
            if (hForeground == hProgman || hForeground == hWorkerW)
            {
                return false;
            }

            if (!IsWindowVisible(hForeground))
            {
                return false;
            }

            // Exclude cloaked background UWP processes
            if (DwmGetWindowAttribute(hForeground, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return false;
            }

            // Only hide if the fullscreen window is on the SAME monitor as the overlay
            IntPtr hMonFg = MonitorFromWindow(hForeground, MONITOR_DEFAULTTONEAREST);
            IntPtr hMonOverlay = MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST);

            if (hMonFg == IntPtr.Zero || hMonFg != hMonOverlay)
            {
                return false;
            }

            MONITORINFO mi = new() { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hMonFg, ref mi))
            {
                return false;
            }

            if (GetWindowRect(hForeground, out RECT rcFg))
            {
                // Fullscreen window covers the entire monitor rect (with 2px tolerance for borderless margins)
                if (rcFg.Left <= mi.rcMonitor.Left + 2 &&
                    rcFg.Top <= mi.rcMonitor.Top + 2 &&
                    rcFg.Right >= mi.rcMonitor.Right - 2 &&
                    rcFg.Bottom >= mi.rcMonitor.Bottom - 2)
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

        if (_config.StatsCustomX.HasValue && _config.StatsCustomY.HasValue)
        {
            Location = new Point(_config.StatsCustomX.Value, _config.StatsCustomY.Value);
            return;
        }

        IntPtr hTray = FindWindow("Shell_TrayWnd", null);
        if (hTray == IntPtr.Zero) return;

        if (GetWindowRect(hTray, out RECT trayRect))
        {
            IntPtr hNotify = FindWindowEx(hTray, IntPtr.Zero, "TrayNotifyWnd", null);
            int targetX;
            int targetY;

            if (hNotify != IntPtr.Zero && GetWindowRect(hNotify, out RECT notifyRect))
            {
                targetX = notifyRect.Left - Width - 12;
                targetY = trayRect.Top + (trayRect.Height - Height) / 2;
            }
            else
            {
                targetX = trayRect.Right - Width - 140;
                targetY = trayRect.Top + (trayRect.Height - Height) / 2;
            }

            if (Location.X != targetX || Location.Y != targetY)
            {
                Location = new Point(targetX, targetY);
            }
        }
    }

    public void ResetPosition()
    {
        _config.StatsCustomX = null;
        _config.StatsCustomY = null;
        _config.Save();
        UpdateDockPosition(force: true);
        CheckVisibilityAndRender();
    }

    public void RenderLayeredWindow()
    {
        if (!IsHandleCreated || IsDisposed) return;

        try
        {
            // Formatted metric strings
            string uploadStr = HardwareMonitor.FormatSpeed(_latestStats.UploadSpeedBps);
            string downloadStr = HardwareMonitor.FormatSpeed(_latestStats.DownloadSpeedBps);
            string cpuStr = $"{_latestStats.CpuPercent}%";
            string ramStr = $"{_latestStats.RamPercent}%";
            string gpuStr = $"{_latestStats.GpuPercent}%";

            int h = OVERLAY_HEIGHT;

            // 1. Measure exact layout width using temporary measuring bitmap
            float calcX = 2f;
            bool hasNet = _config.ShowUploadSpeed || _config.ShowDownloadOnly;

            using (var measureBmp = new Bitmap(1, 1))
            using (var gMeasure = Graphics.FromImage(measureBmp))
            {
                gMeasure.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                if (_config.ShowUploadSpeed)
                {
                    if (_config.ShowNetArrows)
                    {
                        calcX += gMeasure.MeasureString("▲ ", _fontBold, PointF.Empty, _sf).Width;
                    }
                    else
                    {
                        calcX += gMeasure.MeasureString("UP ", _fontBold, PointF.Empty, _sf).Width;
                    }
                    calcX += gMeasure.MeasureString(uploadStr, _fontRegular, PointF.Empty, _sf).Width + 8f;
                }

                if (_config.ShowDownloadOnly)
                {
                    if (_config.ShowNetArrows)
                    {
                        calcX += gMeasure.MeasureString("▼ ", _fontBold, PointF.Empty, _sf).Width;
                    }
                    else if (_config.ShowUploadSpeed)
                    {
                        calcX += gMeasure.MeasureString("DN ", _fontBold, PointF.Empty, _sf).Width;
                    }
                    calcX += gMeasure.MeasureString(downloadStr, _fontRegular, PointF.Empty, _sf).Width + 8f;
                }

                if (hasNet && (_config.ShowCpu || _config.ShowRam || _config.ShowGpu))
                {
                    calcX += 12f; // divider spacing
                }

                if (_config.ShowCpu)
                {
                    calcX += gMeasure.MeasureString("CPU ", _fontBold, PointF.Empty, _sf).Width;
                    calcX += gMeasure.MeasureString(cpuStr, _fontRegular, PointF.Empty, _sf).Width + 8f;
                }

                if (_config.ShowRam)
                {
                    calcX += gMeasure.MeasureString("RAM ", _fontBold, PointF.Empty, _sf).Width;
                    calcX += gMeasure.MeasureString(ramStr, _fontRegular, PointF.Empty, _sf).Width + 8f;
                }

                if (_config.ShowGpu)
                {
                    calcX += gMeasure.MeasureString("GPU ", _fontBold, PointF.Empty, _sf).Width;
                    calcX += gMeasure.MeasureString(gpuStr, _fontRegular, PointF.Empty, _sf).Width + 4f;
                }
            }

            int neededWidth = Math.Max((int)Math.Ceiling(calcX) + 4, 40);
            if (Width != neededWidth)
            {
                Width = neededWidth;
            }

            int w = Width;

            // 2. Render to 32bpp alpha bitmap
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                // 100% transparent background - NO black tint, NO pill, NO box
                g.Clear(Color.Transparent);

                float currentX = 2f;
                float rowY = 5f;

                // --- Network Activity ---
                if (_config.ShowUploadSpeed)
                {
                    if (_config.ShowNetArrows)
                    {
                        g.DrawString("▲", _fontBold, _brushNetUp, currentX, rowY, _sf);
                        currentX += g.MeasureString("▲ ", _fontBold, PointF.Empty, _sf).Width;
                    }
                    else
                    {
                        g.DrawString("UP", _fontBold, _brushNetUp, currentX, rowY, _sf);
                        currentX += g.MeasureString("UP ", _fontBold, PointF.Empty, _sf).Width;
                    }

                    g.DrawString(uploadStr, _fontRegular, _brushVal, currentX, rowY, _sf);
                    currentX += g.MeasureString(uploadStr, _fontRegular, PointF.Empty, _sf).Width + 8f;
                }

                if (_config.ShowDownloadOnly)
                {
                    if (_config.ShowNetArrows)
                    {
                        g.DrawString("▼", _fontBold, _brushNetDown, currentX, rowY, _sf);
                        currentX += g.MeasureString("▼ ", _fontBold, PointF.Empty, _sf).Width;
                    }
                    else if (_config.ShowUploadSpeed)
                    {
                        g.DrawString("DN", _fontBold, _brushNetDown, currentX, rowY, _sf);
                        currentX += g.MeasureString("DN ", _fontBold, PointF.Empty, _sf).Width;
                    }

                    g.DrawString(downloadStr, _fontRegular, _brushVal, currentX, rowY, _sf);
                    currentX += g.MeasureString(downloadStr, _fontRegular, PointF.Empty, _sf).Width + 8f;
                }

                if (hasNet && (_config.ShowCpu || _config.ShowRam || _config.ShowGpu))
                {
                    g.DrawLine(_penDivider, currentX + 1f, rowY + 2f, currentX + 1f, rowY + 14f);
                    currentX += 12f;
                }

                // --- CPU Usage ---
                if (_config.ShowCpu)
                {
                    g.DrawString("CPU", _fontBold, _brushLabel, currentX, rowY, _sf);
                    currentX += g.MeasureString("CPU ", _fontBold, PointF.Empty, _sf).Width;

                    g.DrawString(cpuStr, _fontRegular, _brushVal, currentX, rowY, _sf);
                    currentX += g.MeasureString(cpuStr, _fontRegular, PointF.Empty, _sf).Width + 8f;
                }

                // --- RAM Usage ---
                if (_config.ShowRam)
                {
                    g.DrawString("RAM", _fontBold, _brushLabel, currentX, rowY, _sf);
                    currentX += g.MeasureString("RAM ", _fontBold, PointF.Empty, _sf).Width;

                    g.DrawString(ramStr, _fontRegular, _brushVal, currentX, rowY, _sf);
                    currentX += g.MeasureString(ramStr, _fontRegular, PointF.Empty, _sf).Width + 8f;
                }

                // --- GPU Usage ---
                if (_config.ShowGpu)
                {
                    g.DrawString("GPU", _fontBold, _brushLabel, currentX, rowY, _sf);
                    currentX += g.MeasureString("GPU ", _fontBold, PointF.Empty, _sf).Width;

                    g.DrawString(gpuStr, _fontRegular, _brushVal, currentX, rowY, _sf);
                    currentX += g.MeasureString(gpuStr, _fontRegular, PointF.Empty, _sf).Width + 4f;
                }
            }

            // 3. Update layered window with true 32bpp alpha bitmap
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
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
            _config.StatsCustomX = Location.X;
            _config.StatsCustomY = Location.Y;
            _config.Save();
            RenderLayeredWindow();
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
            _fontBold?.Dispose();
            _brushVal?.Dispose();
            _brushLabel?.Dispose();
            _brushNetDown?.Dispose();
            _brushNetUp?.Dispose();
            _penDivider?.Dispose();
            _sf?.Dispose();
        }
        base.Dispose(disposing);
    }
}
