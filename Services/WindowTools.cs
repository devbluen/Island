using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;

namespace Island;

public sealed record WindowInfo(IntPtr Handle, string Title, string Process, bool Minimized);

static class WindowTools
{
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);

    public static WindowInfo? Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        int len = GetWindowTextLength(hwnd);
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        string proc;
        try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { proc = "?"; }
        return new WindowInfo(hwnd, sb.ToString(), proc, IsIconic(hwnd));
    }

    // Janelas "de verdade" (as que aparecem no Alt+Tab).
    public static List<WindowInfo> List(IntPtr exclude)
    {
        var list = new List<WindowInfo>();
        EnumWindows((h, _) =>
        {
            if (h == exclude || !IsWindowVisible(h) || GetWindow(h, 4 /*GW_OWNER*/) != IntPtr.Zero) return true;
            if (GetWindowTextLength(h) == 0) return true;
            if ((Native.GetWindowLong(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (DwmGetWindowAttribute(h, 14 /*DWMWA_CLOAKED*/, out int cloaked, 4) == 0 && cloaked != 0) return true;
            if (Describe(h) is { } w) list.Add(w);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);

    // O Windows não deixa um app em segundo plano "roubar" o foco; estes truques são os mesmos que lançadores usam.
    public static bool Focus(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, 9 /*SW_RESTORE*/);
        if (Native.GetForegroundWindow() == h) return true;

        // 1) Um toque de Alt libera a troca de foco.
        Keys(new[] { Key(0x12, false), Key(0x12, true) });
        SetForegroundWindow(h);
        if (Native.GetForegroundWindow() == h) return true;

        // 2) Liga a fila de entrada à da janela da frente por um instante.
        var fg = Native.GetForegroundWindow();
        uint fgThread = Native.GetWindowThreadProcessId(fg, out _), me = GetCurrentThreadId();
        AttachThreadInput(me, fgThread, true);
        BringWindowToTop(h);
        SetForegroundWindow(h);
        AttachThreadInput(me, fgThread, false);
        return Native.GetForegroundWindow() == h;
    }

    static readonly string[] Browsers = { "chrome", "msedge", "firefox", "opera", "brave", "vivaldi" };

    public static bool IsBrowser(string process) => Browsers.Contains(process.ToLowerInvariant());

    public static WindowInfo? Find(string query, IntPtr exclude)
    {
        var q = Norm(query);
        var all = List(exclude);
        // "navegador"/"browser": qualquer navegador aberto (o que está mais à frente primeiro).
        if (q is "navegador" or "browser" or "navegadorweb" or "internet" or "webbrowser")
            return all.FirstOrDefault(w => Browsers.Contains(w.Process.ToLowerInvariant()));
        return all.FirstOrDefault(w => Norm(w.Process) == q)
               ?? all.FirstOrDefault(w => Norm(w.Process).Contains(q))
               ?? all.FirstOrDefault(w => Norm(w.Title).Contains(q));
    }

    // Só pelo nome do programa (não pelo título), para "abre o X" não confundir com uma aba que fala de X.
    public static WindowInfo? FindByProcess(string name, IntPtr exclude)
    {
        var q = Norm(name);
        if (q.Length < 2) return null;
        var all = List(exclude);
        if (q is "navegador" or "browser" or "internet") return all.FirstOrDefault(w => Browsers.Contains(w.Process.ToLowerInvariant()));
        return all.FirstOrDefault(w => Norm(w.Process) == q)
               ?? all.FirstOrDefault(w => Norm(w.Process).Contains(q) || (q.Length >= 4 && q.Contains(Norm(w.Process)) && Norm(w.Process).Length >= 4));
    }

    static string Norm(string s) => new string(s.ToLowerInvariant().Normalize(NormalizationForm.FormD)
        .Where(char.IsLetterOrDigit).ToArray());

    // ---------------- Teclado ----------------

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }

    const uint KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;

    static INPUT Key(ushort vk, bool up) => new() { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } } };

    static void Keys(INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    // "ctrl+k", "enter", "alt+tab", "ctrl+shift+p", "f5", "win+d"
    public static bool PressKeys(string combo)
    {
        var parts = combo.ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var vks = new List<ushort>();
        foreach (var p in parts)
        {
            ushort vk = p switch
            {
                "ctrl" or "control" => 0x11,
                "alt" => 0x12,
                "shift" => 0x10,
                "win" or "windows" => 0x5B,
                "enter" or "return" => 0x0D,
                "esc" or "escape" => 0x1B,
                "tab" => 0x09,
                "space" or "espaco" => 0x20,
                "backspace" => 0x08,
                "delete" or "del" => 0x2E,
                "up" or "cima" => 0x26,
                "down" or "baixo" => 0x28,
                "left" or "esquerda" => 0x25,
                "right" or "direita" => 0x27,
                "home" => 0x24,
                "end" => 0x23,
                "pageup" => 0x21,
                "pagedown" => 0x22,
                "playpause" => 0xB3,
                "nexttrack" => 0xB0,
                "prevtrack" => 0xB1,
                // Teclas de pontuação pela tecla física (iguais no ABNT2 e no teclado americano).
                "," or "comma" => 0xBC,
                "." or "period" => 0xBE,
                "`" or "backtick" => 0xC0,
                "-" or "minus" => 0xBD,
                "=" or "plus" => 0xBB,
                _ when p.Length > 1 && p[0] == 'f' && int.TryParse(p[1..], out int fn) && fn is >= 1 and <= 24 => (ushort)(0x6F + fn),
                _ when p.Length == 1 && char.IsLetterOrDigit(p[0]) => (ushort)char.ToUpperInvariant(p[0]),
                _ => 0,
            };
            if (vk == 0) return false;
            vks.Add(vk);
        }
        var inputs = vks.Select(v => Key(v, false)).Concat(Enumerable.Reverse(vks).Select(v => Key(v, true))).ToArray();
        Keys(inputs);
        return true;
    }

    // Digita aos poucos e espera terminar: de uma rajada só, apps como o Bloco de Notas perdem ou trocam letras,
    // e a próxima tecla (ex.: Ctrl+A) chegaria no meio do texto. Quebras de linha viram Enter.
    public static async Task TypeTextAsync(string text, CancellationToken ct = default)
    {
        var buffer = new StringBuilder();
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            if (ch != '\n') buffer.Append(ch);
            if (ch == '\n' || buffer.Length == 6)
            {
                if (buffer.Length > 0) TypeText(buffer.ToString());
                buffer.Clear();
                if (ch == '\n') PressKeys("enter");
                await Task.Delay(12, ct);
            }
        }
        if (buffer.Length > 0) TypeText(buffer.ToString());
        await Task.Delay(60, ct);
    }

    // Digita qualquer texto (com acentos) como se fosse o teclado.
    public static void TypeText(string text)
    {
        var inputs = new List<INPUT>();
        foreach (var ch in text)
        {
            inputs.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } } });
            inputs.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });
        }
        Keys(inputs.ToArray());
    }

    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(80); // apps com página web por dentro ignoram clique sem o mouse "chegar" antes
        var down = new INPUT { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = 0x0002 } } };
        var up = new INPUT { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = 0x0004 } } };
        Keys(new[] { down, up });
    }

    // ---------------- Mouse ----------------

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);

    public static (int X, int Y) CursorPosition() => GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    // Área da tela (em pixels reais) do monitor onde o mouse está; screen = 1, 2… escolhe o monitor.
    public static System.Drawing.Rectangle ScreenBounds(int? screen = null)
    {
        var all = System.Windows.Forms.Screen.AllScreens;
        if (screen is int n && n >= 1 && n <= all.Length) return all[n - 1].Bounds;
        var (x, y) = CursorPosition();
        return System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y)).Bounds;
    }

    public static void MoveMouse(int x, int y) => SetCursorPos(x, y);

    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);

    public static bool IsForeground(IntPtr window) => Native.GetForegroundWindow() == window;

    // A janela (de nível mais alto) que está visível neste ponto da tela.
    public static IntPtr WindowAt(int x, int y) => GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), 2 /*GA_ROOT*/);

    // button: left | right | middle; clicks: 1 ou 2 — onde o mouse estiver.
    public static void MouseClick(string button = "left", int clicks = 1)
    {
        var (down, up) = button switch { "right" => (0x0008u, 0x0010u), "middle" => (0x0020u, 0x0040u), _ => (0x0002u, 0x0004u) };
        for (int i = 0; i < clicks; i++)
            Keys(new[]
            {
                new INPUT { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = down } } },
                new INPUT { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = up } } },
            });
    }

    public static void Minimize(IntPtr h) => ShowWindow(h, 6);
    public static void Maximize(IntPtr h) => ShowWindow(h, 3);
    public static void Restore(IntPtr h) => ShowWindow(h, 9);
    public static void Close(IntPtr h) => PostMessage(h, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);

    // Captura da tela principal, reduzida, em JPEG (para o assistente "ver" a tela quando você pede).
    public static byte[] CapturePrimaryScreenJpeg(int maxWidth = 1568)
    {
        var b = Forms.Screen.PrimaryScreen!.Bounds;
        using var full = new Bitmap(b.Width, b.Height);
        using (var g = Graphics.FromImage(full))
            g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size);

        double k = Math.Min(1.0, maxWidth / (double)b.Width);
        using var small = new Bitmap(full, new Size((int)(b.Width * k), (int)(b.Height * k)));
        using var ms = new MemoryStream();
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        var ps = new EncoderParameters(1) { Param = { [0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L) } };
        small.Save(ms, codec, ps);
        return ms.ToArray();
    }
}

static class SystemStats
{
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    static long _idle, _kernel, _user;
    static int _lastCpu;

    // % de CPU desde a última chamada.
    public static int Cpu()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user)) return _lastCpu;
        long di = idle - _idle, total = (kernel - _kernel) + (user - _user);
        _idle = idle; _kernel = kernel; _user = user;
        if (total > 0) _lastCpu = (int)Math.Clamp(Math.Round((total - di) * 100.0 / total), 0, 100);
        return _lastCpu;
    }

    public static (int Percent, double UsedGb, double TotalGb) Memory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return (0, 0, 0);
        const double Gb = 1024.0 * 1024 * 1024;
        return ((int)m.dwMemoryLoad, (m.ullTotalPhys - m.ullAvailPhys) / Gb, m.ullTotalPhys / Gb);
    }
}
