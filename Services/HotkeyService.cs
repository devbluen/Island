using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Island;

// Atalho global via RegisterHotKey: o Windows só avisa quando ESSA combinação é apertada.
// Não é um hook de teclado — o app não vê nenhuma outra tecla.
public sealed class HotkeyService : IDisposable
{
    const int WM_HOTKEY = 0x0312, Id = 0x4A11;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    IntPtr _hwnd;
    HwndSource? _source;
    bool _registered;

    public event Action? Pressed;

    public void Attach(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(Hook);
    }

    public bool Register(string combo)
    {
        Unregister();
        if (_hwnd == IntPtr.Zero || !TryParse(combo, out uint mods, out uint vk)) return false;
        _registered = RegisterHotKey(_hwnd, Id, mods | MOD_NOREPEAT, vk);
        return _registered;
    }

    void Unregister()
    {
        if (_registered) UnregisterHotKey(_hwnd, Id);
        _registered = false;
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == Id)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public static bool TryParse(string combo, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in parts.Take(parts.Length - 1))
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": mods |= MOD_WIN; break;
                default: return false;
            }
        }
        if (parts.Length == 0 || mods == 0 || !Enum.TryParse(parts[^1], true, out Key key)) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return vk != 0;
    }

    public static string Format(ModifierKeys mods, Key key)
    {
        var s = "";
        if (mods.HasFlag(ModifierKeys.Control)) s += "Ctrl+";
        if (mods.HasFlag(ModifierKeys.Alt)) s += "Alt+";
        if (mods.HasFlag(ModifierKeys.Shift)) s += "Shift+";
        if (mods.HasFlag(ModifierKeys.Windows)) s += "Win+";
        return s + key;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(Hook);
    }
}
