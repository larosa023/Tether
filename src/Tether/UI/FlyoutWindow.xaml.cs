using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Tether.Core.Bluetooth;
using Tether.Core.Native;
using Tether.Core.Updates;
using Tether.Themes;

namespace Tether.UI;

public partial class FlyoutWindow : Window
{
    private readonly ShellViewModel _vm;
    public DateTime LastHidden { get; private set; } = DateTime.MinValue;

    public FlyoutWindow(ShellViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        VersionText.Text = $"v{UpdateService.CurrentVersion}";

        SourceInitialized += (_, _) => ApplyChrome();
        ThemeManager.ThemeChanged += (_, _) => ApplyChrome();
        Deactivated += (_, _) => HideFlyout();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) HideFlyout(); };
        SizeChanged += (_, _) => { if (IsVisible) PositionNearTray(); };
    }

    private void ApplyChrome()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        DwmApi.SetRoundedCorners(hwnd);
        DwmApi.SetDarkTitleBar(hwnd, ThemeManager.IsDark);
        if (TryFindResource("Stroke") is SolidColorBrush b) DwmApi.SetBorderColor(hwnd, b.Color);
    }

    public void ShowFlyout()
    {
        _vm.ApplyCustomNames();
        _vm.RefreshOutputs();
        _ = _vm.Bluetooth.RefreshBatteriesAsync();

        Opacity = 0;
        Show();
        UpdateLayout();
        PositionNearTray();
        Opacity = 1;
        Activate();
        User32.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        Focus();
    }

    public void HideFlyout()
    {
        if (!IsVisible) return;
        LastHidden = DateTime.UtcNow;
        Hide();
    }

    /// <summary>Bottom-right above the taskbar (or wherever the taskbar is) on the screen under the cursor.</summary>
    private void PositionNearTray()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var work = screen.WorkingArea;
        var bounds = screen.Bounds;
        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
        const double margin = 12;

        double w = ActualWidth, h = ActualHeight;
        double left = work.Right / sx - w - margin;
        double top = work.Bottom / sy - h - margin;

        if (work.Top > bounds.Top) top = work.Top / sy + margin;          // taskbar on top
        else if (work.Left > bounds.Left) left = work.Left / sx + margin; // taskbar on left
        // taskbar on right: Right is already the work-area edge.

        Left = Math.Max(work.Left / sx + margin, left);
        Top = Math.Max(work.Top / sy + margin, top);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _ = _vm.Bluetooth.RefreshBatteriesAsync();
        _vm.RefreshOutputs();
    }

    private static BtDevice? DeviceFrom(object sender) =>
        (sender as FrameworkElement)?.DataContext as BtDevice;

    private void Menu_Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceFrom(sender) is { } d) _ = _vm.ToggleConnectionAsync(d);
    }

    private void Menu_UseOutput_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceFrom(sender) is { } d) _vm.UseAsOutputCommand.Execute(d);
    }

    private void Menu_Profile_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceFrom(sender) is { } d) _vm.OpenSettingsCommand.Execute(d.AddressString);
    }

    private void Menu_Hide_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceFrom(sender) is not { } d) return;
        var hidden = _vm.Settings.Current.HiddenDevices;
        if (!hidden.Contains(d.AddressString, StringComparer.OrdinalIgnoreCase)) hidden.Add(d.AddressString);
        _vm.Settings.Save();
        _vm.RefreshDeviceFilter();
    }
}
