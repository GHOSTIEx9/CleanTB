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

    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const uint ULW_ALPHA = 0x02;

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

    private readonly AppConfig _config;
    private readonly HardwareMonitor _monitor;
    private readonly ContextMenuStrip _contextMenu;
    private HardwareStats _latestStats;

    private bool _isDragging;
    private Point _dragStartCursor;
    private Point _dragStartForm;

    private readonly Font _fontRegular;
    private readonly Font _fontBold;

    private const int OVERLAY_HEIGHT = 26;

    public TaskbarStatsOverlay(AppConfig config, HardwareMonitor monitor, ContextMenuStrip contextMenu)
    {
        _config = config;
        _monitor = monitor;
        _contextMenu = contextMenu;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;

        // Modern Fluent typography - 8.5pt matches the Windows 11 clock and tray font size
        _fontRegular = TryCreateFont("Segoe UI Variable Text", 8.5f, FontStyle.Regular);
        _fontBold = TryCreateFont("Segoe UI Variable Text", 8.5f, FontStyle.Bold);

        Size = new Size(320, OVERLAY_HEIGHT);

        _monitor.StatsUpdated += stats =>
        {
            _latestStats = stats;
            if (IsHandleCreated && !IsDisposed)
            {
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        RenderLayeredWindow();
                        UpdateDockPosition();
                    }));
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

    private static Font TryCreateFont(string familyName, float size, FontStyle style)
    {
        try
        {
            return new Font(familyName, size, style);
        }
        catch
        {
            return new Font("Segoe UI", size, style);
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
                cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT (Clicks pass through)
            }

            return cp;
        }
    }

    public void SetClickThrough(bool clickThrough)
    {
        _config.StatsClickThrough = clickThrough;
        _config.Save();
        if (IsHandleCreated)
        {
            RecreateHandle();
            RenderLayeredWindow();
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        RenderLayeredWindow();
        UpdateDockPosition(force: true);
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
        RenderLayeredWindow();
    }

    public void RenderLayeredWindow()
    {
        if (!IsHandleCreated || IsDisposed) return;

        // Formatted strings
        string uploadStr = HardwareMonitor.FormatSpeed(_latestStats.UploadSpeedBps);
        string downloadStr = HardwareMonitor.FormatSpeed(_latestStats.DownloadSpeedBps);
        string cpuStr = $"{_latestStats.CpuPercent}%";
        string ramStr = $"{_latestStats.RamPercent}%";
        string gpuStr = $"{_latestStats.GpuPercent}%";

        // Modern Windows 11 high-contrast palette
        Color colNetUp = Color.FromArgb(255, 185, 80);       // Warm amber/orange
        Color colNetDown = Color.FromArgb(0, 225, 255);      // Clean crisp cyan
        Color colLabel = Color.FromArgb(175, 180, 190);     // Soft silver-gray (matches Windows 11 system font)
        Color colVal = Color.FromArgb(255, 255, 255);       // Pure crisp white
        Color colDivider = Color.FromArgb(60, 255, 255, 255); // Subtle translucent divider

        // 1. Measure all components to dynamically fit width
        int currentX = 0;
        int rowY = 4;
        int h = OVERLAY_HEIGHT;

        // Temporary graphics to measure
        int neededWidth = 0;
        using (var measureBmp = new Bitmap(1, 1))
        using (var gMeasure = Graphics.FromImage(measureBmp))
        {
            if (_config.ShowNetSpeed)
            {
                neededWidth += TextRenderer.MeasureText(gMeasure, "▲ " + uploadStr, _fontRegular, Size.Empty, TextFormatFlags.NoPadding).Width + 10;
                neededWidth += TextRenderer.MeasureText(gMeasure, "▼ " + downloadStr, _fontRegular, Size.Empty, TextFormatFlags.NoPadding).Width + 12;
                neededWidth += 12; // divider spacing
            }

            if (_config.ShowCpu)
            {
                neededWidth += TextRenderer.MeasureText(gMeasure, "CPU " + cpuStr, _fontRegular, Size.Empty, TextFormatFlags.NoPadding).Width + 10;
            }

            if (_config.ShowRam)
            {
                neededWidth += TextRenderer.MeasureText(gMeasure, "RAM " + ramStr, _fontRegular, Size.Empty, TextFormatFlags.NoPadding).Width + 10;
            }

            if (_config.ShowGpu)
            {
                neededWidth += TextRenderer.MeasureText(gMeasure, "GPU " + gpuStr, _fontRegular, Size.Empty, TextFormatFlags.NoPadding).Width + 6;
            }
        }

        neededWidth = Math.Max(neededWidth + 4, 100);
        if (Width != neededWidth)
        {
            Width = neededWidth;
        }

        int w = Width;

        // 2. Render to 32-bit ARGB bitmap (100% transparent background - ZERO pink, ZERO black tint)
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // Completely transparent background - NO background box, NO pill, NO tint!
            g.Clear(Color.Transparent);

            currentX = 2;

            // --- Network Speed: ▲ Upload ▼ Download ---
            if (_config.ShowNetSpeed)
            {
                // Upload ▲
                TextRenderer.DrawText(g, "▲", _fontBold, new Point(currentX, rowY), colNetUp, TextFormatFlags.NoPadding);
                currentX += TextRenderer.MeasureText(g, "▲ ", _fontBold, Size.Empty, TextFormatFlags.NoPadding).Width;

                TextRenderer.DrawText(g, uploadStr, _fontRegular, new Point(currentX, rowY), colVal, TextFormatFlags.NoPadding);
                currentX += TextRenderer.MeasureText(g, uploadStr, _fontRegular, Size.Empty, TextFormatFlags.NoPadding).Width + 10;

                // Download ▼
                TextRenderer.DrawText(g, "▼", _fontBold, new Point(currentX, rowY), colNetDown, TextFormatFlags.NoPadding);
                currentX += TextRenderer.MeasureText(g, "▼ ", _fontBold, Size.Empty, TextFormatFlags.NoPadding).Width;

                TextRenderer.DrawText(g, downloadStr, _fontRegular, new Point(currentX, rowY), colVal, TextFormatFlags.NoPadding);
                currentX += TextRenderer.MeasureText(g, downloadStr, _fontRegular, Size.Empty, TextFormatFlags.NoPadding).Width + 10;

                // Subtle vertical divider line
                using (var pen = new Pen(colDivider, 1f))
                {
                    g.DrawLine(pen, currentX, rowY + 2, currentX, rowY + 14);
                }
                currentX += 12;
            }

            // --- CPU Usage ---
            if (_config.ShowCpu)
            {
                TextRenderer.DrawText(g, "CPU", _fontBold, new Point(currentX, rowY), colLabel, TextFormatFlags.NoPadding);
                currentX += TextRenderer.MeasureText(g, "CPU ", _fontBold, Size.Empty, TextFormatFlags.NoPadding).Width;

                TextRenderer.DrawText(g, cpuStr, _fontRegular, new Point(currentX, rowY), colVal, TextFormatFlags.NoPadding);
                currentX += TextRenderer.MeasureText(g, cpuStr, _fontRegular, Size.Empty, TextFormatFlags.NoPadding).Width + 10;
            }

            // --- RAM Usage ---
            if (_config.ShowRam)
            {
                TextRenderer.DrawText(g, "RAM", _fontBold, new Point(currentX, rowY), colLabel, TextFormatFlags.NoPadding);
                currentX += TextRenderer.MeasureText(g, "RAM ", _fontBold, Size.Empty, TextFormatFlags.NoPadding).Width;

                TextRenderer.DrawText(g, ramStr, _fontRegular, new Point(currentX, rowY), colVal, TextFormatFlags.NoPadding);
                currentX += TextRenderer.MeasureText(g, ramStr, _fontRegular, Size.Empty, TextFormatFlags.NoPadding).Width + 10;
            }

            // --- GPU Usage ---
            if (_config.ShowGpu)
            {
                TextRenderer.DrawText(g, "GPU", _fontBold, new Point(currentX, rowY), colLabel, TextFormatFlags.NoPadding);
                currentX += TextRenderer.MeasureText(g, "GPU ", _fontBold, Size.Empty, TextFormatFlags.NoPadding).Width;

                TextRenderer.DrawText(g, gpuStr, _fontRegular, new Point(currentX, rowY), colVal, TextFormatFlags.NoPadding);
            }
        }

        // Apply true 32-bit ARGB bitmap to the layered window
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
            _fontRegular?.Dispose();
            _fontBold?.Dispose();
        }
        base.Dispose(disposing);
    }
}
