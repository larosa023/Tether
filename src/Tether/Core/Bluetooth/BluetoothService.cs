using System.Collections.ObjectModel;
using System.Windows.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace Tether.Core.Bluetooth;

public sealed class ConnectionChangedEventArgs : EventArgs
{
    public ConnectionChangedEventArgs(BtDevice device, bool connected) { Device = device; Connected = connected; }
    public BtDevice Device { get; }
    public bool Connected { get; }
}

/// <summary>
/// Tracks paired Bluetooth devices (classic + LE) with DeviceWatchers, keeps connection state live and
/// polls battery levels for connected devices. All collection/model changes happen on the UI dispatcher.
/// </summary>
public sealed class BluetoothService : ObservableObject, IDisposable
{
    private const string PropIsConnected = "System.Devices.Aep.IsConnected";
    private const string PropAddress = "System.Devices.Aep.DeviceAddress";
    private const string PropContainerId = "System.Devices.Aep.ContainerId";

    /// <summary>DEVPKEY_Bluetooth_Battery — the value Windows Settings shows under each device.</summary>
    private const string PropBattery = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    private static readonly string[] AepProps = { PropIsConnected, PropAddress, PropContainerId };

    private readonly Dispatcher _ui;
    private DeviceWatcher? _classicWatcher, _leWatcher;
    private readonly HashSet<DeviceWatcher> _enumerated = new();
    private readonly DispatcherTimer _batteryTimer;
    private Radio? _radio;
    private bool _disposed;

    public ObservableCollection<BtDevice> Devices { get; } = new();

    /// <summary>Raised on the UI thread when a device connects or disconnects (not for the initial enumeration).</summary>
    public event EventHandler<ConnectionChangedEventArgs>? ConnectionChanged;
    /// <summary>Raised on the UI thread whenever any battery value changes.</summary>
    public event EventHandler? BatteryChanged;

    private bool _isRadioAvailable;
    public bool IsRadioAvailable { get => _isRadioAvailable; private set => Set(ref _isRadioAvailable, value); }

    private bool _isRadioOn = true;
    public bool IsRadioOn { get => _isRadioOn; private set => Set(ref _isRadioOn, value); }

    private bool _isLoading = true;
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

    public Func<int> LowBatteryThreshold { get; set; } = () => 20;

