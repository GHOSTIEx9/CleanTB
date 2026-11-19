using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace CleanTB;

static class Program
{
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    static void Main()
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

            const string mutexName = "CleanTB_SingleInstanceMutex_7B8A9C";
            _singleInstanceMutex = new Mutex(true, mutexName, out bool isOnlyInstance);

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

            _singleInstanceMutex.ReleaseMutex();
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
