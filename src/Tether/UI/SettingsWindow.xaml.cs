using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Tether.Core;
using Tether.Core.Bluetooth;
using Tether.Core.Native;
using Tether.Core.Profiles;
using Tether.Core.Settings;
using Tether.Core.Updates;
using Tether.Themes;

namespace Tether.UI;

public partial class SettingsWindow : Window
{
    private sealed record TrayDeviceOption(string? Address, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly ShellViewModel _vm;
    private AppSettings S => _vm.Settings.Current;
    private bool _loading = true;
    private BtDevice? _selected;

    public SettingsWindow(ShellViewModel vm)
    {
        InitializeComponent();
        _vm = vm;

        SourceInitialized += (_, _) => ApplyChrome();
        ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => { ThemeManager.ThemeChanged -= OnThemeChanged; _vm.Settings.SaveNow(); };

        LoadGeneral();
        LoadIntegrations();
        LoadUpdates();
        DeviceList.ItemsSource = _vm.Bluetooth.Devices;
        AboutVersion.Text = $"Version {UpdateService.CurrentVersion}";
        ElevationRow.Visibility = StartupManager.IsElevated() ? Visibility.Collapsed : Visibility.Visible;
        Nav.SelectedIndex = 0;
        _loading = false;
    }

    private void OnThemeChanged(object? s, EventArgs e) { ApplyChrome(); BuildAccentSwatches(); }

    private void ApplyChrome()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) DwmApi.SetDarkTitleBar(hwnd, ThemeManager.IsDark);
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var tag = (Nav.SelectedItem as ListBoxItem)?.Tag as string ?? "General";
        PageGeneral.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
        PageDevices.Visibility = tag == "Devices" ? Visibility.Visible : Visibility.Collapsed;
        PageIntegrations.Visibility = tag == "Integrations" ? Visibility.Visible : Visibility.Collapsed;
        PageUpdates.Visibility = tag == "Updates" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "Integrations") RefreshIntegrationStatus();
        if (tag == "Devices" && DeviceList.SelectedItem == null && DeviceList.Items.Count > 0) DeviceList.SelectedIndex = 0;
    }

    public void SelectProfile(string address)
    {
        Nav.SelectedIndex = 1;
        var dev = _vm.Bluetooth.Devices.FirstOrDefault(d => string.Equals(d.AddressString, address, StringComparison.OrdinalIgnoreCase));
        if (dev != null) DeviceList.SelectedItem = dev;
    }

    // ================================================================== General

    private void LoadGeneral()
    {
        ThemeSystem.IsChecked = S.Theme == ThemeMode.System;
        ThemeLight.IsChecked = S.Theme == ThemeMode.Light;
        ThemeDark.IsChecked = S.Theme == ThemeMode.Dark;
        BuildAccentSwatches();

        TrayModeIcon.IsChecked = S.TrayMode == Core.Settings.TrayIconMode.Icon;
        TrayModeBattery.IsChecked = S.TrayMode == Core.Settings.TrayIconMode.Battery;
        RefreshTrayDeviceOptions();

        StartWithWindows.IsChecked = S.StartWithWindows;
        ConnectionNotifications.IsChecked = S.ConnectionNotifications;
        LowBatteryNotifications.IsChecked = S.LowBatteryNotifications;
        ThresholdSlider.Value = S.LowBatteryThreshold;
        ThresholdText.Text = $"Alert at {S.LowBatteryThreshold}%";
        ShowAudioSection.IsChecked = S.ShowAudioSection;
    }

    private void BuildAccentSwatches()
    {
        AccentPanel.Children.Clear();
        AddSwatch("System", ThemeManager.SystemAccent(), "Windows accent");
        foreach (var (name, hex) in ThemeManager.AccentPresets)
            AddSwatch(hex, (Color)ColorConverter.ConvertFromString(hex), name);
    }

    private void AddSwatch(string value, Color color, string tip)
    {
        var rb = new RadioButton
        {
            GroupName = "accent",
            Style = (Style)FindResource("Swatch"),
            Background = new SolidColorBrush(color),
            ToolTip = tip,
            Tag = value,
            IsChecked = string.Equals(S.Accent, value, StringComparison.OrdinalIgnoreCase),
        };
        rb.Checked += (_, _) =>
        {
            if (_loading) return;
            S.Accent = value;
            _vm.Settings.Save();
            ThemeManager.Apply();
        };
        AccentPanel.Children.Add(rb);
    }

    private void RefreshTrayDeviceOptions()
    {
        var options = new List<TrayDeviceOption> { new(null, "Lowest battery among connected devices") };
        options.AddRange(_vm.Bluetooth.Devices.Select(d => new TrayDeviceOption(d.AddressString, d.DisplayName)));
        TrayDeviceCombo.ItemsSource = options;
        TrayDeviceCombo.SelectedItem = options.FirstOrDefault(o => string.Equals(o.Address, S.TrayBatteryDevice, StringComparison.OrdinalIgnoreCase)) ?? options[0];
        TrayBatteryRow.Visibility = S.TrayMode == Core.Settings.TrayIconMode.Battery ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.Theme = ThemeLight.IsChecked == true ? ThemeMode.Light : ThemeDark.IsChecked == true ? ThemeMode.Dark : ThemeMode.System;
        _vm.Settings.Save();
        ThemeManager.Apply();
    }

    private void Tray_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.TrayMode = TrayModeBattery.IsChecked == true ? Core.Settings.TrayIconMode.Battery : Core.Settings.TrayIconMode.Icon;
        TrayBatteryRow.Visibility = S.TrayMode == Core.Settings.TrayIconMode.Battery ? Visibility.Visible : Visibility.Collapsed;
        _vm.Settings.Save();
    }

    private void TrayDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TrayDeviceCombo.SelectedItem is not TrayDeviceOption o) return;
        S.TrayBatteryDevice = o.Address;
        _vm.Settings.Save();
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.StartWithWindows = StartWithWindows.IsChecked == true;
        S.ConnectionNotifications = ConnectionNotifications.IsChecked == true;
        S.LowBatteryNotifications = LowBatteryNotifications.IsChecked == true;
        S.ShowAudioSection = ShowAudioSection.IsChecked == true;
        S.AutoCheckUpdates = AutoCheckUpdates.IsChecked == true;
        S.IncludePrereleases = IncludePrereleases.IsChecked == true;
        if (ReferenceEquals(sender, StartWithWindows)) StartupManager.SetStartWithWindows(S.StartWithWindows);
        _vm.NotifyLayoutSettingsChanged();
        _vm.Settings.Save();
    }

    private void Threshold_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThresholdText == null) return;
        ThresholdText.Text = $"Alert at {(int)ThresholdSlider.Value}%";
        if (_loading) return;
        S.LowBatteryThreshold = (int)ThresholdSlider.Value;
        _vm.ApplyCustomNames();
        _vm.Settings.Save();
    }

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        _vm.Settings.SaveNow();
        StartupManager.RestartElevated();
    }

    // ================================================================== Devices & profiles

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = DeviceList.SelectedItem as BtDevice;
        NoDeviceSelected.Visibility = _selected == null ? Visibility.Visible : Visibility.Collapsed;
        ProfilePanel.Visibility = _selected == null ? Visibility.Collapsed : Visibility.Visible;
        if (_selected == null) return;

        _loading = true;
        S.Profiles.TryGetValue(_selected.AddressString, out var p);
        ProfileDeviceName.Text = _selected.Name;
        ProfileDeviceAddress.Text = $"{_selected.AddressString} · {(_selected.IsLowEnergy ? "Bluetooth LE" : "Bluetooth Classic")} · {_selected.Kind}";
        CustomNameBox.Text = p?.CustomName ?? "";
        HiddenToggle.IsChecked = S.HiddenDevices.Contains(_selected.AddressString, StringComparer.OrdinalIgnoreCase);
        ProfileEnabled.IsChecked = p?.Enabled ?? true;
        SetDefaultOutput.IsChecked = p?.SetAsDefaultOutput ?? false;
        SetDefaultComms.IsChecked = p?.SetAsDefaultCommunications ?? false;
        RestorePrevious.IsChecked = p?.RestorePreviousOutputOnDisconnect ?? false;
        VolumeEnabled.IsChecked = p?.Volume.HasValue ?? false;
        VolumeSlider.Value = p?.Volume ?? 50;
        VolumeText.Text = $"{(int)VolumeSlider.Value}%";
        UseFaderly.IsChecked = p?.UseFaderlyForVolume ?? false;
        TonelyPresetBox.Text = p?.TonelyPreset ?? "";
        UpdateProfileEnablement();
        _loading = false;
    }

    private void UpdateProfileEnablement()
    {
        ProfileActions.IsEnabled = ProfileEnabled.IsChecked == true;
        ProfileActions.Opacity = ProfileActions.IsEnabled ? 1 : 0.5;
        VolumeSlider.IsEnabled = VolumeEnabled.IsChecked == true;
        UseFaderly.IsEnabled = VolumeEnabled.IsChecked == true;
        SetDefaultComms.IsEnabled = SetDefaultOutput.IsChecked == true;
        RunProfileButton.IsEnabled = _selected?.IsConnected == true;
    }

    private void Profile_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _selected == null) return;
        var p = S.GetOrCreateProfile(_selected.AddressString);
        p.CustomName = string.IsNullOrWhiteSpace(CustomNameBox.Text) ? null : CustomNameBox.Text.Trim();
        p.Enabled = ProfileEnabled.IsChecked == true;
        p.SetAsDefaultOutput = SetDefaultOutput.IsChecked == true;
        p.SetAsDefaultCommunications = p.SetAsDefaultOutput && SetDefaultComms.IsChecked == true;
        p.RestorePreviousOutputOnDisconnect = RestorePrevious.IsChecked == true;
        p.Volume = VolumeEnabled.IsChecked == true ? (int)VolumeSlider.Value : null;
        p.UseFaderlyForVolume = UseFaderly.IsChecked == true;
        p.TonelyPreset = string.IsNullOrWhiteSpace(TonelyPresetBox.Text) ? null : TonelyPresetBox.Text.Trim();

        var hidden = S.HiddenDevices;
        hidden.RemoveAll(a => string.Equals(a, _selected.AddressString, StringComparison.OrdinalIgnoreCase));
        if (HiddenToggle.IsChecked == true) hidden.Add(_selected.AddressString);

        UpdateProfileEnablement();
        _vm.ApplyCustomNames();
        _vm.RefreshDeviceFilter();
        _vm.Settings.Save();
    }

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeText == null) return;
        VolumeText.Text = $"{(int)VolumeSlider.Value}%";
        Profile_Changed(sender, e);
    }

    private async void RunProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || !S.Profiles.TryGetValue(_selected.AddressString, out var p)) return;
        RunProfileButton.IsEnabled = false;
        try { await _vm.Profiles.ApplyAsync(_selected, p); }
        finally { RunProfileButton.IsEnabled = _selected?.IsConnected == true; }
    }

    // ================================================================== Integrations

    private void LoadIntegrations()
    {
        TonelyPath.Text = S.Tonely.ExecutablePath ?? "";
        TonelyArgs.Text = S.Tonely.ArgumentTemplate;
        FaderlyPath.Text = S.Faderly.ExecutablePath ?? "";
        FaderlyArgs.Text = S.Faderly.ArgumentTemplate;
    }

    private void RefreshIntegrationStatus()
    {
        TonelyStatus.Text = Describe("Tonely", S.Tonely);
        FaderlyStatus.Text = Describe("Faderly", S.Faderly);
    }

    private static string Describe(string app, IntegrationSettings cfg)
    {
        var exe = CompanionApps.Resolve(app, cfg);
        if (exe != null) return $"Found: {exe}";
        return CompanionApps.IsAvailable(app, cfg) ? $"Using the {app.ToLowerInvariant()}:// link" : $"{app} not found yet";
    }

    private void Integration_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        S.Tonely.ExecutablePath = string.IsNullOrWhiteSpace(TonelyPath.Text) ? null : TonelyPath.Text.Trim();
        S.Tonely.ArgumentTemplate = string.IsNullOrWhiteSpace(TonelyArgs.Text) ? "--preset \"{value}\"" : TonelyArgs.Text;
        S.Faderly.ExecutablePath = string.IsNullOrWhiteSpace(FaderlyPath.Text) ? null : FaderlyPath.Text.Trim();
        S.Faderly.ArgumentTemplate = string.IsNullOrWhiteSpace(FaderlyArgs.Text) ? "--volume {value}" : FaderlyArgs.Text;
        _vm.Settings.Save();
        RefreshIntegrationStatus();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var app = (string)((Button)sender).Tag;
        var dlg = new OpenFileDialog { Title = $"Find {app}.exe", Filter = "Programs (*.exe)|*.exe", FileName = $"{app}.exe" };
        if (dlg.ShowDialog(this) != true) return;
        if (app == "Tonely") TonelyPath.Text = dlg.FileName; else FaderlyPath.Text = dlg.FileName;
    }

    private void TestIntegration_Click(object sender, RoutedEventArgs e)
    {
        var app = (string)((Button)sender).Tag;
        var cfg = app == "Tonely" ? S.Tonely : S.Faderly;
        var value = app == "Tonely" ? TonelyTestValue.Text : FaderlyTestValue.Text;
        var ok = CompanionApps.Send(app, cfg, value, out var err);
        MessageBox.Show(this, ok ? $"Sent to {app}." : err, "Tether", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    // ================================================================== Updates

    private void LoadUpdates()
    {
        CurrentVersionText.Text = $"Tether {UpdateService.CurrentVersion}";
        AutoCheckUpdates.IsChecked = S.AutoCheckUpdates;
        IncludePrereleases.IsChecked = S.IncludePrereleases;
        ShowUpdateState();
    }

    private void ShowUpdateState()
    {
        var u = _vm.Updates.Available;
        if (u != null)
        {
            UpdateStatusText.Text = $"Version {u.Version} is available.";
            InstallUpdateButton.Visibility = Visibility.Visible;
            ReleaseNotesText.Text = string.IsNullOrWhiteSpace(u.Notes) ? "No notes for this release." : u.Notes.Trim();
        }
        else
        {
            UpdateStatusText.Text = S.LastUpdateCheck is { } t ? $"You're up to date. Last checked {t.LocalDateTime:g}." : "Not checked yet.";
            InstallUpdateButton.Visibility = Visibility.Collapsed;
        }
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking…";
        try
        {
            await _vm.Updates.CheckAsync(manual: true);
            _vm.RaiseUpdateChanged();
            ShowUpdateState();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Couldn't reach GitHub: {ex.Message}";
        }
        finally
        {
            CheckNowButton.IsEnabled = true;
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        var u = _vm.Updates.Available;
        if (u == null) return;
        InstallUpdateButton.IsEnabled = CheckNowButton.IsEnabled = false;
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateStatusText.Text = $"Downloading {u.Version}…";
        try
        {
            var path = await _vm.Updates.DownloadAsync(u, new Progress<double>(p => UpdateProgressBar.Value = p));
            UpdateStatusText.Text = "Verified. Restarting…";
            _vm.Settings.SaveNow();
            UpdateService.ApplyAndRestart(path);
        }
        catch (Exception ex)
        {
            Log.Error("Manual update failed", ex);
            UpdateStatusText.Text = ex.Message;
            InstallUpdateButton.IsEnabled = CheckNowButton.IsEnabled = true;
            UpdateProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private void OpenReleases_Click(object sender, RoutedEventArgs e) =>
        StartupManager.OpenUri($"https://github.com/{UpdateService.Owner}/{UpdateService.Repo}/releases");

    // ================================================================== About

    private void OpenRepo_Click(object sender, RoutedEventArgs e) =>
        StartupManager.OpenUri($"https://github.com/{UpdateService.Owner}/{UpdateService.Repo}");

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SettingsStore.LocalDataDir);
        StartupManager.OpenUri(SettingsStore.LocalDataDir);
    }

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SettingsStore.AppDataDir);
        StartupManager.OpenUri(SettingsStore.AppDataDir);
    }
}
