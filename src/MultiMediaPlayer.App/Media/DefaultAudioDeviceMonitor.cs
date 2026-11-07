using System.Runtime.InteropServices;

namespace MultiMediaPlayer.App.Media;

public readonly record struct AudioDeviceState(bool Available, string? Id, string? Error = null);

/// <summary>MMDevice callbacks only arm a timer; COM queries and clients run after the callback returns.</summary>
public sealed class DefaultAudioDeviceMonitor : IDisposable
{
    private readonly object _sync = new();
    private readonly Timer _timer;
    private readonly Func<AudioDeviceState> _query;
    private IMMDeviceEnumerator? _enumerator;
    private NotificationClient? _client;
    private bool _disposed;
    private AudioDeviceState _current;
    public event Action<AudioDeviceState>? Changed;
    public AudioDeviceState Current { get { lock (_sync) return _current; } }

    public DefaultAudioDeviceMonitor()
    {
        _query = QueryDefault;
        _timer = new(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(
                new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), true)!)!;
            _client = new(this);
            Marshal.ThrowExceptionForHR(_enumerator.RegisterEndpointNotificationCallback(_client));
            _current = QueryDefault();
        }
        catch (Exception ex)
        {
            _current = new(false, null, "音频设备监听不可用：" + ex.Message);
            Log.Write(ex.ToString());
        }
    }
    // Uses the same debounce/disposal path in deterministic notification tests.
    internal DefaultAudioDeviceMonitor(Func<AudioDeviceState> query)
    {
        _query = query;
        _current = query();
        _timer = new(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
    }
    internal void NotifyDeviceChanged()
    {
        lock (_sync) if (!_disposed) _timer.Change(300, Timeout.Infinite);
    }
    private void Refresh()
    {
        AudioDeviceState state;
        Action<AudioDeviceState>? handler;
        lock (_sync)
        {
            if (_disposed) return;
            try { state = _query(); }
            catch (Exception ex) { state = new(false, null, "无法读取音频设备：" + ex.Message); }
            if (state == _current) return;
            _current = state;
            handler = Changed;
        }
        try { handler?.Invoke(state); }
        catch (Exception ex) { Log.Write(ex.ToString()); }
    }
    private AudioDeviceState QueryDefault()
    {
        if (_enumerator == null) return new(false, null, "音频设备监听未初始化");
        // DirectSound's default playback device follows the multimedia role.
        if (_enumerator.GetDefaultAudioEndpoint(0, 1, out var device) < 0) return new(false, null);
        try
        {
            Marshal.ThrowExceptionForHR(device.GetId(out var id));
            Marshal.ThrowExceptionForHR(device.GetState(out var state));
            return new((state & 1) != 0, id);
        }
        finally { Marshal.ReleaseComObject(device); }
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
        }
        // Unregister outside the lock: Windows may wait for a notification.
        if (_enumerator != null)
        {
            try
            {
                if (_client != null) _enumerator.UnregisterEndpointNotificationCallback(_client);
                Marshal.ReleaseComObject(_enumerator);
            }
            catch (Exception ex) { Log.Write(ex.ToString()); }
            finally { _enumerator = null; _client = null; }
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class NotificationClient(DefaultAudioDeviceMonitor owner) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string id, uint state) { owner.NotifyDeviceChanged(); return 0; }
        public int OnDeviceAdded(string id) { owner.NotifyDeviceChanged(); return 0; }
        public int OnDeviceRemoved(string id) { owner.NotifyDeviceChanged(); return 0; }
        public int OnDefaultDeviceChanged(int flow, int role, string? id)
        { if (flow == 0 && role is 0 or 1) owner.NotifyDeviceChanged(); return 0; }
        public int OnPropertyValueChanged(string id, PropertyKey key) => 0;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey { public Guid FormatId; public uint PropertyId; }

    [ComVisible(true), Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
    }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint state, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, out IntPtr instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
}
