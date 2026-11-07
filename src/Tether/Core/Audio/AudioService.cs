using System.Runtime.InteropServices;
using Tether.Core.Native;

namespace Tether.Core.Audio;

public sealed record AudioEndpoint(string Id, string Name, string DeviceName, bool IsDefault, bool IsDefaultCommunications);

/// <summary>An audio endpoint that belongs to a Bluetooth device (matched by container ID, address or name).</summary>
internal sealed record BluetoothAudioEndpoint(string EndpointId, bool IsRender, uint State, string? KsFilterId);

/// <summary>Wraps Core Audio: list/switch playback devices, set volume, and Bluetooth audio connect/disconnect.</summary>
public sealed class AudioService : IDisposable
{
    private readonly IMMDeviceEnumerator _notifyEnumerator;
    private readonly NotificationClient _client;
    private Timer? _debounce;

    /// <summary>Raised (debounced, on a worker thread) when endpoints are added/removed/changed or the default changes.</summary>
    public event EventHandler? EndpointsChanged;

    public AudioService()
    {
        _notifyEnumerator = CreateEnumerator();
        _client = new NotificationClient(RaiseChanged);
        _notifyEnumerator.RegisterEndpointNotificationCallback(_client);
    }

    private static IMMDeviceEnumerator CreateEnumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

    private void RaiseChanged()
    {
        _debounce?.Dispose();
        _debounce = new Timer(_ => EndpointsChanged?.Invoke(this, EventArgs.Empty), null, 250, Timeout.Infinite);
    }

    // ---------------------------------------------------------------- playback list / default

