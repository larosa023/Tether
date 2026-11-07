using System.Text.Json.Serialization;

namespace Tether.Core.Settings;

public enum ThemeMode { System, Light, Dark }
public enum TrayIconMode { Icon, Battery }

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    // Appearance
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    /// <summary>"System" to follow the Windows accent color, otherwise a #RRGGBB hex value.</summary>
    public string Accent { get; set; } = "System";

    // Tray
    public TrayIconMode TrayMode { get; set; } = TrayIconMode.Icon;
    /// <summary>Address of the device whose battery is shown in the tray. Null = lowest connected device.</summary>
    public string? TrayBatteryDevice { get; set; }

    // Behaviour
    public bool StartWithWindows { get; set; }
    public bool ConnectionNotifications { get; set; }
    public bool LowBatteryNotifications { get; set; } = true;
    public int LowBatteryThreshold { get; set; } = 20;
    public bool ShowAudioSection { get; set; } = true;
    public List<string> HiddenDevices { get; set; } = new();

    // Updates
    public bool AutoCheckUpdates { get; set; } = true;
    public bool IncludePrereleases { get; set; }
    public string? SkippedVersion { get; set; }
    public DateTimeOffset? LastUpdateCheck { get; set; }

    // Integrations with the companion apps
    public IntegrationSettings Tonely { get; set; } = new() { ArgumentTemplate = "--preset \"{value}\"", UriTemplate = "tonely://preset/{value}" };
    public IntegrationSettings Faderly { get; set; } = new() { ArgumentTemplate = "--volume {value}", UriTemplate = "faderly://volume/{value}" };

    /// <summary>Per-device profiles keyed by Bluetooth address (AA:BB:CC:DD:EE:FF).</summary>
    public Dictionary<string, DeviceProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DeviceProfile GetOrCreateProfile(string address)
    {
        if (!Profiles.TryGetValue(address, out var p))
        {
            p = new DeviceProfile();
            Profiles[address] = p;
        }
        return p;
    }
}

public sealed class IntegrationSettings
{
    /// <summary>Full path to the companion app's exe. Empty = auto-detect, then fall back to the URI scheme.</summary>
    public string? ExecutablePath { get; set; }
    /// <summary>Command-line template; {value} is replaced with the preset name / volume.</summary>
    public string ArgumentTemplate { get; set; } = "{value}";
    /// <summary>URI template used when no executable is found but the protocol is registered.</summary>
    public string UriTemplate { get; set; } = "";
}

public sealed class DeviceProfile
{
    public bool Enabled { get; set; } = true;
    public string? CustomName { get; set; }

    public bool SetAsDefaultOutput { get; set; }
    public bool SetAsDefaultCommunications { get; set; }
    public bool RestorePreviousOutputOnDisconnect { get; set; }

    /// <summary>0–100. Null = leave volume alone.</summary>
    public int? Volume { get; set; }
    /// <summary>When true the volume is handed to Faderly; otherwise Tether sets the endpoint volume itself.</summary>
    public bool UseFaderlyForVolume { get; set; }

    /// <summary>Tonely EQ preset to activate on connect. Null/empty = none.</summary>
    public string? TonelyPreset { get; set; }

    [JsonIgnore]
    public bool HasActions =>
        SetAsDefaultOutput || SetAsDefaultCommunications || RestorePreviousOutputOnDisconnect ||
        Volume.HasValue || !string.IsNullOrWhiteSpace(TonelyPreset);
}
