using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

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

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken,
        uint dwDesiredAccess,
        IntPtr lpTokenAttributes,
        int ImpersonationLevel,
        int TokenType,
        out IntPtr phNewToken);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr TokenHandle,
        bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState,
        uint BufferLength,
        IntPtr PreviousState,
        IntPtr ReturnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessWithTokenW(
        IntPtr hToken,
        uint dwLogonFlags,
        string? lpApplicationName,
        string? lpCommandLine,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        [In] ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenWindowStation(string lpszWinSta, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessWindowStation(IntPtr hWinSta);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll")]
    private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    private enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_GRADIENT = 1,
        ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
        ACCENT_INVALID_STATE = 5
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public uint AccentFlags;
        public uint GradientColor;
        public uint AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWCOMPOSITIONATTRIBDATA
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    private const int WCA_ACCENT_POLICY = 19;
    private const uint WM_DWMCOMPOSITIONCHANGED = 0x031E;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    // COM interfaces for Explorer de-elevation fallback (if Secondary Logon service is disabled)
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellWindows
    {
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW(ref object pvarLoc, ref object pvarLocRoot, int swClass, out int phwnd, int swf);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(out IntPtr phwnd);
        void ContextSensitiveHelp(bool fEnterMode);
        void InsertMenusSB();
        void SetMenuSB();
        void RemoveMenusSB();
        void SetStatusTextSB();
        void EnableModelessSB();
        void TranslateAcceleratorSB();
        void BrowseObject();
        void GetViewStateStream();
        void GetControlWindow();
        void SendControlMsg();
        [return: MarshalAs(UnmanagedType.Interface)]
        object QueryActiveShellView();
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow(out IntPtr phwnd);
        void ContextSensitiveHelp(bool fEnterMode);
        void TranslateAccelerator();
        void EnableModeless();
        void UIActivate();
        void Refresh();
        void CreateViewWindow();
        void DestroyViewWindow();
        void GetCurrentInfo();
        void AddPropertySheetPages();
        void SaveViewState();
        void SelectItem();
        [return: MarshalAs(UnmanagedType.Interface)]
        object GetItemObject(uint uItem, ref Guid riid);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PFN_InjectExplorerTAP(IntPtr window, ref Guid riid, out IntPtr ppv);

    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    private const int STARTF_USESHOWWINDOW = 0x00000001;
    private const uint CREATE_NO_WINDOW = 0x08000000;

    private const byte CMD_APPLY = 1;
    private const byte CMD_RESTORE = 2;
    private const byte CMD_SHUTDOWN = 3;

    private readonly bool _useWorkerBridge;
    private readonly object _bridgeLock = new();
    private NamedPipeClientStream? _pipeClient;
    private Process? _workerProcess;

    private IntPtr _hTapModule = IntPtr.Zero;
    private PFN_InjectExplorerTAP? _injectFunc;
    private ITaskbarAppearanceService? _service;
    private IntPtr _pServiceRaw = IntPtr.Zero;

    private TaskbarMode _currentMode = TaskbarMode.Clear;
    private bool _hideBorder = true;

    public bool IsConnected => _useWorkerBridge
        ? (_pipeClient != null && _pipeClient.IsConnected && _workerConnectedReported)
        : (_service != null);

    private bool _workerConnectedReported;
    private bool _loggedFallbackOnce;
    private static readonly object _diagLock = new();

    private static void LogTapDiag(string message)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanTB");
            Directory.CreateDirectory(dir);
            string logPath = Path.Combine(dir, "tap.log");
            lock (_diagLock)
            {
                if (File.Exists(logPath) && new FileInfo(logPath).Length > 32768)
                    File.WriteAllText(logPath, string.Empty);
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [PID:{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    public TaskbarController() : this(isWorkerProcess: false)
    {
    }

    internal TaskbarController(bool isWorkerProcess)
    {
        EnsureDesktopStation();
        // ExplorerTAP's InitializeXamlDiagnosticsEx enforces matching Medium Integrity Level with explorer.exe.
        // When CleanTB runs elevated (for direct CPU/RAM MSR/SPD sensors), spawn a lightweight Medium-IL
        // --tap-worker child process via Explorer's token so all taskbar modes work seamlessly.
        _useWorkerBridge = !isWorkerProcess && ThermalMonitor.IsRunningAsAdministrator();
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
        if (_useWorkerBridge)
        {
            return EnsureWorkerConnected();
        }

        EnsureDesktopStation();

        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return false;

        string? tapDllPath = Program.PreferredTapDllPath;
        if (string.IsNullOrEmpty(tapDllPath) || !File.Exists(tapDllPath))
        {
            string baseCandidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExplorerTAP.dll");
            string localCandidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanTB", "ExplorerTAP.dll");
            tapDllPath = File.Exists(baseCandidate) ? baseCandidate
                       : File.Exists(localCandidate) ? localCandidate
                       : null;
        }

        if (string.IsNullOrEmpty(tapDllPath))
        {
            LogTapDiag("Connect: ExplorerTAP.dll not found in native dir, BaseDirectory, or LocalAppData.");
            return false;
        }

        if (_hTapModule == IntPtr.Zero)
        {
            _hTapModule = LoadLibrary(tapDllPath);
            if (_hTapModule == IntPtr.Zero)
            {
                LogTapDiag($"Connect: LoadLibrary failed for '{tapDllPath}' (Win32={Marshal.GetLastWin32Error()}).");
                return false;
            }
        }

        if (_injectFunc == null)
        {
            IntPtr proc = GetProcAddress(_hTapModule, "InjectExplorerTAP");
            if (proc == IntPtr.Zero)
            {
                LogTapDiag($"Connect: GetProcAddress(InjectExplorerTAP) failed (Win32={Marshal.GetLastWin32Error()}).");
                return false;
            }
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
            _loggedFallbackOnce = false;

            // Register safety net: if CleanTB (or worker) process terminates, restore taskbar automatically
            try
            {
                _service.RestoreAllTaskbarsToDefaultWhenProcessDies(GetCurrentProcessId());
            }
            catch { }

            return true;
        }

        LogTapDiag($"Connect: InjectExplorerTAP failed (hr=0x{hr:X8}, ptr=0x{pServicePtr.ToInt64():X}, dll='{tapDllPath}', OS={Environment.OSVersion.Version}).");
        return false;
    }

    public void ApplyAppearance(TaskbarMode mode, bool hideBorder)
    {
        _currentMode = mode;
        _hideBorder = hideBorder;

        if (_useWorkerBridge)
        {
            if (SendWorkerCommand(CMD_APPLY, (byte)mode, hideBorder ? (byte)1 : (byte)0))
            {
                return;
            }
            // If worker bridge failed or TAP inside worker failed to inject, try direct fallback composition
            ApplyFallbackToAllTaskbars(mode);
            return;
        }

        if (_service == null && !Connect())
        {
            ApplyFallbackToAllTaskbars(mode);
            return;
        }

        var taskbars = GetAllTaskbarWindows();
        foreach (var hwnd in taskbars)
        {
            ApplyToWindow(hwnd, mode, hideBorder);
        }
    }

    private void ApplyFallbackToAllTaskbars(TaskbarMode mode)
    {
        if (!_loggedFallbackOnce)
        {
            _loggedFallbackOnce = true;
            LogTapDiag($"ApplyFallbackToAllTaskbars: Using Win32 SetWindowCompositionAttribute fallback for mode={mode}.");
        }
        foreach (var hwnd in GetAllTaskbarWindows())
        {
            ApplyFallbackComposition(hwnd, mode);
        }
    }

    private static void ApplyFallbackComposition(IntPtr hwnd, TaskbarMode mode)
    {
        try
        {
            if (mode == TaskbarMode.Normal)
            {
                var off = new AccentPolicy { AccentState = AccentState.ACCENT_DISABLED, AccentFlags = 0, GradientColor = 0 };
                SetAccentPolicy(hwnd, ref off);
                SendMessageTimeout(hwnd, WM_DWMCOMPOSITIONCHANGED, (IntPtr)1, IntPtr.Zero, SMTO_ABORTIFHUNG, 500, out _);
                return;
            }

            AccentPolicy policy = mode switch
            {
                TaskbarMode.Clear => new AccentPolicy { AccentState = AccentState.ACCENT_ENABLE_TRANSPARENTGRADIENT, AccentFlags = 2, GradientColor = 0x00000000 },
                TaskbarMode.Blur => new AccentPolicy { AccentState = AccentState.ACCENT_ENABLE_BLURBEHIND, AccentFlags = 2, GradientColor = 0x40141414 },
                TaskbarMode.Acrylic => new AccentPolicy { AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND, AccentFlags = 0, GradientColor = 0x50202020 },
                TaskbarMode.TintedDark => new AccentPolicy { AccentState = AccentState.ACCENT_ENABLE_TRANSPARENTGRADIENT, AccentFlags = 2, GradientColor = 0x66141414 },
                TaskbarMode.TintedLight => new AccentPolicy { AccentState = AccentState.ACCENT_ENABLE_TRANSPARENTGRADIENT, AccentFlags = 2, GradientColor = 0x66EEEEEE },
                _ => new AccentPolicy { AccentState = AccentState.ACCENT_ENABLE_TRANSPARENTGRADIENT, AccentFlags = 2, GradientColor = 0x00000000 }
            };
            SetAccentPolicy(hwnd, ref policy);
        }
        catch { }
    }

    private static void SetAccentPolicy(IntPtr hwnd, ref AccentPolicy policy)
    {
        int size = Marshal.SizeOf<AccentPolicy>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, ptr, false);
            var data = new WINDOWCOMPOSITIONATTRIBDATA
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = ptr,
                SizeOfData = size
            };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
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
        if (_useWorkerBridge)
        {
            SendWorkerCommand(CMD_RESTORE, 0, 0, spawnIfMissing: false);
        }
        else if (_service != null)
        {
            try
            {
                _service.RestoreAllTaskbarsToDefault();
            }
            catch { }
            return;
        }

        foreach (var hwnd in GetAllTaskbarWindows())
        {
            ApplyFallbackComposition(hwnd, TaskbarMode.Normal);
        }
    }

    // =========================================================================
    // Medium-IL TAP Worker Bridge (Used automatically when running as Admin)
    // =========================================================================

    private bool EnsureWorkerConnected()
    {
        lock (_bridgeLock)
        {
            if (_pipeClient != null && _pipeClient.IsConnected && (_workerProcess == null || !_workerProcess.HasExited))
            {
                return true;
            }

            CleanupWorkerBridge(sendShutdown: false);

            int myPid = Environment.ProcessId;
            string pipeName = $"CleanTB_TAP_{myPid}_{Guid.NewGuid():N}";
            string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(exePath)) return false;
            string args = $"--tap-worker {myPid} {pipeName}";
            string workDir = Path.GetDirectoryName(exePath) ?? AppDomain.CurrentDomain.BaseDirectory;

            if (!SpawnMediumIntegrityWorker(exePath, args, workDir, out _workerProcess))
            {
                LogTapDiag($"EnsureWorkerConnected: Failed to spawn Medium-IL worker for '{exePath}'.");
                return false;
            }

            try
            {
                var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
                // Allow up to 15 seconds on first cold boot when Defender scans single-file extraction
                client.Connect(15000);
                _pipeClient = client;
                return client.IsConnected;
            }
            catch (Exception ex)
            {
                LogTapDiag($"EnsureWorkerConnected: Pipe connect failed ({ex.GetType().Name}: {ex.Message}).");
                CleanupWorkerBridge(sendShutdown: false);
                return false;
            }
        }
    }

    private bool SendWorkerCommand(byte cmd, byte arg1, byte arg2, bool spawnIfMissing = true)
    {
        lock (_bridgeLock)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (_pipeClient == null || !_pipeClient.IsConnected)
                {
                    if (!spawnIfMissing || !EnsureWorkerConnected())
                    {
                        _workerConnectedReported = false;
                        return false;
                    }
                }

                try
                {
                    var packet = new byte[] { cmd, arg1, arg2, 0 };
                    _pipeClient!.Write(packet, 0, 4);
                    _pipeClient.Flush();

                    int ack = _pipeClient.ReadByte();
                    if (ack >= 0)
                    {
                        _workerConnectedReported = ack == 1;
                        return _workerConnectedReported;
                    }
                }
                catch (Exception ex)
                {
                    LogTapDiag($"SendWorkerCommand: IPC error on attempt {attempt + 1} ({ex.Message}).");
                    CleanupWorkerBridge(sendShutdown: false);
                    _workerConnectedReported = false;
                    if (!spawnIfMissing) return false;
                }
            }
            _workerConnectedReported = false;
            return false;
        }
    }

    private void CleanupWorkerBridge(bool sendShutdown)
    {
        if (sendShutdown && _pipeClient != null && _pipeClient.IsConnected)
        {
            try
            {
                var pkt = new byte[] { CMD_SHUTDOWN, 0, 0, 0 };
                _pipeClient.Write(pkt, 0, 4);
                _pipeClient.Flush();
                _pipeClient.ReadByte();
            }
            catch { }
        }

        try { _pipeClient?.Dispose(); } catch { }
        _pipeClient = null;

        if (_workerProcess != null)
        {
            try
            {
                if (!_workerProcess.HasExited && !_workerProcess.WaitForExit(400))
                {
                    _workerProcess.Kill();
                }
            }
            catch { }
            try { _workerProcess.Dispose(); } catch { }
            _workerProcess = null;
        }
    }

    public static bool TryLaunchUnelevated(string arguments)
    {
        string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (string.IsNullOrEmpty(exePath)) return false;
        string workDir = Path.GetDirectoryName(exePath) ?? AppDomain.CurrentDomain.BaseDirectory;
        return SpawnMediumIntegrityWorker(exePath, arguments, workDir, out _);
    }

    private static bool SpawnMediumIntegrityWorker(string exePath, string args, string workDir, out Process? workerProc)
    {
        workerProc = null;

        if (TrySpawnViaShellToken(exePath, args, workDir, out workerProc))
        {
            return true;
        }

        return TrySpawnViaExplorerCom(exePath, args, workDir);
    }

    private static bool TrySpawnViaShellToken(string exePath, string args, string workDir, out Process? workerProc)
    {
        workerProc = null;
        IntPtr hShellProc = IntPtr.Zero;
        IntPtr hShellToken = IntPtr.Zero;
        IntPtr hPrimaryToken = IntPtr.Zero;

        try
        {
            EnablePrivilege("SeImpersonatePrivilege");

            int shellPid = 0;
            IntPtr hShellWnd = GetShellWindow();
            if (hShellWnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(hShellWnd, out shellPid);
            }

            if (shellPid <= 0)
            {
                int currentSession = Process.GetCurrentProcess().SessionId;
                foreach (var p in Process.GetProcessesByName("explorer"))
                {
                    using (p)
                    {
                        if (p.SessionId == currentSession)
                        {
                            shellPid = p.Id;
                            break;
                        }
                    }
                }
            }

            if (shellPid <= 0) return false;

            hShellProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, shellPid);
            if (hShellProc == IntPtr.Zero) return false;

            if (!OpenProcessToken(hShellProc, TOKEN_DUPLICATE | TOKEN_QUERY, out hShellToken) || hShellToken == IntPtr.Zero)
                return false;

            // SecurityImpersonation (2), TokenPrimary (1)
            if (!DuplicateTokenEx(hShellToken, MAXIMUM_ALLOWED, IntPtr.Zero, 2, 1, out hPrimaryToken) || hPrimaryToken == IntPtr.Zero)
                return false;

            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"WinSta0\Default",
                dwFlags = STARTF_USESHOWWINDOW,
                wShowWindow = 0 // SW_HIDE
            };

            string cmdLine = $"\"{exePath}\" {args}";
            if (!CreateProcessWithTokenW(
                    hPrimaryToken,
                    0,
                    exePath,
                    cmdLine,
                    CREATE_NO_WINDOW,
                    IntPtr.Zero,
                    workDir,
                    ref si,
                    out PROCESS_INFORMATION pi))
            {
                return false;
            }

            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);

            try { workerProc = Process.GetProcessById(pi.dwProcessId); } catch { }
            return pi.dwProcessId > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hPrimaryToken != IntPtr.Zero) CloseHandle(hPrimaryToken);
            if (hShellToken != IntPtr.Zero) CloseHandle(hShellToken);
            if (hShellProc != IntPtr.Zero) CloseHandle(hShellProc);
        }
    }

    private static void EnablePrivilege(string privilegeName)
    {
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr hToken))
                return;

            try
            {
                if (LookupPrivilegeValue(null, privilegeName, out LUID luid))
                {
                    var tp = new TOKEN_PRIVILEGES
                    {
                        PrivilegeCount = 1,
                        Luid = luid,
                        Attributes = SE_PRIVILEGE_ENABLED
                    };
                    AdjustTokenPrivileges(hToken, false, ref tp, (uint)Marshal.SizeOf<TOKEN_PRIVILEGES>(), IntPtr.Zero, IntPtr.Zero);
                }
            }
            finally
            {
                CloseHandle(hToken);
            }
        }
        catch { }
    }

    private static bool TrySpawnViaExplorerCom(string exePath, string args, string workDir)
    {
        bool ok = false;

        void InvokeOnSta()
        {
            try
            {
                var swType = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
                if (swType == null) return;

                var shellWindows = (IShellWindows?)Activator.CreateInstance(swType);
                if (shellWindows == null) return;

                object loc = 0; // CSIDL_DESKTOP
                object empty = Type.Missing;
                object disp = shellWindows.FindWindowSW(ref loc, ref empty, 8 /* SWC_DESKTOP */, out _, 1 /* SWFO_NEEDDISPATCH */);
                if (disp is not IComServiceProvider sp) return;

                Guid sidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
                Guid iidShellBrowser = typeof(IShellBrowser).GUID;
                if (sp.QueryService(ref sidTopLevelBrowser, ref iidShellBrowser, out IntPtr pBrowser) != 0 || pBrowser == IntPtr.Zero)
                    return;

                var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(pBrowser);
                Marshal.Release(pBrowser);

                var view = (IShellView)browser.QueryActiveShellView();
                Guid iidDispatch = new("00020400-0000-0000-C000-000000000046");
                object folderView = view.GetItemObject(0 /* SVGIO_BACKGROUND */, ref iidDispatch);
                object? shellApp = folderView.GetType().InvokeMember("Application", BindingFlags.GetProperty, null, folderView, null);
                if (shellApp == null) return;

                shellApp.GetType().InvokeMember(
                    "ShellExecute",
                    BindingFlags.InvokeMethod,
                    null,
                    shellApp,
                    new object[] { exePath, args, workDir, "", 0 /* SW_HIDE */ });
                ok = true;
            }
            catch
            {
                ok = false;
            }
        }

        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            InvokeOnSta();
        }
        else
        {
            var t = new Thread(InvokeOnSta) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(4000);
        }

        return ok;
    }

    private static void TrimWorkerWorkingSet()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            EmptyWorkingSet(GetCurrentProcess());
        }
        catch { }
    }

    public static void RunWorkerHost(int parentPid, string pipeName)
    {
        EnsureDesktopStation();

        var hostLock = new object();
        using var controller = new TaskbarController(isWorkerProcess: true);

        var pipeSecurity = new PipeSecurity();
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        using var server = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.None,
            0,
            0,
            pipeSecurity);
        int stopping = 0;

        // Periodic working-set trimmer so idle COM/CLR pages never sit resident in Task Manager
        using var trimTimer = new System.Threading.Timer(_ =>
        {
            if (Volatile.Read(ref stopping) == 0)
            {
                TrimWorkerWorkingSet();
            }
        }, null, 2500, 15000);

        void ShutdownCleanly()
        {
            if (Interlocked.Exchange(ref stopping, 1) != 0) return;
            lock (hostLock)
            {
                try { controller.Dispose(); } catch { }
            }
            try { server.Dispose(); } catch { }
        }

        // Watchdog thread: if elevated parent exits or is terminated, restore taskbar & exit immediately
        var watchdogThread = new Thread(() =>
        {
            try
            {
                using var parent = Process.GetProcessById(parentPid);
                parent.WaitForExit();
            }
            catch { }
            ShutdownCleanly();
        })
        {
            IsBackground = true
        };
        watchdogThread.Start();

        try
        {
            server.WaitForConnection();

            var buf = new byte[4];
            while (Volatile.Read(ref stopping) == 0 && server.IsConnected)
            {
                int totalRead = 0;
                while (totalRead < 4)
                {
                    int n = server.Read(buf, totalRead, 4 - totalRead);
                    if (n <= 0)
                    {
                        ShutdownCleanly();
                        return;
                    }
                    totalRead += n;
                }

                byte cmd = buf[0];
                if (cmd == CMD_APPLY)
                {
                    var mode = (TaskbarMode)buf[1];
                    bool hideBorder = buf[2] != 0;
                    bool connected = false;
                    lock (hostLock)
                    {
                        controller.ApplyAppearance(mode, hideBorder);
                        connected = controller.IsConnected;
                    }
                    server.WriteByte((byte)(connected ? 1 : 0));
                    server.Flush();
                    TrimWorkerWorkingSet();
                }
                else if (cmd == CMD_RESTORE)
                {
                    lock (hostLock)
                    {
                        controller.RestoreDefault();
                    }
                    server.WriteByte(1);
                    server.Flush();
                    TrimWorkerWorkingSet();
                }
                else if (cmd == CMD_SHUTDOWN)
                {
                    lock (hostLock)
                    {
                        controller.RestoreDefault();
                    }
                    server.WriteByte(1);
                    server.Flush();
                    ShutdownCleanly();
                    return;
                }
            }
        }
        catch
        {
            ShutdownCleanly();
        }
    }

    public void Dispose()
    {
        if (_useWorkerBridge)
        {
            lock (_bridgeLock)
            {
                CleanupWorkerBridge(sendShutdown: true);
            }
            GC.SuppressFinalize(this);
            return;
        }

        RestoreDefault();

        if (_pServiceRaw != IntPtr.Zero)
        {
            Marshal.Release(_pServiceRaw);
            _pServiceRaw = IntPtr.Zero;
        }

        _service = null;
        _injectFunc = null;

        if (_hTapModule != IntPtr.Zero)
        {
            FreeLibrary(_hTapModule);
            _hTapModule = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }
}
