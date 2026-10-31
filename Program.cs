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

            ApplicationConfiguration.Initialize();

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
}
