using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Tether.Core.Settings;

namespace Tether.Themes;

/// <summary>
/// Swaps brush resources at runtime. Every control uses DynamicResource, so a theme change applies instantly.
/// </summary>
public static class ThemeManager
{
    public static event EventHandler? ThemeChanged;

    public static bool IsDark { get; private set; }
    public static Color AccentColor { get; private set; }

    public static readonly (string Name, string Hex)[] AccentPresets =
    {
        ("Indigo", "#5B5BF0"), ("Teal", "#14B8A6"), ("Sky", "#0EA5E9"), ("Rose", "#F43F5E"),
        ("Amber", "#F59E0B"), ("Lime", "#65A30D"), ("Violet", "#8B5CF6"), ("Slate", "#64748B"),
    };

    private static AppSettings? _settings;

    public static void Initialize(AppSettings settings)
    {
        _settings = settings;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Application.Current?.Dispatcher.InvokeAsync(Apply);
        };
        Apply();
    }

    public static void Apply()
    {
        if (_settings == null) return;
        IsDark = _settings.Theme switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => !AppsUseLightTheme(),
        };
        AccentColor = ResolveAccent(_settings.Accent);

        var r = Application.Current.Resources;
        if (IsDark)
        {
            SetBrush(r, "WindowBg", "#1F1F23");
            SetBrush(r, "Surface", "#2A2A30");
            SetBrush(r, "SurfaceHover", "#34343B");
            SetBrush(r, "SurfacePressed", "#3C3C44");
            SetBrush(r, "Stroke", "#3A3A42");
            SetBrush(r, "TextPrimary", "#F4F4F6");
            SetBrush(r, "TextSecondary", "#A1A1AA");
            SetBrush(r, "TextDisabled", "#6B6B74");
            SetBrush(r, "Success", "#34D399");
            SetBrush(r, "Danger", "#F87171");
            SetBrush(r, "TrackOff", "#4A4A52");
        }
        else
        {
            SetBrush(r, "WindowBg", "#F7F7F9");
            SetBrush(r, "Surface", "#FFFFFF");
            SetBrush(r, "SurfaceHover", "#F0F0F3");
            SetBrush(r, "SurfacePressed", "#E7E7EC");
            SetBrush(r, "Stroke", "#E2E2E8");
            SetBrush(r, "TextPrimary", "#16161A");
            SetBrush(r, "TextSecondary", "#62626C");
            SetBrush(r, "TextDisabled", "#A8A8B0");
            SetBrush(r, "Success", "#059669");
            SetBrush(r, "Danger", "#DC2626");
            SetBrush(r, "TrackOff", "#C9C9D1");
        }

        r["Accent"] = Freeze(new SolidColorBrush(AccentColor));
        r["AccentSoft"] = Freeze(new SolidColorBrush(Color.FromArgb(IsDark ? (byte)0x33 : (byte)0x22, AccentColor.R, AccentColor.G, AccentColor.B)));
        r["AccentText"] = Freeze(new SolidColorBrush(Luminance(AccentColor) > 0.6 ? Colors.Black : Colors.White));
        r["AccentColor"] = AccentColor;

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    public static Color ResolveAccent(string accent)
    {
        if (!string.Equals(accent, "System", StringComparison.OrdinalIgnoreCase))
        {
            try { return (Color)ColorConverter.ConvertFromString(accent); } catch { }
        }
        return SystemAccent();
    }

    public static Color SystemAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
                return Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
        }
        catch { }
        return (Color)ColorConverter.ConvertFromString("#5B5BF0");
    }

    public static bool AppsUseLightTheme() => ReadPersonalize("AppsUseLightTheme", 1) == 1;
    public static bool TaskbarUsesLightTheme() => ReadPersonalize("SystemUsesLightTheme", 0) == 1;

    private static int ReadPersonalize(string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue(name) is int v) return v;
        }
        catch { }
        return fallback;
    }

    private static void SetBrush(ResourceDictionary r, string key, string hex) =>
        r[key] = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
}
