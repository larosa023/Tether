using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using Tether.Core;
using Tether.Core.Bluetooth;
using Tether.Core.Native;
using Tether.Core.Settings;
using Tether.Themes;
using Application = System.Windows.Application;

namespace Tether.UI;

/// <summary>
/// The notification-area icon: left click opens the flyout, right click shows a quick menu.
/// The icon is drawn at runtime so it can show connection state or a battery percentage.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly ShellViewModel _vm;
    private readonly NotifyIcon _icon;
    private FlyoutWindow? _flyout;
    private SettingsWindow? _settingsWindow;
    private Icon? _currentIcon;
    private string _lastIconKey = "";
    private Action? _balloonAction;

    public TrayIcon(ShellViewModel vm)
    {
        _vm = vm;
        _icon = new NotifyIcon { Text = "Tether", Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleFlyout(); };
        _icon.BalloonTipClicked += (_, _) => { var a = _balloonAction; _balloonAction = null; a?.Invoke(); };
        _icon.ContextMenuStrip = BuildMenu();
        _icon.ContextMenuStrip.Opening += (_, _) => RebuildDeviceItems();

        vm.TrayStateChanged += (_, _) => Application.Current.Dispatcher.InvokeAsync(Refresh);
        vm.Notify += (t, m) => ShowNotification(t, m,
            t.StartsWith("Tether update", StringComparison.Ordinal) ? () => _ = _vm.InstallUpdateAsync() : null);
        vm.OpenSettingsRequested += OpenSettings;
        ThemeManager.ThemeChanged += (_, _) => { _lastIconKey = ""; Refresh(); StyleMenu(); };
        vm.Settings.Changed += (_, _) => Application.Current.Dispatcher.InvokeAsync(Refresh);

        Refresh();
    }

    // ------------------------------------------------------------------ flyout / settings

    private void ToggleFlyout()
    {
        _flyout ??= new FlyoutWindow(_vm);
        if (_flyout.IsVisible) { _flyout.HideFlyout(); return; }
        // A click on the tray icon deactivates the flyout first; don't instantly reopen it.
        if ((DateTime.UtcNow - _flyout.LastHidden).TotalMilliseconds < 300) return;
        _flyout.ShowFlyout();
    }

    public void ShowFlyout()
    {
        _flyout ??= new FlyoutWindow(_vm);
        if (!_flyout.IsVisible) _flyout.ShowFlyout();
        else _flyout.Activate();
    }

    public void OpenSettings(string? deviceAddress)
    {
        _flyout?.HideFlyout();
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_vm);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Show();
        if (_settingsWindow.WindowState == System.Windows.WindowState.Minimized) _settingsWindow.WindowState = System.Windows.WindowState.Normal;
        _settingsWindow.Activate();
        if (deviceAddress != null) _settingsWindow.SelectProfile(deviceAddress);
    }

    public void ShowNotification(string title, string message, Action? onClick = null)
    {
        _balloonAction = onClick;
        _icon.ShowBalloonTip(5000, title, message, ToolTipIcon.None);
    }

    // ------------------------------------------------------------------ menu

    private ToolStripSeparator? _deviceSeparator;

    private ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip { ShowImageMargin = false, Font = new Font("Segoe UI", 9f) };
        _deviceSeparator = new ToolStripSeparator();
        m.Items.Add(_deviceSeparator);
        m.Items.Add("Open Tether", null, (_, _) => ShowFlyout());
        m.Items.Add("Settings", null, (_, _) => OpenSettings(null));
        m.Items.Add("Check for updates", null, async (_, _) => await CheckForUpdatesFromMenu());
        m.Items.Add("Bluetooth settings", null, (_, _) => StartupManager.OpenUri("ms-settings:bluetooth"));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Exit", null, (_, _) => Application.Current.Shutdown());
        StyleMenu(m);
        return m;
    }

    private void RebuildDeviceItems()
    {
        var m = _icon.ContextMenuStrip!;
        while (m.Items.Count > 0 && m.Items[0] != _deviceSeparator) m.Items.RemoveAt(0);

        var devices = _vm.Bluetooth.Devices
            .Where(d => !_vm.Settings.Current.HiddenDevices.Contains(d.AddressString, StringComparer.OrdinalIgnoreCase))
            .Take(8).ToList();
        _deviceSeparator!.Visible = devices.Count > 0;

        for (int i = devices.Count - 1; i >= 0; i--)
        {
            var d = devices[i];
            var label = d.IsConnected
                ? $"✓  {d.DisplayName}{(d.Battery is int b ? $"  ·  {b}%" : "")}"
                : $"    {d.DisplayName}";
            var item = new ToolStripMenuItem(label) { Enabled = !d.IsBusy, ToolTipText = d.IsConnected ? "Click to disconnect" : "Click to connect" };
            item.Click += (_, _) => _ = _vm.ToggleConnectionAsync(d);
            m.Items.Insert(0, item);
        }
    }

    private async Task CheckForUpdatesFromMenu()
    {
        try
        {
            var info = await _vm.Updates.CheckAsync(manual: true);
            _vm.RaiseUpdateChanged();
            if (info == null) ShowNotification("Tether is up to date", $"You have the latest version ({Core.Updates.UpdateService.CurrentVersion}).");
            else ShowNotification("Update available", $"Version {info.Version} is ready. Click to install.", () => _ = _vm.InstallUpdateAsync());
        }
        catch (Exception ex)
        {
            ShowNotification("Couldn't check for updates", ex.Message);
        }
    }

    private void StyleMenu(ContextMenuStrip? m = null)
    {
        m ??= _icon.ContextMenuStrip;
        if (m == null) return;
        bool dark = ThemeManager.IsDark;
        m.Renderer = new ToolStripProfessionalRenderer(new MenuColors(dark, ThemeManager.AccentColor)) { RoundedEdges = false };
        m.BackColor = dark ? Color.FromArgb(0x2A, 0x2A, 0x30) : Color.White;
        m.ForeColor = dark ? Color.FromArgb(0xF4, 0xF4, 0xF6) : Color.FromArgb(0x16, 0x16, 0x1A);
        foreach (ToolStripItem it in m.Items) it.ForeColor = m.ForeColor;
    }

    private sealed class MenuColors : ProfessionalColorTable
    {
        private readonly Color _bg, _hover, _border, _sep;
        public MenuColors(bool dark, System.Windows.Media.Color accent)
        {
            _bg = dark ? Color.FromArgb(0x2A, 0x2A, 0x30) : Color.White;
            _hover = dark ? Color.FromArgb(0x3C, 0x3C, 0x44) : Color.FromArgb(0xEE, 0xEE, 0xF2);
            _border = dark ? Color.FromArgb(0x3A, 0x3A, 0x42) : Color.FromArgb(0xE2, 0xE2, 0xE8);
            _sep = _border;
        }
        public override Color ToolStripDropDownBackground => _bg;
        public override Color MenuBorder => _border;
        public override Color MenuItemBorder => _hover;
        public override Color MenuItemSelected => _hover;
        public override Color MenuItemSelectedGradientBegin => _hover;
        public override Color MenuItemSelectedGradientEnd => _hover;
        public override Color SeparatorDark => _sep;
        public override Color SeparatorLight => _sep;
        public override Color ImageMarginGradientBegin => _bg;
        public override Color ImageMarginGradientMiddle => _bg;
        public override Color ImageMarginGradientEnd => _bg;
    }

    // ------------------------------------------------------------------ icon

    private void Refresh()
    {
        var s = _vm.Settings.Current;
        var connected = _vm.Bluetooth.Devices.Where(d => d.IsConnected).ToList();

        // Tooltip (NotifyIcon caps this at 127 chars).
        var lines = new List<string> { "Tether" };
        if (!_vm.Bluetooth.IsRadioOn) lines.Add("Bluetooth is off");
        else if (connected.Count == 0) lines.Add("No devices connected");
        foreach (var d in connected)
            lines.Add(d.Battery is int b ? $"{d.DisplayName} · {b}%" : d.DisplayName);
        var tip = string.Join("\n", lines);
        _icon.Text = tip.Length > 127 ? tip[..124] + "…" : tip;

        // Icon
        int? battery = null;
        if (s.TrayMode == TrayIconMode.Battery)
        {
            BtDevice? target = null;
            if (!string.IsNullOrEmpty(s.TrayBatteryDevice))
                target = connected.FirstOrDefault(d => string.Equals(d.AddressString, s.TrayBatteryDevice, StringComparison.OrdinalIgnoreCase));
            target ??= connected.Where(d => d.Battery.HasValue).OrderBy(d => d.Battery).FirstOrDefault();
            battery = target?.Battery;
        }

        bool light = ThemeManager.TaskbarUsesLightTheme();
        bool low = battery.HasValue && battery <= s.LowBatteryThreshold;
        var key = $"{light}|{connected.Count > 0}|{_vm.Bluetooth.IsRadioOn}|{battery}|{low}";
        if (key == _lastIconKey) return;
        _lastIconKey = key;

        var icon = battery.HasValue
            ? RenderBatteryIcon(battery.Value, light, low)
            : RenderTetherIcon(light, connected.Count > 0, _vm.Bluetooth.IsRadioOn);
        var old = _currentIcon;
        _icon.Icon = icon;
        _currentIcon = icon;
        if (old != null) { User32.DestroyIcon(old.Handle); old.Dispose(); }
    }

    private static int IconSize()
    {
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        return Math.Clamp((int)Math.Round(16 * g.DpiX / 96.0), 16, 64);
    }

    /// <summary>Two nodes joined by a cord. Filled far node = something's connected; dimmed = radio off.</summary>
    private static Icon RenderTetherIcon(bool lightTaskbar, bool anyConnected, bool radioOn)
    {
        int n = IconSize();
        using var bmp = new Bitmap(n, n);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var fg = lightTaskbar ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.White;
            if (!radioOn) fg = Color.FromArgb(140, fg);
            float u = n / 16f;
            using var pen = new Pen(fg, 1.6f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var brush = new SolidBrush(fg);

            var a = new RectangleF(1.6f * u, 9.2f * u, 5.2f * u, 5.2f * u);
            var b = new RectangleF(9.2f * u, 1.6f * u, 5.2f * u, 5.2f * u);
            g.DrawEllipse(pen, a);
            if (anyConnected) g.FillEllipse(brush, b); else g.DrawEllipse(pen, b);
            g.DrawLine(pen, 6.0f * u, 10.0f * u, 10.0f * u, 6.0f * u);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static Icon RenderBatteryIcon(int pct, bool lightTaskbar, bool low)
    {
        int n = IconSize();
        using var bmp = new Bitmap(n, n);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var fg = low ? Color.FromArgb(0xF8, 0x71, 0x71) : lightTaskbar ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.White;
            var text = pct >= 100 ? "F" : pct.ToString();
            float size = n * (text.Length > 1 ? 0.62f : 0.78f);
            using var font = new Font("Segoe UI Semibold", size, System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(fg);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, brush, new RectangleF(-n * 0.1f, 0, n * 1.2f, n * 0.92f), fmt);
            // Thin level bar along the bottom.
            using var bar = new SolidBrush(Color.FromArgb(200, fg));
            g.FillRectangle(bar, 0, n - Math.Max(2, n / 8), n * Math.Clamp(pct, 0, 100) / 100f, Math.Max(2, n / 8));
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_currentIcon != null) { User32.DestroyIcon(_currentIcon.Handle); _currentIcon.Dispose(); }
        _flyout?.Close();
        _settingsWindow?.Close();
    }
}
