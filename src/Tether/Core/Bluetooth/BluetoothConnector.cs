using Tether.Core.Audio;
using Tether.Core.Native;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Tether.Core.Bluetooth;

public sealed class ConnectResult
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public bool NeedsElevation { get; init; }
    public static ConnectResult Ok() => new() { Success = true };
    public static ConnectResult Fail(string msg, bool elevation = false) => new() { Message = msg, NeedsElevation = elevation };
}

/// <summary>
/// Connect / disconnect strategies, best first:
///  1. Bluetooth audio (classic): KSPROPERTY_ONESHOT_RECONNECT / DISCONNECT on the device's audio endpoint.
///  2. Other classic devices (mice, controllers, keyboards): toggle the installed Bluetooth services.
///  3. LE devices: hold a GATT session open to connect; release it to let Windows drop the link.
/// </summary>
public sealed class BluetoothConnector
{
    private readonly AudioService _audio;
    private readonly Dictionary<string, GattSession> _leSessions = new();

    public BluetoothConnector(AudioService audio) => _audio = audio;

    public async Task<ConnectResult> ConnectAsync(BtDevice dev)
    {
        Log.Info($"Connect requested: {dev.Name} ({dev.AddressString})");
        if (dev.IsLowEnergy) return await ConnectLeAsync(dev);

        var endpoints = await Task.Run(() => _audio.FindBluetoothEndpoints(dev.ContainerId, dev.AddressHex, dev.Name));
        foreach (var ep in endpoints)
        {
            if (await Task.Run(() => _audio.SendBluetoothOneShot(ep.EndpointId, reconnect: true)))
                return ConnectResult.Ok();
        }
        return await Task.Run(() => ToggleServices(dev, reconnect: true));
    }

    public async Task<ConnectResult> DisconnectAsync(BtDevice dev)
    {
        Log.Info($"Disconnect requested: {dev.Name} ({dev.AddressString})");
        if (dev.IsLowEnergy) return DisconnectLe(dev);

        var endpoints = await Task.Run(() => _audio.FindBluetoothEndpoints(dev.ContainerId, dev.AddressHex, dev.Name));
        foreach (var ep in endpoints)
        {
            if (await Task.Run(() => _audio.SendBluetoothOneShot(ep.EndpointId, reconnect: false)))
                return ConnectResult.Ok();
        }
        return await Task.Run(() => ToggleServices(dev, reconnect: false));
    }

    /// <summary>The audio endpoint Windows created for this device, if it's active right now.</summary>
    public string? FindActiveRenderEndpoint(BtDevice dev) =>
        _audio.FindBluetoothEndpoints(dev.ContainerId, dev.AddressHex, dev.Name)
              .FirstOrDefault(e => e.IsRender && e.State == DeviceState.Active)?.EndpointId;

    // ------------------------------------------------------------------ classic: service toggling

    /// <summary>
    /// Disabling a device's services makes Windows drop the link; re-enabling them makes it reconnect.
    /// For a disconnect we re-enable right after, so the device keeps working the next time it wakes up
    /// (a mouse reconnects when it's moved, exactly like it does after sleep).
    /// </summary>
    private static ConnectResult ToggleServices(BtDevice dev, bool reconnect)
    {
        var radios = BluetoothApis.GetRadios();
        if (radios.Count == 0) return ConnectResult.Fail("No Bluetooth radio found.");
        try
        {
            var radio = radios[0];
            var info = BluetoothApis.BluetoothDeviceInfo.Create(dev.Address);
            if (BluetoothApis.BluetoothGetDeviceInfo(radio, ref info) != BluetoothApis.ErrorSuccess)
                return ConnectResult.Fail("Windows doesn't know this device any more. Try re-pairing it.");

            uint count = 32;
            var services = new Guid[count];
            if (BluetoothApis.BluetoothEnumerateInstalledServices(radio, ref info, ref count, services) != BluetoothApis.ErrorSuccess || count == 0)
                return ConnectResult.Fail("This device doesn't expose services Tether can toggle.");
            var installed = services.Take((int)count).ToArray();

            foreach (var svc in installed)
            {
                var s = svc;
                int err = BluetoothApis.BluetoothSetServiceState(radio, ref info, ref s, BluetoothApis.ServiceDisable);
                if (err == BluetoothApis.ErrorAccessDenied)
                {
                    ReEnable(radio, info, installed);
                    return ConnectResult.Fail("Windows needs administrator rights to toggle this device.", elevation: true);
                }
            }

            Thread.Sleep(reconnect ? 800 : 1500);
            ReEnable(radio, info, installed);
            return ConnectResult.Ok();
        }
        catch (Exception ex)
        {
            Log.Error("Service toggle failed", ex);
            return ConnectResult.Fail(ex.Message);
        }
        finally
        {
            foreach (var r in radios) BluetoothApis.CloseHandle(r);
        }
    }

    private static void ReEnable(IntPtr radio, BluetoothApis.BluetoothDeviceInfo info, IEnumerable<Guid> services)
    {
        foreach (var svc in services)
        {
            var s = svc;
            var i = info;
            int err = BluetoothApis.BluetoothSetServiceState(radio, ref i, ref s, BluetoothApis.ServiceEnable);
            if (err != BluetoothApis.ErrorSuccess) Log.Warn($"Re-enabling service {svc} returned {err}");
        }
    }

    // ------------------------------------------------------------------ LE

    private async Task<ConnectResult> ConnectLeAsync(BtDevice dev)
    {
        try
        {
            var le = await BluetoothLEDevice.FromIdAsync(dev.Id);
            if (le == null) return ConnectResult.Fail("Device not reachable.");
            var session = await GattSession.FromDeviceIdAsync(le.BluetoothDeviceId);
            session.MaintainConnection = true;
            lock (_leSessions)
            {
                if (_leSessions.Remove(dev.Id, out var old)) old.Dispose();
                _leSessions[dev.Id] = session;
            }
            // Touching GATT uncached forces Windows to actually open the link.
            _ = await le.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            return ConnectResult.Ok();
        }
        catch (Exception ex)
        {
            Log.Error("LE connect failed", ex);
            return ConnectResult.Fail("Couldn't reach the device. Make sure it's on and nearby.");
        }
    }

    private ConnectResult DisconnectLe(BtDevice dev)
    {
        lock (_leSessions)
        {
            if (_leSessions.Remove(dev.Id, out var session))
            {
                session.MaintainConnection = false;
                session.Dispose();
                return ConnectResult.Ok();
            }
        }
        return ConnectResult.Fail("Windows keeps Bluetooth LE input devices connected on its own. Turn the device off or use Remove device in Settings.");
    }
}
