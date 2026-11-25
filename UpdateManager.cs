using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CleanTB;

public static class UpdateManager
{
    private const string GitHubApiReleasesLatest = "https://api.github.com/repos/GHOSTIEx9/CleanTB/releases/latest";
    private static int _isChecking;

    public static Version CurrentVersion
    {
        get
        {
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            return ver != null ? new Version(ver.Major, ver.Minor, Math.Max(0, ver.Build)) : new Version(1, 0, 3);
        }
    }

    public static async Task CheckForUpdatesAsync(AppConfig config, bool isManualCheck)
    {
        if (Interlocked.Exchange(ref _isChecking, 1) != 0)
        {
            if (isManualCheck)
            {
                MessageBox.Show(
                    "An update check is already in progress.",
                    "CleanTB Updater",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
            return;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CleanTB-App", CurrentVersion.ToString()));

            var response = await client.GetAsync(GitHubApiReleasesLatest);
            if (!response.IsSuccessStatusCode)
            {
                if (isManualCheck)
                {
                    MessageBox.Show(
                        $"Could not check for updates right now (GitHub server returned HTTP {(int)response.StatusCode}).\nPlease try again later.",
                        "CleanTB Updater",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning
                    );
                }
                return;
            }

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagProp))
            {
                if (isManualCheck)
                {
                    MessageBox.Show(
                        "Unable to parse update information from GitHub.",
                        "CleanTB Updater",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning
                    );
                }
                return;
            }

            string tagName = tagProp.GetString() ?? "";
            string cleanTag = tagName.TrimStart('v', 'V').Trim();

            if (!Version.TryParse(cleanTag, out Version? latestVersion))
            {
                if (isManualCheck)
                {
                    MessageBox.Show(
                        $"Could not parse latest version tag '{tagName}'.",
                        "CleanTB Updater",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning
                    );
                }
                return;
            }

            string downloadUrl = "";
            if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    if (asset.TryGetProperty("name", out var nameProp) &&
                        string.Equals(nameProp.GetString(), "CleanTB.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        if (asset.TryGetProperty("browser_download_url", out var urlProp))
                        {
                            downloadUrl = urlProp.GetString() ?? "";
                            break;
                        }
                    }
                }
            }

            if (latestVersion > CurrentVersion)
            {
                if (string.IsNullOrEmpty(downloadUrl))
                {
                    if (isManualCheck)
                    {
                        MessageBox.Show(
                            $"CleanTB {tagName} is available, but the direct executable asset was not found on the release page.",
                            "CleanTB Updater",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information
                        );
                    }
                    return;
                }

                string releaseNotes = "";
                if (root.TryGetProperty("body", out var bodyProp))
                {
                    releaseNotes = bodyProp.GetString() ?? "";
                    if (releaseNotes.Length > 250)
                    {
                        releaseNotes = releaseNotes.Substring(0, 247) + "...";
                    }
                }

                string prompt = $"A new version of CleanTB is available!\n\n" +
                                $"• Current Version: v{CurrentVersion}\n" +
                                $"• Latest Version:  {tagName}\n\n" +
                                (string.IsNullOrWhiteSpace(releaseNotes) ? "" : $"Notes:\n{releaseNotes}\n\n") +
                                $"Would you like to download and update now?";

                var result = MessageBox.Show(
                    prompt,
                    "CleanTB Update Available",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information
                );

                if (result == DialogResult.Yes)
                {
                    ShowUpdateProgressDialog(downloadUrl, tagName);
                }
            }
            else
            {
                if (isManualCheck)
                {
                    MessageBox.Show(
                        $"You're up to date!\n\nCleanTB v{CurrentVersion} is currently the latest version.",
                        "CleanTB Updater",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
                }
            }
        }
        catch (Exception ex)
        {
            if (isManualCheck)
            {
                MessageBox.Show(
                    $"Unable to check for updates: {ex.Message}\nPlease check your internet connection.",
                    "CleanTB Updater",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isChecking, 0);
        }
    }

    private static void ShowUpdateProgressDialog(string downloadUrl, string tagName)
    {
        var form = new Form
        {
            Text = $"Updating CleanTB to {tagName}",
            Size = new Size(420, 175),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false,
            TopMost = true,
            ShowInTaskbar = true
        };

        var lblStatus = new Label
        {
            Text = "Connecting to GitHub...",
            Location = new Point(20, 18),
            Size = new Size(360, 20),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular)
        };

        var progressBar = new ProgressBar
        {
            Location = new Point(20, 45),
            Size = new Size(365, 24),
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Style = ProgressBarStyle.Continuous
        };

        var lblPercent = new Label
        {
            Text = "0%",
            Location = new Point(20, 75),
            Size = new Size(200, 20),
            ForeColor = Color.DimGray,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular)
        };

