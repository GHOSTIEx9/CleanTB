using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CleanTB;

public enum TaskbarBrush : uint
{
    Acrylic = 0,
    SolidColor = 1
}

[ComImport]
[Guid("5bcf9150-c28a-4ef2-913c-4c3ea2f5ead0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITaskbarAppearanceService
{
    [PreserveSig]
    int SetTaskbarAppearance(IntPtr taskbar, TaskbarBrush brush, uint color);

    [PreserveSig]
    int SetTaskbarBlur(IntPtr taskbar, uint color, float blurAmount);

    [PreserveSig]
    int ReturnTaskbarToDefaultAppearance(IntPtr taskbar);

    [PreserveSig]
    int SetTaskbarBorderVisibility(IntPtr taskbar, [MarshalAs(UnmanagedType.Bool)] bool visible);

    [PreserveSig]
    int RestoreAllTaskbarsToDefault();

    [PreserveSig]
    int RestoreAllTaskbarsToDefaultWhenProcessDies(uint pid);

    [PreserveSig]
    int KillExplorerWhenPackageUninstalls([MarshalAs(UnmanagedType.LPWStr)] string packageFullName);
}

public class TaskbarController : IDisposable
{
    private static readonly Guid IID_ITaskbarAppearanceService = new("5bcf9150-c28a-4ef2-913c-4c3ea2f5ead0");

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenWindowStation(string lpszWinSta, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessWindowStation(IntPtr hWinSta);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PFN_InjectExplorerTAP(IntPtr window, ref Guid riid, out IntPtr ppv);

    private const uint MAXIMUM_ALLOWED = 0x02000000;

    private IntPtr _hTapModule = IntPtr.Zero;
    private PFN_InjectExplorerTAP? _injectFunc;
    private ITaskbarAppearanceService? _service;
    private IntPtr _pServiceRaw = IntPtr.Zero;

    private TaskbarMode _currentMode = TaskbarMode.Clear;
    private bool _hideBorder = true;

    public bool IsConnected => _service != null;

    public TaskbarController()
    {
        EnsureDesktopStation();
    }

    public static void EnsureDesktopStation()
    {
        try
        {
            IntPtr hwinsta = OpenWindowStation("WinSta0", false, MAXIMUM_ALLOWED);
            if (hwinsta != IntPtr.Zero) SetProcessWindowStation(hwinsta);

            IntPtr hdesk = OpenDesktop("Default", 0, false, MAXIMUM_ALLOWED);
            if (hdesk != IntPtr.Zero) SetThreadDesktop(hdesk);
        }
        catch { }
    }

    public bool Connect()
    {
        EnsureDesktopStation();

        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return false;

        string tapDllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExplorerTAP.dll");
        if (!File.Exists(tapDllPath))
        {
            throw new FileNotFoundException("ExplorerTAP.dll not found in application directory!", tapDllPath);
        }

        if (_hTapModule == IntPtr.Zero)
        {
            _hTapModule = LoadLibrary(tapDllPath);
            if (_hTapModule == IntPtr.Zero) return false;
        }

        if (_injectFunc == null)
        {
            IntPtr proc = GetProcAddress(_hTapModule, "InjectExplorerTAP");
            if (proc == IntPtr.Zero) return false;
            _injectFunc = Marshal.GetDelegateForFunctionPointer<PFN_InjectExplorerTAP>(proc);
        }

        Guid iid = IID_ITaskbarAppearanceService;
        int hr = _injectFunc(tray, ref iid, out IntPtr pServicePtr);
        if (hr == 0 && pServicePtr != IntPtr.Zero)
        {
            if (_pServiceRaw != IntPtr.Zero)
            {
                Marshal.Release(_pServiceRaw);
                _pServiceRaw = IntPtr.Zero;
            }

            _pServiceRaw = pServicePtr;
            _service = (ITaskbarAppearanceService)Marshal.GetObjectForIUnknown(pServicePtr);

            // Register safety net: if CleanTB process terminates, restore taskbar automatically
            try
            {
                _service.RestoreAllTaskbarsToDefaultWhenProcessDies(GetCurrentProcessId());
            }
            catch { }

            return true;
        }

        return false;
    }

    public void ApplyAppearance(TaskbarMode mode, bool hideBorder)
    {
        _currentMode = mode;
        _hideBorder = hideBorder;

        if (_service == null && !Connect())
        {
            return;
        }

        var taskbars = GetAllTaskbarWindows();
        foreach (var hwnd in taskbars)
        {
            ApplyToWindow(hwnd, mode, hideBorder);
        }
    }

    private void ApplyToWindow(IntPtr hwnd, TaskbarMode mode, bool hideBorder)
    {
        if (_service == null) return;

        try
        {
            switch (mode)
            {
                case TaskbarMode.Clear:
                    // SolidColor with 0x00000000 = completely transparent glass
                    _service.SetTaskbarAppearance(hwnd, TaskbarBrush.SolidColor, 0x00000000);
                    break;

                case TaskbarMode.Blur:
                    // Modern blur with subtle dark tint (ABGR: A=0x40, B=0x14, G=0x14, R=0x14)
                    _service.SetTaskbarBlur(hwnd, 0x40141414, 30.0f);
                    break;

                case TaskbarMode.Acrylic:
                    // Windows 11 Acrylic brush
                    _service.SetTaskbarAppearance(hwnd, TaskbarBrush.Acrylic, 0x50202020);
                    break;

                case TaskbarMode.TintedDark:
                    // 40% translucent dark tint (ABGR: A=0x66, B=0x14, G=0x14, R=0x14)
                    _service.SetTaskbarAppearance(hwnd, TaskbarBrush.SolidColor, 0x66141414);
                    break;

                case TaskbarMode.TintedLight:
                    // 40% translucent light tint (ABGR: A=0x66, B=0xEE, G=0xEE, R=0xEE)
                    _service.SetTaskbarAppearance(hwnd, TaskbarBrush.SolidColor, 0x66EEEEEE);
                    break;

                case TaskbarMode.Normal:
                    _service.ReturnTaskbarToDefaultAppearance(hwnd);
                    break;
            }

            // Set top border line visibility (false = clean borderless look)
            _service.SetTaskbarBorderVisibility(hwnd, !hideBorder);
        }
        catch (COMException)
        {
            // If explorer restarted or disconnected, reset and try reconnecting once
            _service = null;
            if (Connect())
            {
                ApplyToWindow(hwnd, mode, hideBorder);
            }
        }
    }

    public List<IntPtr> GetAllTaskbarWindows()
    {
        EnsureDesktopStation();
        var list = new List<IntPtr>();

        // Primary taskbar
        IntPtr primary = FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero)
        {
            list.Add(primary);
        }

        // Secondary taskbars on multiple monitors
        IntPtr secondary = IntPtr.Zero;
        while ((secondary = FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
        {
            list.Add(secondary);
        }

        return list;
    }

    public void RestoreDefault()
    {
        if (_service != null)
        {
            try
            {
                _service.RestoreAllTaskbarsToDefault();
            }
            catch { }
        }
    }

    public void Dispose()
    {
        RestoreDefault();

        if (_pServiceRaw != IntPtr.Zero)
        {
            Marshal.Release(_pServiceRaw);
            _pServiceRaw = IntPtr.Zero;
        }
        _service = null;
    }
}
