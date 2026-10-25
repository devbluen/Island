using System;
using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Island;

// Observa eventos do sistema. Caps Lock e idioma são lidos por consulta periódica do ESTADO da tecla
// (GetKeyState) — o app NÃO instala hook de teclado e não vê o que você digita.
public sealed class SystemWatcher : IDisposable
{
    readonly Dispatcher _ui;
    bool _caps;
    IntPtr _lastForeground, _lastLayout;
    bool _fullscreen;
    Forms.PowerLineStatus _powerLine;
    bool _lowBatteryWarned;
    ManagementEventWatcher? _brightness;

    public IntPtr OwnWindow { get; set; }

    // Última janela em primeiro plano que não é a ilha (o que você estava usando).
    public IntPtr LastExternalWindow => _lastForeground;

    public event Action<bool>? CapsLockChanged;
    public event Action<string>? LanguageChanged;
    public event Action<bool, int>? PowerChanged;
    public event Action<int>? LowBattery;
    public event Action<bool>? NetworkChanged;
    public event Action<int>? BrightnessChanged;
    public event Action<bool>? FullscreenChanged;

    public SystemWatcher(Dispatcher ui) => _ui = ui;

    public void Start()
    {
        _caps = IsCapsLockOn();
        _powerLine = Forms.SystemInformation.PowerStatus.PowerLineStatus;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        Task.Run(StartBrightnessWatcher);
    }

    public static (bool HasBattery, int Percent, bool Charging) Battery()
    {
        var s = Forms.SystemInformation.PowerStatus;
        bool has = !s.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery)
                   && s.BatteryChargeStatus != Forms.BatteryChargeStatus.Unknown;
        return (has, (int)Math.Round(s.BatteryLifePercent * 100), s.PowerLineStatus == Forms.PowerLineStatus.Online);
    }

    static bool IsCapsLockOn() => (Native.GetKeyState(Native.VK_CAPITAL) & 1) != 0;

    // Chamado pela UI a cada 250 ms.
    public void Poll()
    {
        bool caps = IsCapsLockOn();
        if (caps != _caps)
        {
            _caps = caps;
            CapsLockChanged?.Invoke(caps);
        }

        var fg = Native.GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != OwnWindow)
        {
            var layout = Native.GetKeyboardLayout(Native.GetWindowThreadProcessId(fg, out _));
            // Só avisa quando o idioma muda na MESMA janela (evita aviso ao trocar de app).
            if (fg == _lastForeground && layout != _lastLayout && _lastLayout != IntPtr.Zero)
                LanguageChanged?.Invoke(DescribeLayout(layout));
            _lastForeground = fg;
            _lastLayout = layout;

            bool fullscreen = Native.IsFullscreenOnPrimary(fg);
            if (fullscreen != _fullscreen)
            {
                _fullscreen = fullscreen;
                FullscreenChanged?.Invoke(fullscreen);
            }
        }
    }

    static string DescribeLayout(IntPtr hkl)
    {
        try
        {
            var culture = new CultureInfo((int)((long)hkl & 0xFFFF));
            return $"{culture.TextInfo.ToTitleCase(culture.NativeName)} · {culture.TwoLetterISOLanguageName.ToUpperInvariant()}";
        }
        catch
        {
            return "Layout alterado";
        }
    }

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.StatusChange) return;
        _ui.BeginInvoke(() =>
        {
            var (has, pct, charging) = Battery();
            var line = Forms.SystemInformation.PowerStatus.PowerLineStatus;
            if (line != _powerLine)
            {
                _powerLine = line;
                if (has) PowerChanged?.Invoke(charging, pct);
            }

            if (has && !charging && pct <= 20 && !_lowBatteryWarned)
            {
                _lowBatteryWarned = true;
                LowBattery?.Invoke(pct);
            }
            if (charging || pct > 25) _lowBatteryWarned = false;
        });
    }

    void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e) =>
        _ui.BeginInvoke(() => NetworkChanged?.Invoke(e.IsAvailable));

    // Brilho só existe em notebooks/tablets; em PCs de mesa isso falha silenciosamente.
    void StartBrightnessWatcher()
    {
        try
        {
            var watcher = new ManagementEventWatcher(
                new ManagementScope(@"root\wmi"),
                new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            watcher.EventArrived += (_, e) =>
            {
                int value = Convert.ToInt32(e.NewEvent.Properties["Brightness"].Value);
                _ui.BeginInvoke(() => BrightnessChanged?.Invoke(value));
            };
            watcher.Start();
            _brightness = watcher;
        }
        catch { }
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        try { _brightness?.Stop(); _brightness?.Dispose(); } catch { }
    }
}
