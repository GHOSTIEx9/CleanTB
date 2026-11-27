using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace CleanTB;

static class Program
{
    private const string SingleInstanceMutexName = "CleanTB_SingleInstanceMutex_7B8A9C";
    private static Mutex? _singleInstanceMutex;

    // Intentionally omit [STAThread] on Main so EnsureDesktopStation() can bind WinSta0\Default
    // BEFORE OleMainThreadWndClass is created by STA COM initialization.
    static void Main(string[] args)
    {
        try
        {
            TaskbarController.EnsureDesktopStation();

            if (TryParseTapWorkerArgs(args, out int parentPid, out string pipeName))
            {
                EnsureEmbeddedDependencies();
                TaskbarController.RunWorkerHost(parentPid, pipeName);
                return;
            }

            WaitForPreviousInstanceIfRequested(args);

            // Check single-instance BEFORE prompting UAC so double-clicking while running doesn't trigger UAC
            if (IsAnotherInstanceRunning())
            {
                MessageBox.Show(
                    "CleanTB is already running in your system tray.",
                    "CleanTB",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
                return;
            }

            // Always request Administrator permissions on startup by default so CPU & RAM temperature toggles
            // can be enabled instantly from the tray menu without restarting CleanTB.
            // Temperature engines stay completely off/unloaded until enabled by the user, and taskbar styling
            // delegates cleanly to a lightweight Medium-IL --tap-worker subprocess.
            if (!ThermalMonitor.IsRunningAsAdministrator() &&
                !HasArg(args, "--no-elevate") &&
                !HasArg(args, "--elevate-attempted"))
            {
                if (TryLaunchElevatedInstance())
                {
                    return;
                }
            }

            var uiThread = new Thread(RunGuiApplication)
            {
                IsBackground = false
            };
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();
            uiThread.Join();
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), ex.ToString());
            }
            catch { }
        }
    }

    private static bool IsAnotherInstanceRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(SingleInstanceMutexName, out Mutex? existing))
            {
                existing.Dispose();
                return true;
            }
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Owned by an elevated instance
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasArg(string[] args, string flag)
    {
        foreach (string a in args)
        {
            if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool TryLaunchElevatedInstance()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return false;

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--wait-pid {Environment.ProcessId} --elevate-attempted",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
            Process.Start(startInfo);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User declined UAC prompt; fall back gracefully to standard user mode
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void RunGuiApplication()
    {
        try
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), e.ExceptionObject?.ToString() ?? "Unknown UnhandledException");
                }
                catch { }
            };
            Application.ThreadException += (s, e) =>
            {
                try
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), e.Exception.ToString());
                }
                catch { }
            };

            ApplicationConfiguration.Initialize();

            // Auto-extract embedded native dependencies if missing (enables 100% standalone single-file execution)
            EnsureEmbeddedDependencies();

            bool isOnlyInstance;
            try
            {
                _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out isOnlyInstance);
            }
            catch (UnauthorizedAccessException)
            {
                isOnlyInstance = false;
            }

            if (!isOnlyInstance)
            {
                MessageBox.Show(
                    "CleanTB is already running in your system tray.",
                    "CleanTB",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
                return;
            }

            var config = AppConfig.Load();
            using var controller = new TaskbarController();
            using var trayManager = new TrayIconManager(controller, config);

            // Run message pump for headless system tray app
            Application.Run(new ApplicationContext());

            try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
            _singleInstanceMutex?.Dispose();
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), ex.ToString());
            }
            catch { }
        }
    }

    private static bool TryParseTapWorkerArgs(string[] args, out int parentPid, out string pipeName)
    {
        parentPid = 0;
        pipeName = string.Empty;
        for (int i = 0; i < args.Length - 2; i++)
        {
            if (string.Equals(args[i], "--tap-worker", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out parentPid) &&
                parentPid > 0 &&
                !string.IsNullOrWhiteSpace(args[i + 2]))
            {
                pipeName = args[i + 2];
                return true;
            }
        }
        return false;
    }

    private static void WaitForPreviousInstanceIfRequested(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--wait-pid", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out int oldPid) &&
                oldPid > 0 &&
                oldPid != Environment.ProcessId)
            {
                try
                {
                    using var proc = Process.GetProcessById(oldPid);
                    proc.WaitForExit(5000);
                }
                catch
                {
                    // Already exited or not accessible
                }
                break;
            }
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "DeleteFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeleteFileW(string lpFileName);

    internal static string? PreferredTapDllPath { get; private set; }

    private static void EnsureEmbeddedDependencies()
    {
        string localAppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanTB");
        string tempFallbackDir = Path.Combine(Path.GetTempPath(), "CleanTB");

        // Compute short content hash of embedded ExplorerTAP.dll so updates never collide with
        // an older locked ExplorerTAP.dll pinned inside running explorer.exe, keep the folder
        // next to CleanTB.exe 100% DLL-free, and grant ALL APPLICATION PACKAGES (S-1-15-2-1) RX access.
        string versionTag = ComputeEmbeddedTapTag();
        string preferredDir = Path.Combine(localAppDataDir, $"native_{versionTag}");
        string fallbackDir = Path.Combine(tempFallbackDir, $"native_{versionTag}");

        PreferredTapDllPath = ExtractVerifiedResource("CleanTB.ExplorerTAP.dll", "ExplorerTAP.dll", preferredDir, fallbackDir);

        CleanupOldNativeDirs(localAppDataDir, preferredDir);
        CleanupOldNativeDirs(tempFallbackDir, fallbackDir);
    }

    private static string ComputeEmbeddedTapTag()
    {
        try
        {
            using var stream = typeof(Program).Assembly.GetManifestResourceStream("CleanTB.ExplorerTAP.dll");
            if (stream != null)
            {
                byte[] hash = SHA256.HashData(stream);
                return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
            }
        }
        catch { }
        return "v1";
    }

    private static string? ExtractVerifiedResource(string resourceName, string fileName, string preferredDir, string fallbackDir)
    {
        byte[]? embeddedBytes = null;
        try
        {
            using var stream = typeof(Program).Assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return null;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            embeddedBytes = ms.ToArray();
        }
        catch
        {
            return null;
        }

        string? result = TryWriteVerifiedFile(Path.Combine(preferredDir, fileName), embeddedBytes, grantAppContainerAcl: true);
        if (!string.IsNullOrEmpty(result)) return result;

        return TryWriteVerifiedFile(Path.Combine(fallbackDir, fileName), embeddedBytes, grantAppContainerAcl: true);
    }

    private static string? TryWriteVerifiedFile(string targetPath, byte[] expectedBytes, bool grantAppContainerAcl)
    {
        try
        {
            string? dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            if (grantAppContainerAcl && !string.IsNullOrEmpty(dir))
            {
                EnsureAppContainerReadable(dir, isDirectory: true);
            }

            bool needsWrite = !File.Exists(targetPath);
            if (!needsWrite)
            {
                try
                {
                    var fi = new FileInfo(targetPath);
                    if (fi.Length != expectedBytes.Length)
                    {
                        needsWrite = true;
                    }
                    else
                    {
                        byte[] existing = File.ReadAllBytes(targetPath);
                        if (!existing.AsSpan().SequenceEqual(expectedBytes))
                            needsWrite = true;
                    }
                }
                catch
                {
                    needsWrite = true;
                }
            }

            if (needsWrite)
            {
                using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                fs.Write(expectedBytes, 0, expectedBytes.Length);
            }

            // Unblock Mark-of-the-Web and ensure explorer.exe / XAML host can LoadLibrary it
            try { DeleteFileW(targetPath + ":Zone.Identifier"); } catch { }
            if (grantAppContainerAcl)
            {
                EnsureAppContainerReadable(targetPath, isDirectory: false);
            }

            return targetPath;
        }
        catch
        {
            return File.Exists(targetPath) ? targetPath : null;
        }
    }

    private static void EnsureAppContainerReadable(string path, bool isDirectory)
    {
        try
        {
            var appPackages = new SecurityIdentifier("S-1-15-2-1"); // ALL APPLICATION PACKAGES
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var inherit = isDirectory
                ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
                : InheritanceFlags.None;

            if (isDirectory)
            {
                var di = new DirectoryInfo(path);
                DirectorySecurity sec = di.GetAccessControl();
                sec.AddAccessRule(new FileSystemAccessRule(appPackages, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
                di.SetAccessControl(sec);
            }
            else
            {
                var fi = new FileInfo(path);
                FileSecurity sec = fi.GetAccessControl();
                sec.AddAccessRule(new FileSystemAccessRule(appPackages, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
                fi.SetAccessControl(sec);
            }
        }
        catch { }
    }

    private static void CleanupOldNativeDirs(string localAppDataDir, string keepDir)
    {
        try
        {
            if (!Directory.Exists(localAppDataDir)) return;
            foreach (string d in Directory.GetDirectories(localAppDataDir, "native_*"))
            {
                if (!string.Equals(d, keepDir, StringComparison.OrdinalIgnoreCase))
                {
                    try { Directory.Delete(d, recursive: true); } catch { }
                }
            }
        }
        catch { }
    }
}
