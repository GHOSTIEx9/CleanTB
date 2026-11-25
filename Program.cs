using System;
using System.Diagnostics;
using System.IO;
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

    private static void EnsureEmbeddedDependencies()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string localAppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanTB");

        ExtractResourceIfMissing("CleanTB.ExplorerTAP.dll", "ExplorerTAP.dll", baseDir, localAppDataDir);
        ExtractResourceIfMissing("CleanTB.ExplorerHooks.dll", "ExplorerHooks.dll", baseDir, localAppDataDir);
    }

    private static void ExtractResourceIfMissing(string resourceName, string fileName, string primaryDir, string fallbackDir)
    {
        try
        {
            string primaryPath = Path.Combine(primaryDir, fileName);
            if (File.Exists(primaryPath)) return;

            // Attempt write to primary (app directory)
            try
            {
                using var stream = typeof(Program).Assembly.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    using var fileStream = new FileStream(primaryPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    stream.CopyTo(fileStream);
                    return;
                }
            }
            catch (UnauthorizedAccessException)
            {
                // App directory is write-protected; fall back to local AppData
            }

            // Fallback to %LOCALAPPDATA%\CleanTB
            string fallbackPath = Path.Combine(fallbackDir, fileName);
            if (File.Exists(fallbackPath)) return;

            if (!Directory.Exists(fallbackDir))
            {
                Directory.CreateDirectory(fallbackDir);
            }

            using var fallbackStream = typeof(Program).Assembly.GetManifestResourceStream(resourceName);
            if (fallbackStream != null)
            {
                using var fileStream = new FileStream(fallbackPath, FileMode.Create, FileAccess.Write, FileShare.None);
                fallbackStream.CopyTo(fileStream);
            }
        }
        catch
        {
            // Silently continue if extraction fails; controller has its own verification
        }
    }
}
