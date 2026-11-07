using System.Windows;
using System.Windows.Threading;
using Tether.Core;
using Tether.Core.Settings;
using Tether.Core.Updates;
using Tether.Themes;
using Tether.UI;

namespace Tether;

public partial class App : Application
{
    private const string MutexName = @"Local\Tether.SingleInstance.6E1B3A70";
    private const string ShowEventName = @"Local\Tether.Show.6E1B3A70";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private ShellViewModel? _shell;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool relaunch = e.Args.Contains("--updated") || e.Args.Contains("--elevated-restart");

        if (!AcquireSingleInstance(waitForPrevious: relaunch))
        {
            // Another Tether is running: ask it to open its flyout, then leave.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("Unhandled", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("Unobserved task", a.Exception); a.SetObserved(); };

        UpdateService.CleanupAfterUpdate();
        Log.Info($"Tether {UpdateService.CurrentVersion} starting");

        var settings = new SettingsStore();
        settings.Load();
        ThemeManager.Initialize(settings.Current);
        if (settings.Current.StartWithWindows) StartupManager.Sync(true);

        _shell = new ShellViewModel(settings, Dispatcher);
        _tray = new TrayIcon(_shell);
        _ = _shell.StartAsync();

        if (e.Args.Contains("--updated"))
            _tray.ShowNotification("Tether updated", $"You're now on version {UpdateService.CurrentVersion}.");
        else if (!e.Args.Contains("--tray") && !File.Exists(Path.Combine(SettingsStore.AppDataDir, "settings.json")))
        {
            // First run: save defaults and show the flyout so people can see where Tether lives.
            settings.SaveNow();
            Dispatcher.InvokeAsync(() => _tray.ShowFlyout(), DispatcherPriority.ApplicationIdle);
            _tray.ShowNotification("Tether is in your tray", "Click the icon to manage your Bluetooth devices.");
        }

        ListenForShowRequests();
    }

    private bool AcquireSingleInstance(bool waitForPrevious)
    {
        _mutex = new Mutex(true, MutexName, out bool created);
        if (created) return true;
        if (!waitForPrevious) return false;
        try
        {
            // The previous instance is exiting after an update / elevation restart.
            return _mutex.WaitOne(TimeSpan.FromSeconds(15));
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private void ListenForShowRequests()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    _showEvent.WaitOne();
                    Dispatcher.InvokeAsync(() => _tray?.ShowFlyout());
                }
                catch { return; }
            }
        }) { IsBackground = true, Name = "Tether.ShowListener" };
        t.Start();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true; // stay alive in the tray
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _shell?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