        var btnCancel = new Button
        {
            Text = "Cancel",
            Location = new Point(295, 95),
            Size = new Size(90, 28),
            Font = new Font("Segoe UI", 9f)
        };

        form.Controls.Add(lblStatus);
        form.Controls.Add(progressBar);
        form.Controls.Add(lblPercent);
        form.Controls.Add(btnCancel);

        var cts = new CancellationTokenSource();
        btnCancel.Click += (s, e) =>
        {
            cts.Cancel();
            form.Close();
        };

        form.FormClosing += (s, e) =>
        {
            cts.Cancel();
        };

        form.Shown += async (s, e) =>
        {
            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "CleanTB_Update");
                if (!Directory.Exists(tempDir))
                {
                    Directory.CreateDirectory(tempDir);
                }

                string tempFile = Path.Combine(tempDir, "CleanTB.exe");
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }

                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CleanTB-App", CurrentVersion.ToString()));

                using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                response.EnsureSuccessStatusCode();

                long? totalBytes = response.Content.Headers.ContentLength;

                await using (var contentStream = await response.Content.ReadAsStreamAsync(cts.Token))
                await using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    byte[] buffer = new byte[81920];
                    long totalRead = 0;
                    int bytesRead;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cts.Token)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead, cts.Token);
                        totalRead += bytesRead;

                        if (totalBytes.HasValue && totalBytes.Value > 0)
                        {
                            int progress = (int)((totalRead * 100) / totalBytes.Value);
                            double readMb = totalRead / (1024.0 * 1024.0);
                            double totalMb = totalBytes.Value / (1024.0 * 1024.0);

                            if (form.IsHandleCreated && !form.IsDisposed)
                            {
                                form.BeginInvoke(new Action(() =>
                                {
                                    progressBar.Value = Math.Clamp(progress, 0, 100);
                                    lblStatus.Text = $"Downloading update... ({readMb:F1} MB / {totalMb:F1} MB)";
                                    lblPercent.Text = $"{progress}%";
                                }));
                            }
                        }
                    }
                }

                lblStatus.Text = "Applying update and restarting...";
                progressBar.Value = 100;
                btnCancel.Enabled = false;

                await Task.Delay(400);

                // Seamless handoff: PowerShell script terminates old instance, overwrites the exe, and starts the new one
                ExecuteHandoffAndRestart(tempFile, Application.ExecutablePath, form);
            }
            catch (OperationCanceledException)
            {
                // User clicked cancel
            }
            catch (Exception ex)
            {
                if (form.IsHandleCreated && !form.IsDisposed)
                {
                    MessageBox.Show(
                        $"Failed to download update: {ex.Message}",
                        "CleanTB Updater",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                    form.Close();
                }
            }
        };

        form.ShowDialog();
    }

    private static void ExecuteHandoffAndRestart(string newExePath, string currentExePath, Form? form)
    {
        try
        {
            int currentPid = Environment.ProcessId;
            string targetExePath = Environment.ProcessPath ?? currentExePath;

            // Robust handoff script in PowerShell:
            // 1. Force-stops current process ID so Windows immediately frees the file lock on CleanTB.exe
            // 2. Retries copying newExePath over targetExePath up to 30 times (every 200ms = 6s total)
            // 3. Starts the updated CleanTB.exe
            // 4. Cleans up the temporary update file
            string psScript = 
                $"$targetPid = {currentPid}; " +
                $"$dest = '{targetExePath.Replace("'", "''")}'; " +
                $"$src = '{newExePath.Replace("'", "''")}'; " +
                "try { Stop-Process -Id $targetPid -Force -ErrorAction SilentlyContinue } catch {} " +
                "Start-Sleep -Milliseconds 150; " +
                "for ($i=0; $i -lt 30; $i++) { " +
                "  try { " +
                "    if ($i -ge 2) { Get-Process -Name 'CleanTB' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue } " +
                "    Copy-Item -Path $src -Destination $dest -Force -ErrorAction Stop; " +
                "    Remove-Item -Path $src -Force -ErrorAction SilentlyContinue; " +
                "    $workDir = Split-Path -Path $dest; " +
                "    Start-Process -FilePath $dest -WorkingDirectory $workDir; " +
                "    exit 0; " +
                "  } catch { " +
                "    Start-Sleep -Milliseconds 200; " +
                "  } " +
                "}";

            string encodedScript = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(psScript));

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encodedScript}",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(startInfo);

            try
            {
                form?.Hide();
                form?.Close();
            }
            catch { }

            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not complete automatic restart: {ex.Message}\nYou can manually replace CleanTB.exe with:\n{newExePath}",
                "CleanTB Updater",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
        }
    }
}