    public BluetoothService(Dispatcher ui)
    {
        _ui = ui;
        _batteryTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMinutes(2) };
        _batteryTimer.Tick += async (_, _) => await RefreshBatteriesAsync();
    }

    public async Task StartAsync()
    {
        await InitRadioAsync();
        StartWatchers();
        _batteryTimer.Start();
    }

    // ------------------------------------------------------------------ radio

    private async Task InitRadioAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            _radio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            IsRadioAvailable = _radio != null;
            if (_radio != null)
            {
                IsRadioOn = _radio.State == RadioState.On;
                _radio.StateChanged += (r, _) => _ui.InvokeAsync(() =>
                {
                    var on = r.State == RadioState.On;
                    if (on != IsRadioOn)
                    {
                        IsRadioOn = on;
                        if (on) RestartWatchers();
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not read Bluetooth radio", ex);
            IsRadioAvailable = false;
        }
    }

    public async Task<bool> SetRadioAsync(bool on)
    {
        if (_radio == null) return false;
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed)
            {
                Log.Warn($"Radio access: {access}");
                return false;
            }
            var result = await _radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
            return result == RadioAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            Log.Error("Toggling Bluetooth radio failed", ex);
            return false;
        }
    }

    // ------------------------------------------------------------------ watchers

    private void StartWatchers()
    {
        _classicWatcher = CreateWatcher(BluetoothDevice.GetDeviceSelectorFromPairingState(true), isLe: false);
        _leWatcher = CreateWatcher(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), isLe: true);
        _classicWatcher.Start();
        _leWatcher.Start();
    }

    private void RestartWatchers()
    {
        StopWatchers();
        _enumerated.Clear();
        IsLoading = true;
        StartWatchers();
    }

    private void StopWatchers()
    {
        foreach (var w in new[] { _classicWatcher, _leWatcher })
        {
            if (w == null) continue;
            try
            {
                if (w.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) w.Stop();
            }
            catch { }
        }
        _classicWatcher = _leWatcher = null;
    }

    private DeviceWatcher CreateWatcher(string selector, bool isLe)
    {
        var w = DeviceInformation.CreateWatcher(selector, AepProps, DeviceInformationKind.AssociationEndpoint);
        w.Added += (s, info) => _ui.InvokeAsync(() => OnAdded(s, info, isLe));
        w.Updated += (s, upd) => _ui.InvokeAsync(() => OnUpdated(upd));
        w.Removed += (s, upd) => _ui.InvokeAsync(() => OnRemoved(upd));
        w.EnumerationCompleted += (s, _) => _ui.InvokeAsync(() =>
        {
            _enumerated.Add(s);
            if (_enumerated.Count >= 2) IsLoading = false;
            _ = RefreshBatteriesAsync();
        });
        w.Stopped += (s, _) => _ui.InvokeAsync(async () =>
        {
            // Watchers abort when the radio turns off or the stack restarts. Bring them back.
            if (_disposed || s.Status != DeviceWatcherStatus.Aborted) return;
            Log.Warn("Device watcher aborted; restarting");
            await Task.Delay(2000);
            if (!_disposed) RestartWatchers();
        });
        return w;
    }

    private bool InitialEnumerationDone(DeviceWatcher? w) => w != null && _enumerated.Contains(w);

    private void OnAdded(DeviceWatcher source, DeviceInformation info, bool isLe)
    {
        if (!BtDevice.TryParseAddress(Get<string>(info.Properties, PropAddress), out var address)) return;

        var existing = Devices.FirstOrDefault(d => d.Address == address);
        if (existing != null)
        {
            // Dual-mode devices show up in both lists. Prefer the classic entry.
            if (existing.IsLowEnergy && !isLe) Devices.Remove(existing);
            else return;
        }

        var dev = new BtDevice(info.Id, address, isLe)
        {
            Info = info,
            Name = string.IsNullOrWhiteSpace(info.Name) ? BtDevice.FormatAddress(address) : info.Name,
            ContainerId = Get<Guid?>(info.Properties, PropContainerId),
            IsConnected = Get<bool?>(info.Properties, PropIsConnected) ?? false,
            LowThreshold = LowBatteryThreshold(),
        };
        dev.Kind = GuessKindFromName(dev.Name);
        InsertSorted(dev);
        _ = ClassifyAsync(dev);
        if (dev.IsConnected) _ = RefreshBatteryAsync(dev);

        if (InitialEnumerationDone(source) && dev.IsConnected)
            ConnectionChanged?.Invoke(this, new ConnectionChangedEventArgs(dev, true));
    }

    private void OnUpdated(DeviceInformationUpdate upd)
    {
        var dev = Devices.FirstOrDefault(d => d.Id == upd.Id);
        if (dev?.Info == null) return;
        dev.Info.Update(upd);
        if (!string.IsNullOrWhiteSpace(dev.Info.Name)) dev.Name = dev.Info.Name;
        var cid = Get<Guid?>(dev.Info.Properties, PropContainerId);
        if (cid.HasValue) dev.ContainerId = cid;

        var connected = Get<bool?>(dev.Info.Properties, PropIsConnected) ?? dev.IsConnected;
        if (connected == dev.IsConnected) return;

        dev.IsConnected = connected;
        Resort();
        Log.Info($"{dev.Name} {(connected ? "connected" : "disconnected")}");
        if (connected)
        {
            // Windows populates the battery property a few seconds after connecting.
            _ = RefreshBatteryLaterAsync(dev, 4000);
            _ = RefreshBatteryLaterAsync(dev, 20000);
        }
        else
        {
            dev.LowBatteryNotified = false;
        }
        ConnectionChanged?.Invoke(this, new ConnectionChangedEventArgs(dev, connected));
        BatteryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnRemoved(DeviceInformationUpdate upd)
    {
        var dev = Devices.FirstOrDefault(d => d.Id == upd.Id);
        if (dev != null) Devices.Remove(dev);
    }

    private void InsertSorted(BtDevice dev)
    {
        Devices.Add(dev);
        Resort();
    }

    /// <summary>Connected first, then alphabetical.</summary>
    public void Resort()
    {
        var sorted = Devices.OrderByDescending(d => d.IsConnected).ThenBy(d => d.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            var cur = Devices.IndexOf(sorted[i]);
            if (cur != i) Devices.Move(cur, i);
        }
    }

    // ------------------------------------------------------------------ classification

    private async Task ClassifyAsync(BtDevice dev)
    {
        try
        {
            DeviceKind? kind = null;
            if (!dev.IsLowEnergy)
            {
                using var bd = await BluetoothDevice.FromIdAsync(dev.Id);
                if (bd != null) kind = FromClassOfDevice(bd.ClassOfDevice.RawValue);
            }
            else
            {
                using var le = await BluetoothLEDevice.FromIdAsync(dev.Id);
                if (le != null) kind = FromAppearance(le.Appearance.Category, le.Appearance.SubCategory);
            }
            if (kind is { } k && k != DeviceKind.Other) dev.Kind = k;
        }
        catch (Exception ex)
        {
            Log.Warn($"Classifying {dev.Name} failed: {ex.Message}");
        }
    }

    internal static DeviceKind FromClassOfDevice(uint cod)
    {
        uint major = (cod >> 8) & 0x1F;
        uint minor = (cod >> 2) & 0x3F;
        switch (major)
        {
            case 1: return DeviceKind.Computer;
            case 2: return DeviceKind.Phone;
            case 7: return DeviceKind.Watch;
            case 4:
                return minor switch
                {
                    1 or 2 => DeviceKind.Headset,           // wearable headset, hands-free
                    5 or 7 or 8 or 10 => DeviceKind.Speaker, // loudspeaker, portable, car audio, HiFi
                    _ => DeviceKind.Headphones,
                };
            case 5:
                uint kp = (minor >> 4) & 0x3; // 1 keyboard, 2 pointing, 3 combo
                uint sub = minor & 0xF;       // 1 joystick, 2 gamepad
                if (sub is 1 or 2) return DeviceKind.Gamepad;
                if (kp == 2) return DeviceKind.Mouse;
                if (kp is 1 or 3) return DeviceKind.Keyboard;
                return DeviceKind.Other;
            default: return DeviceKind.Other;
        }
    }

    internal static DeviceKind FromAppearance(ushort category, ushort sub) => category switch
    {
        0x01 => DeviceKind.Phone,
        0x02 => DeviceKind.Computer,
        0x03 => DeviceKind.Watch,
        0x0F => sub switch { 1 => DeviceKind.Keyboard, 2 => DeviceKind.Mouse, 3 or 4 => DeviceKind.Gamepad, _ => DeviceKind.Other },
        0x21 => DeviceKind.Speaker,
        0x25 => sub == 2 ? DeviceKind.Headset : DeviceKind.Headphones,
        _ => DeviceKind.Other,
    };

    internal static DeviceKind GuessKindFromName(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("dualsense") || n.Contains("joy-con") || n.Contains("xbox")) return DeviceKind.Gamepad;
        if (n.Contains("mouse") || n.Contains("mx master") || n.Contains("trackpad")) return DeviceKind.Mouse;
        if (n.Contains("keyboard") || n.Contains("keys")) return DeviceKind.Keyboard;
        if (n.Contains("headset")) return DeviceKind.Headset;
        if (n.Contains("bud") || n.Contains("ear") || n.Contains("pods") || n.Contains("headphone") || n.Contains("wh-") || n.Contains("wf-") || n.Contains("qc")) return DeviceKind.Headphones;
        if (n.Contains("speaker") || n.Contains("soundbar") || n.Contains("jbl")) return DeviceKind.Speaker;
        if (n.Contains("phone") || n.Contains("galaxy") || n.Contains("pixel")) return DeviceKind.Phone;
        if (n.Contains("watch")) return DeviceKind.Watch;
        return DeviceKind.Other;
    }

    // ------------------------------------------------------------------ battery

    private async Task RefreshBatteryLaterAsync(BtDevice dev, int delayMs)
    {
        await Task.Delay(delayMs);
        if (dev.IsConnected) await RefreshBatteryAsync(dev);
    }

    public async Task RefreshBatteriesAsync()
    {
        foreach (var dev in Devices.Where(d => d.IsConnected).ToList())
            await RefreshBatteryAsync(dev);
    }

    private async Task RefreshBatteryAsync(BtDevice dev)
    {
        int? level = null;
        try
        {
            level = await ReadPnpBatteryAsync(dev);
            if (level == null && dev.IsLowEnergy) level = await ReadGattBatteryAsync(dev);
        }
        catch (Exception ex)
        {
            Log.Warn($"Battery read for {dev.Name} failed: {ex.Message}");
        }

        if (level != dev.Battery && (level.HasValue || !dev.IsConnected))
        {
            dev.Battery = level;
            BatteryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Reads DEVPKEY_Bluetooth_Battery from the PnP device nodes that share this device's container
    /// (that's where the hands-free / AVRCP / HID drivers publish it).
    /// </summary>
    private static async Task<int?> ReadPnpBatteryAsync(BtDevice dev)
    {
        if (dev.ContainerId is not { } cid) return null;
        var aqs = $"System.Devices.ContainerId:=\"{{{cid}}}\"";
        var nodes = await DeviceInformation.FindAllAsync(aqs, new[] { PropBattery }, DeviceInformationKind.Device);
        int? best = null;
        foreach (var node in nodes)
        {
            if (node.Properties.TryGetValue(PropBattery, out var v) && v != null)
            {
                int pct = Convert.ToInt32(v);
                if (pct is >= 0 and <= 100) best = best.HasValue ? Math.Max(best.Value, pct) : pct;
            }
        }
        return best;
    }

    /// <summary>Fallback for LE devices: read the standard GATT Battery Level characteristic.</summary>
    private static async Task<int?> ReadGattBatteryAsync(BtDevice dev)
    {
        using var le = await BluetoothLEDevice.FromIdAsync(dev.Id);
        if (le == null) return null;
        var services = await le.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached);
        if (services.Status != GattCommunicationStatus.Success) return null;
        foreach (var svc in services.Services)
        {
            using (svc)
            {
                var chars = await svc.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Cached);
                if (chars.Status != GattCommunicationStatus.Success) continue;
                foreach (var c in chars.Characteristics)
                {
                    var r = await c.ReadValueAsync(BluetoothCacheMode.Uncached);
                    if (r.Status == GattCommunicationStatus.Success && r.Value.Length > 0)
                    {
                        using var reader = Windows.Storage.Streams.DataReader.FromBuffer(r.Value);
                        return reader.ReadByte();
                    }
                }
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ helpers

    private static T? Get<T>(IReadOnlyDictionary<string, object> props, string key)
    {
        if (props.TryGetValue(key, out var v) && v is T t) return t;
        if (v != null && typeof(T) == typeof(Guid?) && v is Guid g) return (T)(object)g;
        if (v != null && typeof(T) == typeof(bool?) && v is bool b) return (T)(object)b;
        return default;
    }

    public void Dispose()
    {
        _disposed = true;
        _batteryTimer.Stop();
        StopWatchers();
    }
}
