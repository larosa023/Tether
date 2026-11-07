using Tether.Core.Audio;
using Tether.Core.Bluetooth;
using Tether.Core.Settings;

namespace Tether.Core.Profiles;

/// <summary>Runs a device's profile when it connects (and the restore step when it disconnects).</summary>
public sealed class ProfileService
{
    private readonly BluetoothService _bt;
    private readonly BluetoothConnector _connector;
    private readonly AudioService _audio;
    private readonly SettingsStore _settings;
    private readonly Dictionary<ulong, string> _previousOutput = new();

    /// <summary>(title, message) — surfaced as a tray notification.</summary>
    public event Action<string, string>? Notify;

    public ProfileService(BluetoothService bt, BluetoothConnector connector, AudioService audio, SettingsStore settings)
    {
        _bt = bt;
        _connector = connector;
        _audio = audio;
        _settings = settings;
        _bt.ConnectionChanged += OnConnectionChanged;
    }

    private void OnConnectionChanged(object? sender, ConnectionChangedEventArgs e)
    {
        if (!_settings.Current.Profiles.TryGetValue(e.Device.AddressString, out var profile) || !profile.Enabled) return;
        _ = e.Connected ? ApplyAsync(e.Device, profile) : RevertAsync(e.Device, profile);
    }

    /// <summary>Applies a profile now. Also used by the "Run now" button in Settings.</summary>
    public async Task ApplyAsync(BtDevice dev, DeviceProfile p)
    {
        var name = dev.DisplayName;
        var problems = new List<string>();
        try
        {
            string? endpoint = null;
            bool needsEndpoint = p.SetAsDefaultOutput || p.SetAsDefaultCommunications || (p.Volume.HasValue && !p.UseFaderlyForVolume);
            if (needsEndpoint)
            {
                endpoint = await WaitForEndpointAsync(dev, TimeSpan.FromSeconds(15));
                if (endpoint == null) problems.Add("its audio output never appeared");
            }

            if (endpoint != null && (p.SetAsDefaultOutput || p.SetAsDefaultCommunications))
            {
                var prev = _audio.GetDefaultPlaybackId();
                if (prev != null && prev != endpoint) _previousOutput[dev.Address] = prev;
                if (!_audio.SetDefault(endpoint, p.SetAsDefaultCommunications)) problems.Add("couldn't switch the default output");
            }

            if (p.Volume is int vol)
            {
                if (p.UseFaderlyForVolume)
                {
                    if (!CompanionApps.Send("Faderly", _settings.Current.Faderly, vol.ToString(), out var err)) problems.Add(err!);
                }
                else if (endpoint != null && !_audio.SetVolume(endpoint, vol))
                {
                    problems.Add("couldn't set the volume");
                }
            }

            if (!string.IsNullOrWhiteSpace(p.TonelyPreset))
            {
                // Give Windows a moment to finish moving streams to the new endpoint before the EQ switches.
                await Task.Delay(400);
                if (!CompanionApps.Send("Tonely", _settings.Current.Tonely, p.TonelyPreset!, out var err)) problems.Add(err!);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Profile for {name} failed", ex);
            problems.Add(ex.Message);
        }

        if (problems.Count > 0) Notify?.Invoke($"{name} profile", "Partly applied: " + string.Join("; ", problems) + ".");
        else Log.Info($"Applied profile for {name}");
    }

    private async Task RevertAsync(BtDevice dev, DeviceProfile p)
    {
        if (!p.RestorePreviousOutputOnDisconnect) return;
        if (!_previousOutput.Remove(dev.Address, out var prev)) return;
        // Windows may have already chosen a fallback; only override it if the old device is still around.
        await Task.Delay(800);
        if (_audio.IsActive(prev)) _audio.SetDefault(prev, p.SetAsDefaultCommunications);
    }

    private async Task<string?> WaitForEndpointAsync(BtDevice dev, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var ep = await Task.Run(() => _connector.FindActiveRenderEndpoint(dev));
            if (ep != null) return ep;
            await Task.Delay(500);
        }
        return null;
    }
}
