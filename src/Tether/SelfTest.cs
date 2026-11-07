using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Tether.Core.Settings;
using Tether.Core.Updates;
using Tether.Themes;
using Tether.UI;

namespace Tether;

/// <summary>
/// `Tether.exe --selftest <report-file>`: builds every window, cycles themes and settings pages, then exits.
/// Run by CI on Windows to catch XAML / resource / interop failures that a compile can't see.
/// </summary>
internal static class SelfTest
{
    public static async void Run(App app, string reportPath)
    {
        var log = new List<string>();
        int code = 0;
        try
        {
            log.Add($"version {UpdateService.CurrentVersion}");
            var settings = new SettingsStore();
            ThemeManager.Initialize(settings.Current);
            log.Add("theme ok");

            var shell = new ShellViewModel(settings, app.Dispatcher);
            log.Add("services ok");
            try { await shell.StartAsync(); log.Add($"bluetooth started (radio available: {shell.Bluetooth.IsRadioAvailable})"); }
            catch (Exception ex) { log.Add($"bluetooth start skipped: {ex.GetType().Name}: {ex.Message}"); }

            using var tray = new TrayIcon(shell);
            log.Add("tray ok");

            var flyout = new FlyoutWindow(shell);
            flyout.ShowFlyout();
            await Pump();
            log.Add($"flyout ok ({flyout.ActualWidth:0}x{flyout.ActualHeight:0})");

            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark, ThemeMode.System })
            {
                settings.Current.Theme = mode;
                ThemeManager.Apply();
                await Pump();
            }
            log.Add("theme switching ok");

            var win = new SettingsWindow(shell);
            win.Show();
            await Pump();
            var nav = (ListBox)win.FindName("Nav");
            for (int i = 0; i < nav.Items.Count; i++)
            {
                nav.SelectedIndex = i;
                await Pump();
            }
            log.Add("settings pages ok");

            log.Add($"semver: {SemVer.TryParse("v1.2.3-beta.1", out var v) && v.CompareTo(new SemVer(1, 2, 3, null)) < 0}");

            // Updater: real call to GitHub. A CI build (0.0.0-ci.N) sees the latest release as an update,
            // so this also proves download + SHA-256 verification end to end. Network trouble isn't fatal.
            try
            {
                var info = await shell.Updates.CheckAsync(manual: true);
                if (info == null) log.Add("updater: up to date / no releases");
                else
                {
                    log.Add($"updater: found {info.Version} (sha asset: {info.ShaUrl != null})");
                    var path = await shell.Updates.DownloadAsync(info, null);
                    log.Add($"updater: downloaded and verified {new FileInfo(path).Length / 1024 / 1024} MB");
                }
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException)
            {
                log.Add($"updater: network unavailable ({ex.Message})");
            }
            win.Close();
            flyout.Close();
            shell.Dispose();
            log.Add("SELFTEST OK");
        }
        catch (Exception ex)
        {
            code = 1;
            log.Add("SELFTEST FAILED");
            log.Add(ex.ToString());
        }

        try { File.WriteAllLines(reportPath, log); } catch { }
        app.Shutdown(code);
    }

    private static Task Pump() =>
        Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task
            .ContinueWith(_ => Task.Delay(300)).Unwrap();
}