    public IReadOnlyList<AudioEndpoint> GetPlaybackDevices()
    {
        var result = new List<AudioEndpoint>();
        var en = CreateEnumerator();
        try
        {
            var defaultId = GetDefaultId(en, ERole.Multimedia);
            var commsId = GetDefaultId(en, ERole.Communications);
            if (en.EnumAudioEndpoints(EDataFlow.Render, DeviceState.Active, out var col) != 0) return result;
            col.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                if (col.Item(i, out var dev) != 0) continue;
                try
                {
                    dev.GetId(out var id);
                    var (name, deviceName, _) = ReadProps(dev);
                    result.Add(new AudioEndpoint(id, name ?? "Audio device", deviceName ?? name ?? "", id == defaultId, id == commsId));
                }
                finally { Marshal.ReleaseComObject(dev); }
            }
            Marshal.ReleaseComObject(col);
        }
        catch (Exception ex) { Log.Error("Enumerating playback devices failed", ex); }
        finally { Marshal.ReleaseComObject(en); }
        return result;
    }

    public string? GetDefaultPlaybackId()
    {
        var en = CreateEnumerator();
        try { return GetDefaultId(en, ERole.Multimedia); }
        finally { Marshal.ReleaseComObject(en); }
    }

    private static string? GetDefaultId(IMMDeviceEnumerator en, ERole role)
    {
        if (en.GetDefaultAudioEndpoint(EDataFlow.Render, role, out var dev) != 0 || dev == null) return null;
        try { dev.GetId(out var id); return id; }
        finally { Marshal.ReleaseComObject(dev); }
    }

    public bool SetDefault(string endpointId, bool includeCommunications)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClientComObject();
            try
            {
                int hr = policy.SetDefaultEndpoint(endpointId, ERole.Console);
                hr |= policy.SetDefaultEndpoint(endpointId, ERole.Multimedia);
                if (includeCommunications) hr |= policy.SetDefaultEndpoint(endpointId, ERole.Communications);
                return hr == 0;
            }
            finally { Marshal.ReleaseComObject(policy); }
        }
        catch (Exception ex)
        {
            Log.Error("Setting default audio device failed", ex);
            return false;
        }
    }

    public bool IsActive(string endpointId)
    {
        var en = CreateEnumerator();
        try
        {
            if (en.GetDevice(endpointId, out var dev) != 0 || dev == null) return false;
            try { dev.GetState(out var s); return s == DeviceState.Active; }
            finally { Marshal.ReleaseComObject(dev); }
        }
        catch { return false; }
        finally { Marshal.ReleaseComObject(en); }
    }

    /// <summary>Sets an endpoint's master volume (0–100).</summary>
    public bool SetVolume(string endpointId, int percent)
    {
        var en = CreateEnumerator();
        try
        {
            if (en.GetDevice(endpointId, out var dev) != 0 || dev == null) return false;
            try
            {
                var iid = CoreAudioGuids.IAudioEndpointVolume;
                if (dev.Activate(ref iid, CoreAudioGuids.ClsCtxAll, IntPtr.Zero, out var o) != 0) return false;
                var vol = (IAudioEndpointVolume)o;
                try
                {
                    var ctx = Guid.Empty;
                    return vol.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, ref ctx) == 0;
                }
                finally { Marshal.ReleaseComObject(vol); }
            }
            finally { Marshal.ReleaseComObject(dev); }
        }
        catch (Exception ex)
        {
            Log.Error("Setting volume failed", ex);
            return false;
        }
        finally { Marshal.ReleaseComObject(en); }
    }

    // ---------------------------------------------------------------- Bluetooth matching

    /// <summary>
    /// Finds the audio endpoints (any state) that belong to a Bluetooth device.
    /// Matches on container ID first, then the BT address inside the KS filter path, then the device name.
    /// </summary>
    internal List<BluetoothAudioEndpoint> FindBluetoothEndpoints(Guid? containerId, string addressHex, string deviceName)
    {
        var matches = new List<BluetoothAudioEndpoint>();
        var en = CreateEnumerator();
        try
        {
            foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
            {
                if (en.EnumAudioEndpoints(flow, DeviceState.All, out var col) != 0) continue;
                col.GetCount(out var count);
                for (uint i = 0; i < count; i++)
                {
                    if (col.Item(i, out var dev) != 0) continue;
                    try
                    {
                        dev.GetId(out var id);
                        dev.GetState(out var state);
                        var (_, ifaceName, cid) = ReadProps(dev);
                        var ksId = GetKsFilterId(dev);

                        bool isBt = ksId != null && ksId.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase)
                                    || ksId != null && ksId.Contains("BTHHFENUM", StringComparison.OrdinalIgnoreCase)
                                    || ksId != null && ksId.Contains("{0000110b", StringComparison.OrdinalIgnoreCase);

                        bool match =
                            (containerId.HasValue && cid.HasValue && cid.Value == containerId.Value) ||
                            (ksId != null && ksId.Contains(addressHex, StringComparison.OrdinalIgnoreCase)) ||
                            (isBt && !string.IsNullOrEmpty(ifaceName) && string.Equals(ifaceName, deviceName, StringComparison.OrdinalIgnoreCase));

                        if (match) matches.Add(new BluetoothAudioEndpoint(id, flow == EDataFlow.Render, state, ksId));
                    }
                    catch (Exception ex) { Log.Warn($"Skipping endpoint while matching: {ex.Message}"); }
                    finally { Marshal.ReleaseComObject(dev); }
                }
                Marshal.ReleaseComObject(col);
            }
        }
        finally { Marshal.ReleaseComObject(en); }

        // Render endpoints first, active ones first.
        return matches.OrderByDescending(m => m.IsRender).ThenBy(m => m.State == DeviceState.Active ? 0 : 1).ToList();
    }

    /// <summary>
    /// Asks the Bluetooth audio driver to connect/disconnect via KSPROPERTY_ONESHOT_RECONNECT / DISCONNECT.
    /// This is the same mechanism Windows' own "Connect" button uses for headphones.
    /// </summary>
    internal bool SendBluetoothOneShot(string endpointId, bool reconnect)
    {
        var en = CreateEnumerator();
        IMMDevice? dev = null;
        IDeviceTopology? topo = null;
        IConnector? conn = null, other = null;
        IKsControl? ks = null;
        try
        {
            if (en.GetDevice(endpointId, out dev) != 0 || dev == null) return false;
            var iid = CoreAudioGuids.IDeviceTopology;
            if (dev.Activate(ref iid, CoreAudioGuids.ClsCtxAll, IntPtr.Zero, out var topoObj) != 0) return false;
            topo = (IDeviceTopology)topoObj;
            if (topo.GetConnector(0, out conn) != 0 || conn == null) return false;
            if (conn.GetConnectedTo(out other) != 0 || other == null) return false;

            var part = (IPart)other;
            var ksIid = CoreAudioGuids.IKsControl;
            if (part.Activate(CoreAudioGuids.ClsCtxAll, ref ksIid, out var ksObj) != 0) return false;
            ks = (IKsControl)ksObj;

            var prop = new KsProperty
            {
                Set = CoreAudioGuids.KsPropSetIdBtAudio,
                Id = reconnect ? CoreAudioGuids.KsPropertyOneShotReconnect : CoreAudioGuids.KsPropertyOneShotDisconnect,
                Flags = CoreAudioGuids.KsPropertyTypeGet,
            };
            int hr = ks.KsProperty(ref prop, (uint)Marshal.SizeOf<KsProperty>(), IntPtr.Zero, 0, out _);
            if (hr != 0) Log.Warn($"KS one-shot {(reconnect ? "reconnect" : "disconnect")} returned 0x{hr:X8}");
            return hr == 0;
        }
        catch (Exception ex)
        {
            Log.Error("Bluetooth audio one-shot failed", ex);
            return false;
        }
        finally
        {
            if (ks != null) Marshal.ReleaseComObject(ks);
            if (other != null) Marshal.ReleaseComObject(other);
            if (conn != null) Marshal.ReleaseComObject(conn);
            if (topo != null) Marshal.ReleaseComObject(topo);
            if (dev != null) Marshal.ReleaseComObject(dev);
            Marshal.ReleaseComObject(en);
        }
    }

    private static string? GetKsFilterId(IMMDevice dev)
    {
        IDeviceTopology? topo = null;
        IConnector? conn = null;
        try
        {
            var iid = CoreAudioGuids.IDeviceTopology;
            if (dev.Activate(ref iid, CoreAudioGuids.ClsCtxAll, IntPtr.Zero, out var o) != 0) return null;
            topo = (IDeviceTopology)o;
            if (topo.GetConnector(0, out conn) != 0 || conn == null) return null;
            return conn.GetDeviceIdConnectedTo(out var id) == 0 ? id : null;
        }
        catch { return null; }
        finally
        {
            if (conn != null) Marshal.ReleaseComObject(conn);
            if (topo != null) Marshal.ReleaseComObject(topo);
        }
    }

    private static (string? name, string? interfaceName, Guid? containerId) ReadProps(IMMDevice dev)
    {
        if (dev.OpenPropertyStore(0 /* STGM_READ */, out var store) != 0 || store == null) return (null, null, null);
        try
        {
            return (ReadString(store, PKeys.DeviceFriendlyName),
                    ReadString(store, PKeys.DeviceInterfaceFriendlyName),
                    ReadGuid(store, PKeys.DeviceContainerId));
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    private static string? ReadString(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var pv) != 0) return null;
        try { return pv.AsString(); } finally { pv.Clear(); }
    }

    private static Guid? ReadGuid(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var pv) != 0) return null;
        try { return pv.AsGuid(); } finally { pv.Clear(); }
    }

    public void Dispose()
    {
        try { _notifyEnumerator.UnregisterEndpointNotificationCallback(_client); } catch { }
        Marshal.ReleaseComObject(_notifyEnumerator);
        _debounce?.Dispose();
    }
}

[ComVisible(true)]
internal sealed class NotificationClient : IMMNotificationClient
{
    private readonly Action _changed;
    public NotificationClient(Action changed) => _changed = changed;
    public void OnDeviceStateChanged(string deviceId, uint newState) => _changed();
    public void OnDeviceAdded(string deviceId) => _changed();
    public void OnDeviceRemoved(string deviceId) => _changed();
    public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
    {
        if (flow == EDataFlow.Render) _changed();
    }
    public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
}
