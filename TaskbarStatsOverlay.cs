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

    private const int OVERLAY_WIDTH = 156;
    private const int OVERLAY_HEIGHT = 34;

    public TaskbarStatsOverlay(AppConfig config, HardwareMonitor monitor, ContextMenuStrip contextMenu)
    {
        _config = config;
        _monitor = monitor;
        _contextMenu = contextMenu;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Size = new Size(OVERLAY_WIDTH, OVERLAY_HEIGHT);

        // Modern fonts matching Windows 11 Fluent typography
        _fontRegular = TryCreateFont("Segoe UI Variable Text", 8.0f, FontStyle.Regular);
        _fontBold = TryCreateFont("Segoe UI Variable Text", 8.0f, FontStyle.Bold);

        _monitor.StatsUpdated += stats =>
        {
            _latestStats = stats;
            if (IsHandleCreated && !IsDisposed)
            {
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        UpdateDockPosition();
                        RenderLayeredWindow();
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
        UpdateDockPosition(force: true);
        RenderLayeredWindow();
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
                targetX = notifyRect.Left - Width - 10;
                targetY = trayRect.Top + (trayRect.Height - Height) / 2;
            }
            else
            {
                targetX = trayRect.Right - Width - 120;
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

        int w = Width;
        int h = Height;
        if (w <= 0 || h <= 0) return;

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // Draw clean frosted dark pill container (Zero pink, true alpha blending)
            var rect = new Rectangle(0, 0, w - 1, h - 1);
            using (var path = GetRoundedRectPath(rect, 6))
            {
                // Fluent dark translucent background (Alpha 175 = ~68% opacity)
                using (var bgBrush = new SolidBrush(Color.FromArgb(175, 18, 18, 22)))
                {
                    g.FillPath(bgBrush, path);
                }

                // Subtle modern border (Alpha 35 = ~14% subtle white outline)
                using (var borderPen = new Pen(Color.FromArgb(45, 255, 255, 255), 1f))
                {
                    g.DrawPath(borderPen, path);
                }
            }

            // Draw subtle vertical column divider
            using (var divPen = new Pen(Color.FromArgb(30, 255, 255, 255), 1f))
            {
                g.DrawLine(divPen, 76, 5, 76, h - 6);
            }

            // Formatted values
            string uploadStr = HardwareMonitor.FormatSpeed(_latestStats.UploadSpeedBps);
            string downloadStr = HardwareMonitor.FormatSpeed(_latestStats.DownloadSpeedBps);
            string cpuStr = $"{_latestStats.CpuPercent}%";
            string ramStr = $"{_latestStats.RamPercent}%";

            // Palette
            Color colNetUp = Color.FromArgb(255, 175, 75);      // Warm glowing amber
            Color colNetDown = Color.FromArgb(0, 215, 255);     // Crisp vivid cyan
            Color colLabel = Color.FromArgb(175, 180, 190);    // Clean Fluent muted gray
            Color colVal = Color.FromArgb(250, 250, 250);      // High-contrast clean white

            // Row vertical centers
            int row1Y = 2;
            int row2Y = 17;

            // ========================================================
            // COLUMN 1: Network Activity (X: 6 to 72)
            // ========================================================
            if (_config.ShowNetSpeed)
            {
                // Upload row
                TextRenderer.DrawText(g, "▲", _fontBold, new Point(6, row1Y), colNetUp, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(
                    g,
                    uploadStr,
                    _fontRegular,
                    new Rectangle(18, row1Y, 54, 14),
                    colVal,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                );

                // Download row
                TextRenderer.DrawText(g, "▼", _fontBold, new Point(6, row2Y), colNetDown, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(
                    g,
                    downloadStr,
                    _fontRegular,
                    new Rectangle(18, row2Y, 54, 14),
                    colVal,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                );
            }

            // ========================================================
            // COLUMN 2: System Usage (X: 82 to 148)
            // ========================================================
            int col2LabelX = 82;
            int col2ValRectX = 106;
            int col2ValWidth = 43;

            // Row 1: CPU
            if (_config.ShowCpu)
            {
                TextRenderer.DrawText(g, "CPU", _fontBold, new Point(col2LabelX, row1Y), colLabel, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(
                    g,
                    cpuStr,
                    _fontRegular,
                    new Rectangle(col2ValRectX, row1Y, col2ValWidth, 14),
                    colVal,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                );
            }

            // Row 2: RAM (or GPU)
            if (_config.ShowGpu && _latestStats.GpuPercent > 0)
            {
                TextRenderer.DrawText(g, "GPU", _fontBold, new Point(col2LabelX, row2Y), colLabel, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(
                    g,
                    $"{_latestStats.GpuPercent}%",
                    _fontRegular,
                    new Rectangle(col2ValRectX, row2Y, col2ValWidth, 14),
                    colVal,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                );
            }
            else if (_config.ShowRam)
            {
                TextRenderer.DrawText(g, "RAM", _fontBold, new Point(col2LabelX, row2Y), colLabel, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(
                    g,
                    ramStr,
                    _fontRegular,
                    new Rectangle(col2ValRectX, row2Y, col2ValWidth, 14),
                    colVal,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                );
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

    private static GraphicsPath GetRoundedRectPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int diameter = radius * 2;
        var arc = new Rectangle(rect.Location, new Size(diameter, diameter));

        path.AddArc(arc, 180, 90);
        arc.X = rect.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = rect.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = rect.Left;
        path.AddArc(arc, 90, 90);

        path.CloseFigure();
        return path;
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
