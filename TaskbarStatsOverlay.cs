using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
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

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private readonly AppConfig _config;
    private readonly HardwareMonitor _monitor;
    private readonly ContextMenuStrip _contextMenu;
    private HardwareStats _latestStats;

    private bool _isDragging;
    private Point _dragStartCursor;
    private Point _dragStartForm;

    private readonly Font _fontRegular;
    private readonly Font _fontBold;

    public TaskbarStatsOverlay(AppConfig config, HardwareMonitor monitor, ContextMenuStrip contextMenu)
    {
        _config = config;
        _monitor = monitor;
        _contextMenu = contextMenu;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;

        // Visual sizing for taskbar dock
        Size = new Size(150, 36);

        // Transparency setup
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;

        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.DoubleBuffer |
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.ResizeRedraw,
            true
        );

        // Fonts matching Windows 11 Fluent typography
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
                        Invalidate();
                    }));
                }
                catch { }
            }
        };

        // Wire mouse events
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        DoubleClick += (s, e) =>
        {
            // Double-click opens Windows Task Manager
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
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE

            if (_config?.StatsClickThrough == true)
            {
                cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT
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
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        UpdateDockPosition(force: true);
    }

    public void UpdateDockPosition(bool force = false)
    {
        if (_isDragging && !force) return;

        // Custom user positioning
        if (_config.StatsCustomX.HasValue && _config.StatsCustomY.HasValue)
        {
            Location = new Point(_config.StatsCustomX.Value, _config.StatsCustomY.Value);
            return;
        }

        // Automatic docking next to Windows System Tray (TrayNotifyWnd)
        IntPtr hTray = FindWindow("Shell_TrayWnd", null);
        if (hTray == IntPtr.Zero) return;

        if (GetWindowRect(hTray, out RECT trayRect))
        {
            IntPtr hNotify = FindWindowEx(hTray, IntPtr.Zero, "TrayNotifyWnd", null);
            int targetX;
            int targetY;

            if (hNotify != IntPtr.Zero && GetWindowRect(hNotify, out RECT notifyRect))
            {
                // Dock immediately to the left of the tray icons area
                targetX = notifyRect.Left - Width - 10;
                targetY = trayRect.Top + (trayRect.Height - Height) / 2;
            }
            else
            {
                // Fallback: dock near bottom right
                targetX = trayRect.Right - Width - 120;
                targetY = trayRect.Top + (trayRect.Height - Height) / 2;
            }

            if (Location.X != targetX || Location.Y != targetY)
            {
                SetWindowPos(Handle, HWND_TOPMOST, targetX, targetY, Width, Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
        }
    }

    public void ResetPosition()
    {
        _config.StatsCustomX = null;
        _config.StatsCustomY = null;
        _config.Save();
        UpdateDockPosition(force: true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        // Draw pill-shaped frosted background container
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = GetRoundedRectPath(rect, 6))
        {
            // Subtle dark translucent fill
            using (var brush = new SolidBrush(Color.FromArgb(170, 20, 20, 24)))
            {
                g.FillPath(brush, path);
            }

            // Subtle border
            using (var pen = new Pen(Color.FromArgb(40, 255, 255, 255), 1f))
            {
                g.DrawPath(pen, path);
            }
        }

        // Metrics formatting
        string uploadStr = HardwareMonitor.FormatSpeed(_latestStats.UploadSpeedBps);
        string downloadStr = HardwareMonitor.FormatSpeed(_latestStats.DownloadSpeedBps);
        string cpuStr = $"{_latestStats.CpuPercent}%";
        string ramStr = $"{_latestStats.RamPercent}%";

        // Colors
        Color colNetUp = Color.FromArgb(240, 160, 70);       // Warm amber/orange
        Color colNetDown = Color.FromArgb(0, 215, 255);      // Vibrant cyan
        Color colLabel = Color.FromArgb(170, 175, 185);     // Muted Fluent gray
        Color colVal = Color.FromArgb(250, 250, 250);       // Clean white

        int leftColX = 8;
        int rightColX = 82;
        int line1Y = 3;
        int line2Y = 18;

        // --- LINE 1: Net Upload & CPU ---
        if (_config.ShowNetSpeed)
        {
            TextRenderer.DrawText(g, "▲", _fontBold, new Point(leftColX, line1Y), colNetUp, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, uploadStr, _fontRegular, new Point(leftColX + 13, line1Y), colVal, TextFormatFlags.NoPadding);
        }

        if (_config.ShowCpu)
        {
            TextRenderer.DrawText(g, "CPU", _fontBold, new Point(rightColX, line1Y), colLabel, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, cpuStr, _fontRegular, new Point(rightColX + 28, line1Y), colVal, TextFormatFlags.NoPadding);
        }

        // --- LINE 2: Net Download & RAM (or GPU) ---
        if (_config.ShowNetSpeed)
        {
            TextRenderer.DrawText(g, "▼", _fontBold, new Point(leftColX, line2Y), colNetDown, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, downloadStr, _fontRegular, new Point(leftColX + 13, line2Y), colVal, TextFormatFlags.NoPadding);
        }

        if (_config.ShowGpu && _latestStats.GpuPercent > 0)
        {
            TextRenderer.DrawText(g, "GPU", _fontBold, new Point(rightColX, line2Y), colLabel, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, $"{_latestStats.GpuPercent}%", _fontRegular, new Point(rightColX + 28, line2Y), colVal, TextFormatFlags.NoPadding);
        }
        else if (_config.ShowRam)
        {
            TextRenderer.DrawText(g, "RAM", _fontBold, new Point(rightColX, line2Y), colLabel, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, ramStr, _fontRegular, new Point(rightColX + 28, line2Y), colVal, TextFormatFlags.NoPadding);
        }
    }

    private static GraphicsPath GetRoundedRectPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int diameter = radius * 2;
        var arc = new Rectangle(rect.Location, new Size(diameter, diameter));

        // Top-left
        path.AddArc(arc, 180, 90);

        // Top-right
        arc.X = rect.Right - diameter;
        path.AddArc(arc, 270, 90);

        // Bottom-right
        arc.Y = rect.Bottom - diameter;
        path.AddArc(arc, 0, 90);

        // Bottom-left
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

