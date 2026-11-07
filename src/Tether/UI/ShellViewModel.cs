using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Tether.Core;
using Tether.Core.Audio;
using Tether.Core.Bluetooth;
using Tether.Core.Profiles;
using Tether.Core.Settings;
using Tether.Core.Updates;

namespace Tether.UI;

public sealed class AudioOutput : ObservableObject
{
    public AudioOutput(AudioEndpoint ep) { Id = ep.Id; Name = ep.Name; _isDefault = ep.IsDefault; }
    public string Id { get; }
    public string Name { get; }
    private bool _isDefault;
    public bool IsDefault { get => _isDefault; set => Set(ref _isDefault, value); }
    public string Glyph => Name.Contains("Headphone", StringComparison.OrdinalIgnoreCase) || Name.Contains("Headset", StringComparison.OrdinalIgnoreCase)
        ? "" : Name.Contains("HDMI", StringComparison.OrdinalIgnoreCase) || Name.Contains("Display", StringComparison.OrdinalIgnoreCase) ? "" : "";
}

/// <summary>Everything the flyout, tray menu and settings window bind to.</summary>
public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _ui;

    public SettingsStore Settings { get; }
    public AudioService Audio { get; }
    public BluetoothService Bluetooth { get; }
    public BluetoothConnector Connector { get; }
    public ProfileService Profiles { get; }
    public UpdateService Updates { get; }

    public ICollectionView VisibleDevices { get; }
    public ObservableCollection<AudioOutput> Outputs { get; } = new();

    public ICommand ToggleConnectionCommand { get; }
    public ICommand SetOutputCommand { get; }
    public ICommand ToggleRadioCommand { get; }
    public ICommand OpenBluetoothSettingsCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand InstallUpdateCommand { get; }
    public ICommand UseAsOutputCommand { get; }

    /// <summary>(title, message)</summary>
    public event Action<string, string>? Notify;
    public event EventHandler? TrayStateChanged;
    public event Action<string?>? OpenSettingsRequested;

    public ShellViewModel(SettingsStore settings, Dispatcher ui)
    {
        _ui = ui;
        Settings = settings;
        Audio = new AudioService();
        Bluetooth = new BluetoothService(ui) { LowBatteryThreshold = () => Settings.Current.LowBatteryThreshold };
        Connector = new BluetoothConnector(Audio);
        Profiles = new ProfileService(Bluetooth, Connector, Audio, settings);
        Updates = new UpdateService(settings);

        var view = new ListCollectionView(Bluetooth.Devices);
        view.Filter = o => o is BtDevice d && !Settings.Current.HiddenDevices.Contains(d.AddressString, StringComparer.OrdinalIgnoreCase);
        VisibleDevices = view;

        ToggleConnectionCommand = new RelayCommand(p => { if (p is BtDevice d) _ = ToggleConnectionAsync(d); }, p => p is BtDevice { IsBusy: false });
        SetOutputCommand = new RelayCommand(p => { if (p is AudioOutput o) SetOutput(o); });
        ToggleRadioCommand = new RelayCommand(() => _ = ToggleRadioAsync());
        OpenBluetoothSettingsCommand = new RelayCommand(() => StartupManager.OpenUri("ms-settings:bluetooth"));
        OpenSettingsCommand = new RelayCommand(p => OpenSettingsRequested?.Invoke(p as string));
        InstallUpdateCommand = new RelayCommand(() => _ = InstallUpdateAsync());
        UseAsOutputCommand = new RelayCommand(p => { if (p is BtDevice d) _ = UseAsOutputAsync(d); });

        Profiles.Notify += (t, m) => _ui.InvokeAsync(() => Notify?.Invoke(t, m));
        Audio.EndpointsChanged += (_, _) => _ui.InvokeAsync(RefreshOutputs);
        Bluetooth.ConnectionChanged += OnConnectionChanged;
        Bluetooth.BatteryChanged += (_, _) => { CheckLowBatteries(); TrayStateChanged?.Invoke(this, EventArgs.Empty); };
        Bluetooth.Devices.CollectionChanged += (_, _) => { ApplyCustomNames(); TrayStateChanged?.Invoke(this, EventArgs.Empty); };
        Updates.UpdateAvailable += (_, info) => _ui.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(UpdateBannerText));
            Notify?.Invoke("Tether update available", $"Version {info.Version} is ready. Click to install.");
        });
    }

    public async Task StartAsync()
    {
        RefreshOutputs();
        await Bluetooth.StartAsync();
        Updates.StartBackgroundChecks();
    }

    // ------------------------------------------------------------------ devices

    public bool HasUpdate => Updates.Available != null;
    public string UpdateBannerText => Updates.Available is { } u ? $"Update {u.Version} available" : "";

    private void OnConnectionChanged(object? sender, ConnectionChangedEventArgs e)
    {
        if (Settings.Current.ConnectionNotifications)
            Notify?.Invoke(e.Device.DisplayName, e.Connected ? "Connected" : "Disconnected");
        TrayStateChanged?.Invoke(this, EventArgs.Empty);
        RefreshOutputs();
    }

    public void ApplyCustomNames()
    {
        foreach (var d in Bluetooth.Devices)
        {
            Settings.Current.Profiles.TryGetValue(d.AddressString, out var p);
            d.CustomName = p?.CustomName;
            d.HasProfile = p is { Enabled: true, HasActions: true };
            d.LowThreshold = Settings.Current.LowBatteryThreshold;
        }
    }

    public void RefreshDeviceFilter()
    {
        VisibleDevices.Refresh();
        TrayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ToggleConnectionAsync(BtDevice dev)
    {
        if (dev.IsBusy) return;
        bool target = !dev.IsConnected;
        dev.BusyText = target ? "Connecting…" : "Disconnecting…";
        dev.IsBusy = true;
        try
        {
            var result = target ? await Connector.ConnectAsync(dev) : await Connector.DisconnectAsync(dev);
            if (!result.Success)
            {
                if (result.NeedsElevation && !StartupManager.IsElevated())
                {
                    var answer = MessageBox.Show(
                        $"{result.Message}\n\nRestart Tether as administrator?",
                        "Tether", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (answer == MessageBoxResult.Yes) StartupManager.RestartElevated();
                }
                else
                {
                    Notify?.Invoke(dev.DisplayName, result.Message ?? "That didn't work.");
                }
                return;
            }

            // Wait for Windows to report the new state (the watcher flips IsConnected for us).
            var deadline = DateTime.UtcNow.AddSeconds(target ? 15 : 8);
            while (dev.IsConnected != target && DateTime.UtcNow < deadline) await Task.Delay(250);
            if (dev.IsConnected != target)
                Notify?.Invoke(dev.DisplayName, target ? "Didn't connect. Make sure it's on, nearby and not connected to another device." : "Didn't disconnect.");
        }
        finally
        {
            dev.IsBusy = false;
            dev.BusyText = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task ToggleRadioAsync()
    {
        if (!await Bluetooth.SetRadioAsync(!Bluetooth.IsRadioOn))
            Notify?.Invoke("Bluetooth", "Windows didn't allow Tether to change the radio. Use Quick Settings instead.");
    }

    private void CheckLowBatteries()
    {
        if (!Settings.Current.LowBatteryNotifications) return;
        int threshold = Settings.Current.LowBatteryThreshold;
        foreach (var d in Bluetooth.Devices)
        {
            if (d.Battery is not int b || !d.IsConnected) continue;
            if (b <= threshold && !d.LowBatteryNotified)
            {
                d.LowBatteryNotified = true;
                Notify?.Invoke($"{d.DisplayName} battery low", $"{b}% remaining.");
            }
            else if (b > threshold + 5)
            {
                d.LowBatteryNotified = false;
            }
        }
    }

    // ------------------------------------------------------------------ audio

    private int _outputsVersion;

    public void RefreshOutputs()
    {
        int version = ++_outputsVersion;
        Task.Run(() =>
        {
            var list = Audio.GetPlaybackDevices();
            // Mark which Bluetooth device is currently the default output.
            var defaultId = list.FirstOrDefault(e => e.IsDefault)?.Id;
            return (list, defaultId);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted || version != _outputsVersion) return;
            var (list, defaultId) = t.Result;
            Outputs.Clear();
            foreach (var ep in list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)) Outputs.Add(new AudioOutput(ep));
            OnPropertyChanged(nameof(HasOutputs));
            _ = MarkDefaultBluetoothOutputAsync(defaultId);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private async Task MarkDefaultBluetoothOutputAsync(string? defaultId)
    {
        foreach (var d in Bluetooth.Devices.ToList())
        {
            if (!d.IsConnected || !d.IsAudio || defaultId == null) { d.IsDefaultOutput = false; continue; }
            var ep = await Task.Run(() => Connector.FindActiveRenderEndpoint(d));
            d.IsDefaultOutput = ep == defaultId;
        }
    }

    public bool HasOutputs => Outputs.Count > 0;
    public bool ShowAudioSection => Settings.Current.ShowAudioSection;
    public void NotifyLayoutSettingsChanged() => OnPropertyChanged(nameof(ShowAudioSection));

    private async Task UseAsOutputAsync(BtDevice d)
    {
        var ep = await Task.Run(() => Connector.FindActiveRenderEndpoint(d));
        if (ep != null && Audio.SetDefault(ep, includeCommunications: true)) RefreshOutputs();
        else Notify?.Invoke(d.DisplayName, "No audio output is available for this device right now.");
    }

    private void SetOutput(AudioOutput o)
    {
        if (o.IsDefault) return;
        if (Audio.SetDefault(o.Id, includeCommunications: true))
        {
            foreach (var x in Outputs) x.IsDefault = x.Id == o.Id;
        }
        else
        {
            Notify?.Invoke("Audio", $"Couldn't switch to {o.Name}.");
        }
    }

    // ------------------------------------------------------------------ updates

    private bool _isUpdating;
    public bool IsUpdating { get => _isUpdating; private set => Set(ref _isUpdating, value); }

    private double _updateProgress;
    public double UpdateProgress { get => _updateProgress; private set => Set(ref _updateProgress, value); }

    public async Task InstallUpdateAsync()
    {
        var info = Updates.Available;
        if (info == null || IsUpdating) return;
        IsUpdating = true;
        try
        {
            var progress = new Progress<double>(p => UpdateProgress = p);
            var path = await Updates.DownloadAsync(info, progress);
            Settings.SaveNow();
            UpdateService.ApplyAndRestart(path);
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            Notify?.Invoke("Update failed", ex.Message);
            IsUpdating = false;
        }
    }

    public void RaiseUpdateChanged()
    {
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(UpdateBannerText));
    }

    public void Dispose()
    {
        Settings.SaveNow();
        Updates.Dispose();
        Bluetooth.Dispose();
        Audio.Dispose();
    }
}
