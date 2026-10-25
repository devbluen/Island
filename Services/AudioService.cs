using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Island;

// Lê o volume do Windows pela API Core Audio (a mesma que o mixer do sistema usa).
// Só altera o volume quando você pede ao assistente.
public sealed class AudioService : IDisposable
{
    readonly Dispatcher _ui;
    readonly VolumeCallback _volumeCallback;
    readonly NotificationClient _notificationClient;
    IMMDeviceEnumerator? _enumerator;
    IAudioEndpointVolume? _volume;
    string? _deviceName;
    float _lastVolume = -1;
    bool _lastMuted;

    public float Volume => _lastVolume;
    public bool Muted => _lastMuted;
    public string? DeviceName => _deviceName;

    public event Action<float, bool>? VolumeChanged;
    public event Action<string>? DeviceChanged;

    public AudioService(Dispatcher ui)
    {
        _ui = ui;
        _volumeCallback = new VolumeCallback(this);
        _notificationClient = new NotificationClient(this);
    }

    public void Start()
    {
        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
        Bind();
    }

    void Bind()
    {
        Unbind();
        if (_enumerator == null) return;
        if (_enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out var device) != 0 || device == null) return;

        var iid = typeof(IAudioEndpointVolume).GUID;
        if (device.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out var obj) != 0) return;

        _volume = (IAudioEndpointVolume)obj;
        _volume.RegisterControlChangeNotify(_volumeCallback);
        _volume.GetMasterVolumeLevelScalar(out _lastVolume);
        _volume.GetMute(out _lastMuted);
        _deviceName = GetFriendlyName(device);
    }

    // Usado só pelo assistente, quando você pede ("abaixa o volume").
    public void SetVolume(float level)
    {
        var ctx = Guid.Empty;
        _volume?.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref ctx);
    }

    public void SetMute(bool mute)
    {
        var ctx = Guid.Empty;
        _volume?.SetMute(mute, ref ctx);
    }

    void Unbind()
    {
        if (_volume == null) return;
        try { _volume.UnregisterControlChangeNotify(_volumeCallback); } catch { }
        Marshal.ReleaseComObject(_volume);
        _volume = null;
    }

    internal void OnVolume(float volume, bool muted) => _ui.BeginInvoke(() =>
    {
        if (Math.Abs(volume - _lastVolume) < 0.001f && muted == _lastMuted) return;
        _lastVolume = volume;
        _lastMuted = muted;
        VolumeChanged?.Invoke(volume, muted);
    });

    // ---------------- Volume por aplicativo (o mesmo do Mixer de Volume do Windows) ----------------

    public sealed record AppSession(string Name, int Pid, float Volume, bool Muted);

    // Navegadores e afins: "o navegador" vale para qualquer um deles.
    static readonly string[] Browsers = { "chrome", "msedge", "firefox", "opera", "brave", "vivaldi" };

    public System.Collections.Generic.List<AppSession> ListAppSessions() =>
        ForEachSession((name, pid, vol) =>
        {
            vol.GetMasterVolume(out float v);
            vol.GetMute(out bool m);
            return new AppSession(name, pid, v, m);
        });

    // Ajusta o volume de um app pelo nome do processo ("spotify", "discord", "navegador"…).
    // level/delta de 0 a 1. Devolve os apps alterados com o volume final.
    public System.Collections.Generic.List<AppSession> SetAppVolume(string app, float? level, float? delta, bool? mute)
    {
        var key = new string(app.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        bool browser = key is "navegador" or "browser" or "navegador web" or "internet";
        return ForEachSession((name, pid, vol) =>
        {
            var n = name.ToLowerInvariant();
            if (!(browser ? Browsers.Contains(n) : n.Contains(key) || key.Contains(n))) return null;
            var ctx = Guid.Empty;
            vol.GetMasterVolume(out float v);
            if (level is float l) v = l;
            if (delta is float d) v += d;
            v = Math.Clamp(v, 0f, 1f);
            if (level != null || delta != null) vol.SetMasterVolume(v, ref ctx);
            if (mute is bool mu) vol.SetMute(mu, ref ctx);
            vol.GetMute(out bool muted);
            return new AppSession(name, pid, v, muted);
        });
    }

    // Percorre as sessões de áudio de todas as saídas ativas (um app pode tocar em mais de uma).
    System.Collections.Generic.List<AppSession> ForEachSession(Func<string, int, ISimpleAudioVolume, AppSession?> action)
    {
        var result = new System.Collections.Generic.List<AppSession>();
        if (_enumerator == null || _enumerator.EnumAudioEndpoints(0 /*eRender*/, 1 /*ACTIVE*/, out var devices) != 0 || devices == null)
            return result;
        devices.GetCount(out uint count);
        for (uint i = 0; i < count; i++)
        {
            if (devices.Item(i, out var dev) != 0 || dev == null) continue;
            try
            {
                var iid = typeof(IAudioSessionManager2).GUID;
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out var obj) != 0) continue;
                var manager = (IAudioSessionManager2)obj;
                if (manager.GetSessionEnumerator(out var sessions) != 0 || sessions == null) continue;
                sessions.GetCount(out int n);
                for (int s = 0; s < n; s++)
                {
                    if (sessions.GetSession(s, out var control) != 0 || control == null) continue;
                    control.GetProcessId(out uint pid);
                    if (pid == 0) continue; // sons do sistema
                    string name;
                    try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { continue; }
                    if (action(name, (int)pid, (ISimpleAudioVolume)control) is { } r && result.All(x => x.Pid != r.Pid || x.Name != r.Name))
                        result.Add(r);
                    Marshal.ReleaseComObject(control);
                }
                Marshal.ReleaseComObject(sessions);
                Marshal.ReleaseComObject(manager);
            }
            catch { }
            finally
            {
                Marshal.ReleaseComObject(dev);
            }
        }
        Marshal.ReleaseComObject(devices);
        return result;
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr guid, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr guid, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    // ---------------- Microfones ----------------

    public event Action<string>? CaptureDeviceChanged;

    public sealed record Mic(string Id, string Name, bool IsDefault, bool Connected);

    // Microfones conectados + os conhecidos que estão desconectados agora (para você saber que existem).
    public System.Collections.Generic.List<Mic> ListMicrophones()
    {
        if (Demo.Active) return Demo.Microphones();
        var list = new System.Collections.Generic.List<Mic>();
        if (_enumerator == null) return list;
        var defaultId = DefaultCaptureId();
        const int Active = 1, NotPresent = 4, Unplugged = 8;
        if (_enumerator.EnumAudioEndpoints(1 /*eCapture*/, Active | NotPresent | Unplugged, out var col) != 0 || col == null) return list;
        col.GetCount(out uint n);
        for (uint i = 0; i < n; i++)
        {
            if (col.Item(i, out var dev) != 0 || dev == null) continue;
            dev.GetId(out var id);
            dev.GetState(out int state);
            var name = GetFriendlyName(dev) ?? "Microfone";
            bool connected = (state & Active) != 0;
            // Desconectados: só os que parecem microfone (a Realtek registra vários conectores que não são).
            if (connected || System.Text.RegularExpressions.Regex.IsMatch(name, "micro|mic\\b|headset|fone|webcam|camera|câmera", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                list.Add(new Mic(id, name, id == defaultId, connected));
            Marshal.ReleaseComObject(dev);
        }
        Marshal.ReleaseComObject(col);
        return list
            .GroupBy(m => m.Name)
            .Select(g => g.OrderByDescending(m => m.Connected).First())
            .OrderByDescending(m => m.Connected)
            .ToList();
    }

    // O reconhecimento de voz do Windows sempre usa o microfone padrão.
    string? DefaultCaptureId()
    {
        if (_enumerator == null || _enumerator.GetDefaultAudioEndpoint(1 /*eCapture*/, 0 /*eConsole*/, out var dev) != 0 || dev == null)
            return null;
        dev.GetId(out var id);
        Marshal.ReleaseComObject(dev);
        return id;
    }

    public string? DefaultMicrophoneName()
    {
        if (_enumerator == null || _enumerator.GetDefaultAudioEndpoint(1, 0, out var dev) != 0 || dev == null) return null;
        var name = GetFriendlyName(dev);
        Marshal.ReleaseComObject(dev);
        return name;
    }

    internal void OnDefaultDeviceChanged(int flow, int role)
    {
        if (flow == 1 && role == 0)
        {
            _ui.BeginInvoke(() =>
            {
                if (DefaultMicrophoneName() is { } name) CaptureDeviceChanged?.Invoke(name);
            });
            return;
        }
        if (flow != 0 || role != 1) return;
        _ui.BeginInvoke(() =>
        {
            var previous = _deviceName;
            Bind();
            if (_deviceName != null && _deviceName != previous) DeviceChanged?.Invoke(_deviceName);
        });
    }

    static string? GetFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0 /*STGM_READ*/, out var store) != 0 || store == null) return null;
        var key = new PropertyKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
        if (store.GetValue(ref key, out var value) != 0) return null;
        try { return value.vt == 31 /*VT_LPWSTR*/ ? Marshal.PtrToStringUni(value.pointer) : null; }
        finally { PropVariantClear(ref value); }
    }

    public void Dispose()
    {
        Unbind();
        if (_enumerator != null)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(_notificationClient); } catch { }
            Marshal.ReleaseComObject(_enumerator);
            _enumerator = null;
        }
    }

    // ---- Interop Core Audio ----

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pv);

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct VolumeNotificationData
    {
        public Guid eventContext;
        public int muted;
        public float masterVolume;
        public uint channels;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr data);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
    }

    sealed class VolumeCallback : IAudioEndpointVolumeCallback
    {
        readonly AudioService _owner;
        public VolumeCallback(AudioService owner) => _owner = owner;

        public int OnNotify(IntPtr data)
        {
            var d = Marshal.PtrToStructure<VolumeNotificationData>(data);
            _owner.OnVolume(d.masterVolume, d.muted != 0);
            return 0;
        }
    }

    sealed class NotificationClient : IMMNotificationClient
    {
        readonly AudioService _owner;
        public NotificationClient(AudioService owner) => _owner = owner;

        public int OnDeviceStateChanged(string id, int state) => 0;
        public int OnDeviceAdded(string id) => 0;
        public int OnDeviceRemoved(string id) => 0;
        public int OnPropertyValueChanged(string id, PropertyKey key) => 0;

        public int OnDefaultDeviceChanged(int flow, int role, string id)
        {
            _owner.OnDefaultDeviceChanged(flow, role);
            return 0;
        }
    }
}
