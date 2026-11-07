using Windows.Devices.Enumeration;

namespace Tether.Core.Bluetooth;

public enum DeviceKind { Headphones, Headset, Speaker, Mouse, Keyboard, Gamepad, Phone, Computer, Watch, Other }

/// <summary>One paired Bluetooth device, as shown in the flyout. Mutated only on the UI thread.</summary>
public sealed class BtDevice : ObservableObject
{
    internal DeviceInformation? Info { get; set; }

    public BtDevice(string id, ulong address, bool isLowEnergy)
    {
        Id = id;
        Address = address;
        IsLowEnergy = isLowEnergy;
    }

    /// <summary>Association-endpoint device ID from Windows.Devices.Enumeration.</summary>
    public string Id { get; }
    public ulong Address { get; }
    public bool IsLowEnergy { get; }

    public string AddressString => FormatAddress(Address);
    /// <summary>12 uppercase hex chars, as it appears in BTHENUM instance paths.</summary>
    public string AddressHex => Address.ToString("X12");

    private string _name = "";
    public string Name { get => _name; set { if (Set(ref _name, value)) OnPropertyChanged(nameof(DisplayName)); } }

    private string? _customName;
    public string? CustomName { get => _customName; set { if (Set(ref _customName, value)) OnPropertyChanged(nameof(DisplayName)); } }

    public string DisplayName => string.IsNullOrWhiteSpace(CustomName) ? Name : CustomName!;

    private Guid? _containerId;
    public Guid? ContainerId { get => _containerId; set => Set(ref _containerId, value); }

    private DeviceKind _kind = DeviceKind.Other;
    public DeviceKind Kind
    {
        get => _kind;
        set { if (Set(ref _kind, value)) { OnPropertyChanged(nameof(Glyph)); OnPropertyChanged(nameof(IsAudio)); } }
    }

    public bool IsAudio => Kind is DeviceKind.Headphones or DeviceKind.Headset or DeviceKind.Speaker;

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (Set(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(ActionText));
                OnPropertyChanged(nameof(HasBattery));
            }
        }
    }

    private int? _battery;
    public int? Battery
    {
        get => _battery;
        set
        {
            if (Set(ref _battery, value))
            {
                OnPropertyChanged(nameof(HasBattery));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(BatteryGlyph));
                OnPropertyChanged(nameof(IsBatteryLow));
            }
        }
    }

    public bool HasBattery => Battery.HasValue && IsConnected;

    private int _lowThreshold = 20;
    internal int LowThreshold { get => _lowThreshold; set { _lowThreshold = value; OnPropertyChanged(nameof(IsBatteryLow)); } }
    public bool IsBatteryLow => Battery.HasValue && Battery.Value <= _lowThreshold;

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { if (Set(ref _isBusy, value)) { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(ActionText)); } }
    }

    private string? _busyText;
    public string? BusyText { get => _busyText; set { if (Set(ref _busyText, value)) OnPropertyChanged(nameof(StatusText)); } }

    private bool _hasProfile;
    public bool HasProfile { get => _hasProfile; set => Set(ref _hasProfile, value); }

    private bool _isDefaultOutput;
    public bool IsDefaultOutput { get => _isDefaultOutput; set => Set(ref _isDefaultOutput, value); }

    /// <summary>Set once the battery has been reported low, cleared when it recovers; prevents repeat toasts.</summary>
    internal bool LowBatteryNotified { get; set; }

    public string StatusText
    {
        get
        {
            if (IsBusy) return BusyText ?? "Working…";
            if (!IsConnected) return "Not connected";
            return Battery.HasValue ? $"Connected · {Battery}%" : "Connected";
        }
    }

    public string ActionText => IsBusy ? "…" : IsConnected ? "Disconnect" : "Connect";

    /// <summary>Segoe Fluent Icons / MDL2 glyphs.</summary>
    public string Glyph => Kind switch
    {
        DeviceKind.Headphones => "",
        DeviceKind.Headset => "",
        DeviceKind.Speaker => "",
        DeviceKind.Mouse => "",
        DeviceKind.Keyboard => "",
        DeviceKind.Gamepad => "",
        DeviceKind.Phone => "",
        DeviceKind.Computer => "",
        DeviceKind.Watch => "",
        _ => "",
    };

    public string BatteryGlyph => Battery switch
    {
        null => "",
        >= 95 => "",
        >= 85 => "",
        >= 75 => "",
        >= 65 => "",
        >= 55 => "",
        >= 45 => "",
        >= 35 => "",
        >= 25 => "",
        >= 15 => "",
        >= 5 => "",
        _ => "",
    };

    public static string FormatAddress(ulong a) =>
        string.Join(":", Enumerable.Range(0, 6).Select(i => ((a >> (8 * (5 - i))) & 0xFF).ToString("X2")));

    public static bool TryParseAddress(string? s, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var hex = s.Replace(":", "").Replace("-", "").Trim();
        return hex.Length == 12 && ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out address);
    }
}
