using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Island;

public partial class MainWindow : Window, IAssistantHost
{
    enum Mode { Compact, Toast, Expanded }
    enum HAlign { Left, Center, Right }
    enum VAlign { Top, Bottom }

    const double BaseW = 600, BaseH = 420, EdgeMargin = 10;
    const double ToastW = 360, ToastH = 44, PillH = 34, BallSize = 44;
    const double ToastTrackWidth = 100;
    const int TabIndexAssistant = 4, TabIndexShelf = 3;
    const string GlyphPlay = "", GlyphPause = "", GlyphMic = "", GlyphChat = "";

    static readonly SolidColorBrush DimBrush = Frozen(Color.FromArgb(0x99, 255, 255, 255));
    static readonly SolidColorBrush TomatoBrush = Frozen(Color.FromRgb(0xFF, 0x6B, 0x5E));
    static readonly SolidColorBrush GreenBrush = Frozen(Color.FromRgb(0x34, 0xC7, 0x59));
    static readonly SolidColorBrush ActiveTabBrush = Frozen(Color.FromArgb(0x26, 255, 255, 255));

    readonly AppData _data = Storage.Load();
    readonly ObservableCollection<TodoItem> _todos;
    readonly ObservableCollection<ShelfItem> _shelf = new();
    readonly ObservableCollection<ChatEntry> _chat = new();
    readonly MediaService _media;
    readonly AudioService _audio;
    readonly SystemWatcher _sys;
    readonly HotkeyService _hotkey = new();
    readonly VoiceService _voice = new();
    readonly AssistantRouter _assistant;
    readonly DispatcherTimer _tick, _hoverDelay, _hoverPoll, _toastTimer;

    Mode _mode = Mode.Compact;
    HAlign _hAlign = HAlign.Center;
    VAlign _vAlign = VAlign.Top;
    int _tab, _outTicks, _ticks, _artVersion = -1;
    bool _eqRunning, _dragging, _fullscreen, _userHidden, _sticky, _hotkeyOk, _trackToastVisible;
    bool _pressPending, _suppressHover;
    Point _pressStart;
    string? _lastTitle;
    IntPtr _hwnd;
    SettingsWindow? _settingsWindow;

    bool _timerRunning, _isBreak;
    TimeSpan _timerLeft;
    DateTime _timerEnd;
    int _cycles;

    ShelfItem? _pressed;
    Point _pressPoint;

    CancellationTokenSource? _busy;
    TaskCompletionSource<bool>? _confirm;

    public bool AllowClose { get; set; }
    public AppSettings Settings => _data.Settings;
    public bool HotkeyRegistered => _hotkeyOk;
    public string WakeStatusText { get; private set; } = "Desligada";
    public string LastWakeHeard { get; private set; } = "";
    readonly System.Collections.Generic.List<string> _recentHeard = new();
    public event Action? WakeInfo;
    public event Action? MicChanged;
    string? _warmedBrain;
    public AudioService AudioService => _audio;
    public VoiceService Voice => _voice;
    // Voz que fala as respostas: a escolhida (se for do idioma atual) ou a natural padrão do idioma.
    public string? TtsVoiceId => S.TtsVoice == "windows" ? null
        : PiperTts.Find(S.TtsVoice) is { } v && v.Lang == UiLang ? v.Id
        : PiperTts.DefaultFor(UiLang).Id;

    string UiLang => string.IsNullOrEmpty(S.Language) ? Loc.SystemLanguage() : S.Language;
    SpotifyService? _spotify;
    public SpotifyService Spotify => _spotify ??= new SpotifyService(() => S) { TokensChanged = () => Dispatcher.BeginInvoke(SaveData) };
    string WakeLabel => Loc.T(S.WakeName switch { "ilha" => "wake.ilha", "jarvis" => "wake.jarvis", _ => "wake.island" });
    AppSettings S => _data.Settings;
    bool Ball => S.Shape == "ball";

    public MainWindow()
    {
        Loc.Apply(UiLang); // antes de montar a tela, para os textos já virem no idioma certo
        InitializeComponent();

        _todos = new ObservableCollection<TodoItem>(_data.Todos);
        TodoList.ItemsSource = _todos;
        ShelfList.ItemsSource = _shelf;
        ChatList.ItemsSource = _chat;
        foreach (var path in _data.Shelf) AddShelf(path, save: false);
        _timerLeft = CurrentTimerLength;

        _media = new MediaService(Dispatcher);
        _media.Changed += OnMediaChanged;

        _audio = new AudioService(Dispatcher);
        _audio.VolumeChanged += (v, muted) =>
        {
            if (S.NotifyVolume) ShowToast(muted || v < 0.005 ? "" : "", muted ? Loc.T("t.mute") : Loc.T("t.volume"), null, muted ? 0 : v);
        };
        _audio.DeviceChanged += name =>
        {
            if (S.NotifyAudioDevice) ShowToast(LooksLikeHeadphones(name) ? "" : "", Loc.T("t.audioOut"), name);
        };
        _audio.CaptureDeviceChanged += name =>
        {
            _voice.OnDefaultMicrophoneChanged(); // se a ilha usa o "padrão do Windows", passa a ouvir o novo
            if (S.NotifyAudioDevice) ShowToast("", Loc.T("t.mic"), name);
            MicChanged?.Invoke();
        };

        _sys = new SystemWatcher(Dispatcher);
        _sys.CapsLockChanged += on =>
        {
            if (S.NotifyCapsLock) ShowToast("", on ? Loc.T("t.capsOn") : Loc.T("t.capsOff"));
        };
        _sys.LanguageChanged += name =>
        {
            if (S.NotifyLanguage) ShowToast("", Loc.T("t.kbLang"), name);
        };
        _sys.PowerChanged += (charging, pct) =>
        {
            if (S.NotifyPower) ShowToast(charging ? "" : "", charging ? Loc.T("t.charging") : Loc.T("t.onBattery"), null, pct / 100.0);
        };
        _sys.LowBattery += pct =>
        {
            if (S.NotifyLowBattery) ShowToast("", Loc.T("t.lowBattery"), Loc.F("t.lowBatterySub", pct));
        };
        _sys.NetworkChanged += up =>
        {
            if (S.NotifyNetwork) ShowToast(up ? "" : "", up ? Loc.T("t.netUp") : Loc.T("t.netDown"));
        };
        _sys.BrightnessChanged += b =>
        {
            if (S.NotifyBrightness) ShowToast("", Loc.T("t.brightness"), null, b / 100.0);
        };
        _sys.FullscreenChanged += fs =>
        {
            _fullscreen = fs;
            ApplyVisibility();
        };

        _assistant = new AssistantRouter(this);
        _hotkey.Pressed += () => StartListening();
        _voice.WakeDetected += rest => Dispatcher.BeginInvoke(async () =>
        {
            _conversation = false;
            if (_voice.IsListening || !IsVisible) return;
            _voice.StopSpeaking(); // chamou de novo: a ilha para de falar

            // Ainda pensando/fazendo algo: a voz interrompe. "Jarvis, para" só cancela; "Jarvis, <outro pedido>" troca de pedido.
            if (_busy != null)
            {
                ResolveConfirm(false);
                _busy.Cancel();
                for (int i = 0; i < 40 && _busy != null; i++) await Task.Delay(100);
                if (rest != null && IsCancelPhrase(rest))
                {
                    _voice.Chime();
                    return;
                }
            }
            else if (rest != null && IsCancelPhrase(rest) && !AlarmRinging)
            {
                return; // "Jarvis, para" sem nada acontecendo: só cala a fala
            }
            if (_busy != null) return;
            if (S.Provider == "local") _ = OllamaAssistant.WarmUpAsync(S); // carrega enquanto você fala
            _voice.Chime();
            // "Ei Island, abre o Spotify": o pedido já veio junto.
            if (rest != null)
            {
                _ = SubmitAsync(rest, fromVoice: true);
                return;
            }
            // Só "Ei Island": a próxima frase vira o pedido (a escuta continua, nada é perdido).
            if (_mode == Mode.Expanded) AskHint.Text = Loc.T("hint.listening");
            else ShowToast(GlyphMic, Loc.T("t.listening"), Loc.T("t.listeningSub"), sticky: true);
        });
        _voice.Error += e => Log.Error("Reconhecimento de voz", e);
        _voice.FollowUpExpired += () => Dispatcher.BeginInvoke(() =>
        {
            AskHint.Text = Loc.T("chat.hint");
            // Depois de uma pergunta da ilha, silêncio só encerra a conversa (sem aviso).
            if (_conversation)
            {
                _conversation = false;
                if (_busy == null && _sticky) EndSticky();
                return;
            }
            if (_busy != null || !_sticky) return;
            EndSticky();
            ShowToast(GlyphMic, Loc.T("t.noRequest"), Loc.F("t.sayAgain", WakeLabel));
        });
        _voice.Transcribing += () => Dispatcher.BeginInvoke(() =>
        {
            if (_mode == Mode.Toast && _sticky)
            {
                ToastTitle.Text = Loc.T("t.understanding");
                ToastSub.Visibility = Visibility.Collapsed;
            }
            else if (_mode == Mode.Expanded)
            {
                AskHint.Text = Loc.T("t.understanding");
            }
        });
        _voice.WakeStatus += status => Dispatcher.BeginInvoke(() =>
        {
            WakeStatusText = status;
            WakeInfo?.Invoke();
        });
        _voice.WakeHeard += (text, accepted) => Dispatcher.BeginInvoke(() =>
        {
            _recentHeard.Insert(0, $"{DateTime.Now:HH:mm:ss}  “{text}”{(accepted ? "  " + Loc.T("heard.activated") : "")}");
            if (_recentHeard.Count > 5) _recentHeard.RemoveAt(5);
            LastWakeHeard = string.Join("\n", _recentHeard);
            WakeInfo?.Invoke();
        });

        _tick = MakeTimer(250, Tick);
        _hoverDelay = MakeTimer(140, () =>
        {
            _hoverDelay!.Stop();
            if (S.ExpandOnHover && !_dragging && !_pressPending && !_suppressHover && !LeftButtonDown() && IsCursorOverPill())
                SetMode(Mode.Expanded);
        });
        _hoverPoll = MakeTimer(120, HoverPoll);
        _toastTimer = MakeTimer(2200, () =>
        {
            _toastTimer!.Stop();
            if (_mode == Mode.Toast && !_sticky) SetMode(Mode.Compact);
        });

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(ApplyPosition);
    }

    static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    static DispatcherTimer MakeTimer(int ms, Action action)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => action();
        return t;
    }

    static bool LooksLikeHeadphones(string name)
    {
        var n = name.ToLowerInvariant();
        return new[] { "fone", "head", "airpods", "buds", "earphone", "headset", "auricular" }.Any(n.Contains);
    }

    // ---------------- Ciclo de vida ----------------

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _sys.OwnWindow = _hwnd;
        // Sem Alt+Tab e sem roubar o foco ao clicar.
        Native.SetExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE, true);
        _hotkey.Attach(_hwnd);
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplySettings();
        SelectTab(0);
        UpdateTodoEmpty();
        UpdateShelfEmpty();
        UpdateChatEmpty();
        UpdateTimerUi();
        OnMediaChanged();
        _tick.Start();
        if (!Demo.Active) StartUpdateChecks();
        if (Demo.Active) return; // screenshots: sem microfone, mídia, atalhos nem assistente de configuração

        try { _audio.Start(); } catch { }
        try { _sys.Start(); } catch { }
        try { await _media.StartAsync(); } catch { }

        // Primeira vez neste PC: assistente de configuração (idioma, voz, IA local).
        if (!S.SetupDone) OpenSetup();
    }

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!AllowClose) e.Cancel = true;
    }

    public void Cleanup()
    {
        SaveData();
        _tick.Stop();
        _busy?.Cancel();
        try { _hotkey.Dispose(); } catch { }
        try { _voice.Dispose(); } catch { }
        try { _audio.Dispose(); } catch { }
        try { _sys.Dispose(); } catch { }
        _settingsWindow?.Close();
    }

    public void SetUserHidden(bool hidden)
    {
        _userHidden = hidden;
        ApplyVisibility();
    }

    void ApplyVisibility()
    {
        bool show = !(_fullscreen && S.HideInFullscreen) && !_userHidden;
        if (show && !IsVisible)
        {
            Show();
            SetMode(Mode.Compact);
        }
        else if (!show && IsVisible)
        {
            Hide();
        }
    }

    void SetNoActivate(bool on)
    {
        if (_hwnd != IntPtr.Zero) Native.SetExStyle(_hwnd, Native.WS_EX_NOACTIVATE, on);
    }

    public void OpenSettings(int page = 0)
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        _settingsWindow.ShowPage(page);
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    SetupWindow? _setupWindow;

    public void OpenSetup()
    {
        if (_setupWindow == null)
        {
            _settingsWindow?.Close();
            _setupWindow = new SetupWindow(this);
            _setupWindow.Closed += (_, _) => _setupWindow = null;
            _setupWindow.Show();
        }
        _setupWindow.Activate();
    }

    // ---------------- Configurações: tamanho, forma, posição ----------------

    public void ApplySettings()
    {
        S.Scale = Math.Clamp(S.Scale, 0.6, 2.0);
        RootScale.ScaleX = RootScale.ScaleY = S.Scale;
        Width = BaseW * S.Scale;
        Height = BaseH * S.Scale;

if (UiLang != Loc.Lang)
        {
            Loc.Apply(UiLang);
            AskHint.Text = Loc.T("chat.hint");
            UpdateTimerUi();
            if (_mode == Mode.Expanded) RefreshExpanded();
        }

        _hotkeyOk = !Demo.Active && _hotkey.Register(S.Hotkey);
        bool wake = S.VoiceEnabled && S.WakeWordEnabled;
        HotkeyHint.Text = !S.VoiceEnabled ? ""
            : wake ? Loc.F("hint.wake", WakeLabel, S.Hotkey)
            : Loc.F("hint.hotkey", S.Hotkey);
        _voice.SaveRecordings = S.SaveVoiceRecordings;
        _voice.TtsVoice = TtsVoiceId;
        if (!Demo.Active) _voice.Configure(S.MicrophoneId, S.VoiceModel, wake, S.WakePhraseList(), S.WakeSensitivity, S.WakeName, S.WakeSamples, UiLang);

        var brain = S.Provider + "|" + S.OllamaModel;
        if (S.Provider == "local" && brain != _warmedBrain && !Demo.Active) _ = OllamaAssistant.WarmUpAsync(S);
        _warmedBrain = brain;

        ApplyPosition();
        ApplyVisibility();
        RefreshCompact();
        if (_mode == Mode.Compact) SetMode(Mode.Compact);
    }

    (double W, double H) CompactRefSize() => Ball ? (BallSize, BallSize) : (120, PillH);

    // A ilha "ancora" no lado mais próximo da tela, para crescer para dentro dela.
    void ApplyPosition()
    {
        double s = S.Scale, m = EdgeMargin * s;
        var (rw, rh) = CompactRefSize();
        double cw = rw * s, ch = rh * s;

        bool docked = S.PillX is null || S.PillY is null;
        double px, py;
        Rect area;
        if (docked)
        {
            px = SystemParameters.PrimaryScreenWidth / 2;
            py = m + ch / 2;
            area = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        }
        else
        {
            px = S.PillX!.Value;
            py = S.PillY!.Value;
            if (WorkAreaAt(px, py) is not Rect a)
            {
                S.PillX = S.PillY = null;
                ApplyPosition();
                return;
            }
            area = a;
        }

        _hAlign = docked ? HAlign.Center
            : px < area.Left + area.Width / 3 ? HAlign.Left
            : px > area.Left + area.Width * 2 / 3 ? HAlign.Right
            : HAlign.Center;
        _vAlign = !docked && py > area.Top + area.Height / 2 ? VAlign.Bottom : VAlign.Top;

        PillHost.HorizontalAlignment = _hAlign switch
        {
            HAlign.Left => HorizontalAlignment.Left,
            HAlign.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center,
        };
        PillHost.VerticalAlignment = _vAlign == VAlign.Bottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        ExpandedView.VerticalAlignment = PillHost.VerticalAlignment;

        Left = _hAlign switch
        {
            HAlign.Left => px - cw / 2 - m,
            HAlign.Right => px + cw / 2 + m - Width,
            _ => px - Width / 2,
        };
        Top = _vAlign == VAlign.Top ? py - ch / 2 - m : py + ch / 2 + m - Height;
    }

    // Ponto de ancoragem atual (centro da ilha compacta) a partir da posição da janela.
    (double X, double Y) AnchorFromWindow()
    {
        double s = S.Scale, m = EdgeMargin * s;
        var (rw, rh) = CompactRefSize();
        double cw = rw * s, ch = rh * s;
        double x = _hAlign switch
        {
            HAlign.Left => Left + m + cw / 2,
            HAlign.Right => Left + Width - m - cw / 2,
            _ => Left + Width / 2,
        };
        double y = _vAlign == VAlign.Top ? Top + m + ch / 2 : Top + Height - m - ch / 2;
        return (x, y);
    }

    Rect? WorkAreaAt(double x, double y)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var pt = new System.Drawing.Point((int)(x * dpi.DpiScaleX), (int)(y * dpi.DpiScaleY));
        var screen = Forms.Screen.AllScreens.FirstOrDefault(sc => sc.Bounds.Contains(pt));
        if (screen == null) return null;
        var wa = screen.WorkingArea;
        return new Rect(wa.X / dpi.DpiScaleX, wa.Y / dpi.DpiScaleY, wa.Width / dpi.DpiScaleX, wa.Height / dpi.DpiScaleY);
    }

    // Apertar o botão só "arma" o arraste; ele começa de fato quando o mouse anda alguns pixels.
    // Assim um clique simples continua sendo clique.
    void Pill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _hoverDelay.Stop();
        _pressPending = true;
        _pressStart = e.GetPosition(this);
        PillHost.CaptureMouse();
        e.Handled = true;
    }

    void Pill_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressPending) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelPress();
            return;
        }
        var d = e.GetPosition(this) - _pressStart;
        if (Math.Abs(d.X) < 4 && Math.Abs(d.Y) < 4) return;

        CancelPress();
        if (!S.LockPosition) BeginDrag();
    }

    void Pill_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressPending) return;
        CancelPress();
        if (_mode != Mode.Expanded) SetMode(Mode.Expanded);
    }

    void CancelPress()
    {
        _pressPending = false;
        if (PillHost.IsMouseCaptured) PillHost.ReleaseMouseCapture();
    }

    void BeginDrag()
    {
        double left = Left, top = Top;
        _hoverDelay.Stop();
        _dragging = true;
        // Ao arrastar, a ilha volta para a forma compacta e fica assim — e vai para baixo do cursor,
        // senão ela encolheria em direção ao canto onde estava e ficaria longe da mão.
        if (_mode != Mode.Compact)
        {
            SetMode(Mode.Compact);
            if (Native.GetCursorPos(out var p))
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                var (ax, ay) = AnchorFromWindow();
                Left += p.X / dpi.DpiScaleX - ax;
                Top += p.Y / dpi.DpiScaleY - ay;
            }
        }
        try { DragMove(); } catch { }
        finally { _dragging = false; }

        // Não reabre embaixo do cursor logo depois de soltar: só depois que o mouse sair dela.
        _suppressHover = true;
        if (Math.Abs(Left - left) > 2 || Math.Abs(Top - top) > 2) OnDragged();
    }

    static bool LeftButtonDown() => (Native.GetAsyncKeyState(0x01) & 0x8000) != 0;

    void OnDragged()
    {
        var (x, y) = AnchorFromWindow();
        double s = S.Scale;
        // Solta perto do topo central da tela principal: encaixa.
        if (Math.Abs(x - SystemParameters.PrimaryScreenWidth / 2) < 80 && y >= 0 && y < 60 * s)
        {
            S.PillX = S.PillY = null;
        }
        else
        {
            S.PillX = x;
            S.PillY = y;
        }
        ApplyPosition();
        SaveData();
    }

    // ---------------- Relógio principal (250 ms) ----------------

    void Tick()
    {
        var now = DateTime.Now;
        var clock = now.ToString("HH:mm");

        if (!_media.HasMedia && S.ShowClockWhenIdle)
        {
            SetText(CompactText, clock);
            if (!_timerRunning) SetText(BallText, clock);
        }

        if (_timerRunning)
        {
            _timerLeft = _timerEnd - now;
            if (_timerLeft <= TimeSpan.Zero) TimerFinished();
            UpdateTimerUi();
        }

        if (_mode == Mode.Expanded)
        {
            SetText(ExpClock, clock);
            SetText(ExpDate, now.ToString("ddd, d MMM", Loc.Culture));
            if (_tab == 0) UpdateProgress();
        }

        if (!Demo.Active) _sys.Poll();
        if (_suppressHover && !_dragging && !IsCursorOverPill()) _suppressHover = false;

        _ticks++;
        if (_ticks % 4 == 0)
        {
            CheckTodos(now);
            UpdateHeaderStats();
        }
        if (_ticks % 8 == 0 && IsVisible)
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    static void SetText(TextBlock t, string s)
    {
        if (t.Text != s) t.Text = s;
    }

    void UpdateHeaderStats()
    {
        int cpu = SystemStats.Cpu();
        if (_mode != Mode.Expanded) return;
        if (Demo.Active)
        {
            SetText(ExpBattery, "86%");
            SetText(ExpStats, "CPU 12% · RAM 41%");
            return;
        }
        var (has, pct, charging) = SystemWatcher.Battery();
        SetText(ExpBattery, has ? $"{(charging ? "⚡" : "")}{pct}%" : "");
        SetText(ExpStats, S.ShowSystemStats ? $"CPU {cpu}% · RAM {SystemStats.Memory().Percent}%" : "");
    }

    // ---------------- Estados e animação ----------------

    void SetMode(Mode m)
    {
        var prev = _mode;
        _mode = m;
        if (m != Mode.Toast) _sticky = false;

        var (w, h) = m switch
        {
            Mode.Toast => (ToastW, ToastH),
            Mode.Expanded => ExpandedSize(),
            _ => CompactSize(),
        };
        AnimateSize(w, h);

        Fade(CompactView, m == Mode.Compact && !Ball);
        Fade(BallView, m == Mode.Compact && Ball);
        Fade(ToastView, m == Mode.Toast);
        Fade(ExpandedView, m == Mode.Expanded);

        if (m == Mode.Expanded)
        {
            _outTicks = 0;
            _hoverPoll.Start();
            _toastTimer.Stop();
            if (prev != m) RefreshExpanded();
        }
        else
        {
            SetNoActivate(true);
        }
        UpdateEq();
    }

    (double, double) ExpandedSize() => _tab == TabIndexAssistant ? (540, 340) : (500, 210);

    (double, double) CompactSize()
    {
        if (Ball) return (BallSize, BallSize);
        double w = _media.HasMedia ? 220 : _timerRunning ? 180 : S.ShowClockWhenIdle ? 120 : 84;
        return (w, PillH);
    }

    void AnimateSize(double w, double h)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(460));
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
        PillHost.BeginAnimation(WidthProperty, new DoubleAnimation(w, duration) { EasingFunction = ease });
        PillHost.BeginAnimation(HeightProperty, new DoubleAnimation(h, duration) { EasingFunction = ease });
    }

    static void Fade(UIElement el, bool show)
    {
        if (el.IsHitTestVisible == show && Math.Abs(el.Opacity - (show ? 1 : 0)) < 0.01) return;
        el.IsHitTestVisible = show;
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 220 : 100))
        {
            BeginTime = show ? TimeSpan.FromMilliseconds(120) : TimeSpan.Zero,
        });
    }

    void PillHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double w = PillHost.ActualWidth, h = PillHost.ActualHeight;
        double r = Math.Min(Math.Min(w, h) / 2, 30);
        var radius = new CornerRadius(r);
        Pill.CornerRadius = Shadow.CornerRadius = DropRing.CornerRadius = radius;
        Pill.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
    }

    void RefreshCompact()
    {
        bool media = _media.HasMedia;
        var clock = S.ShowClockWhenIdle ? DateTime.Now.ToString("HH:mm") : "";

        CompactArt.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
        CompactText.Text = media ? "" : clock;
        CompactTimer.Visibility = _timerRunning ? Visibility.Visible : Visibility.Collapsed;

        BallArt.Visibility = media && !_timerRunning ? Visibility.Visible : Visibility.Collapsed;
        BallText.Text = _timerRunning ? CompactTimer.Text : media ? "" : clock;
        BallText.Foreground = _timerRunning ? CompactTimer.Foreground : Brushes.White;

        UpdateEq();
        if (_mode == Mode.Compact) SetMode(Mode.Compact);
    }

    void UpdateEq()
    {
        EqBars.Visibility = _media.HasMedia && !_timerRunning ? Visibility.Visible : Visibility.Collapsed;

        bool run = _mode == Mode.Compact && _media.IsPlaying && !_timerRunning && IsVisible;
        if (run == _eqRunning && !(Ball && !run)) return;
        _eqRunning = run;

        double[] durations = { 0.42, 0.58, 0.36, 0.5 };
        int i = 0;
        foreach (Rectangle bar in EqBars.Children)
        {
            var st = (ScaleTransform)bar.RenderTransform;
            st.BeginAnimation(ScaleTransform.ScaleYProperty, run && !Ball
                ? new DoubleAnimation(0.25, 1, TimeSpan.FromSeconds(durations[i++ % durations.Length]))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                }
                : new DoubleAnimation(0.3, TimeSpan.FromMilliseconds(200)));
        }

        // Na bolinha, um anel na cor da capa "respira" enquanto toca.
        BallRing.BeginAnimation(OpacityProperty, run && Ball
            ? new DoubleAnimation(0.2, 0.9, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
            : new DoubleAnimation(_media.HasMedia ? 0.3 : 0, TimeSpan.FromMilliseconds(200)));
    }

    // ---------------- Hover ----------------

    void Pill_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_mode != Mode.Expanded && S.ExpandOnHover && !_suppressHover && !_dragging) _hoverDelay.Start();
    }

    void HoverPoll()
    {
        if (Demo.Active) return;
        if (_mode != Mode.Expanded)
        {
            _hoverPoll.Stop();
            return;
        }
        if (IsCursorOverPill() || _dragging || _confirm != null || _alarmItem != null || TodoInput.IsKeyboardFocused || AskInput.IsKeyboardFocused)
        {
            _outTicks = 0;
            return;
        }
        if (++_outTicks >= 3) SetMode(Mode.Compact);
    }

    bool IsCursorOverPill()
    {
        if (!IsVisible || PresentationSource.FromVisual(PillHost) == null) return false;
        if (!Native.GetCursorPos(out var p)) return false;
        var pt = PillHost.PointFromScreen(new Point(p.X, p.Y));
        return pt.X >= -4 && pt.Y >= -10 && pt.X <= PillHost.ActualWidth + 4 && pt.Y <= PillHost.ActualHeight + 10;
    }

    // ---------------- Avisos rápidos ----------------

    void ShowToast(string glyph, string title, string? sub = null, double? level = null, System.Windows.Media.ImageSource? art = null,
                   bool sticky = false, double? seconds = null)
    {
        if (_mode == Mode.Expanded || !IsVisible) return;

        _trackToastVisible = false;
        ToastIcon.Text = glyph;
        ToastIcon.Visibility = art == null ? Visibility.Visible : Visibility.Collapsed;
        ToastArt.Visibility = art != null ? Visibility.Visible : Visibility.Collapsed;
        ToastArtBrush.ImageSource = art;
        ToastTitle.Text = title;
        ToastSub.Text = sub ?? "";
        ToastSub.Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible;

        if (level is double l)
        {
            l = Math.Clamp(l, 0, 1);
            ToastLevel.Visibility = Visibility.Visible;
            ToastFill.BeginAnimation(WidthProperty, new DoubleAnimation(l * ToastTrackWidth, TimeSpan.FromMilliseconds(140)));
            ToastValue.Text = Math.Round(l * 100).ToString();
        }
        else
        {
            ToastLevel.Visibility = Visibility.Collapsed;
        }

        if (_mode != Mode.Toast) SetMode(Mode.Toast);
        _sticky = sticky;
        ToastIcon.BeginAnimation(OpacityProperty, sticky
            ? new DoubleAnimation(0.35, 1, TimeSpan.FromSeconds(0.7)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
            : null);

        _toastTimer.Stop();
        if (!sticky)
        {
            _toastTimer.Interval = TimeSpan.FromSeconds(seconds ?? S.ToastSeconds);
            _toastTimer.Start();
        }
    }

    void EndSticky()
    {
        if (!_sticky) return;
        _sticky = false;
        ToastIcon.BeginAnimation(OpacityProperty, null);
        if (_mode == Mode.Toast)
        {
            _toastTimer.Interval = TimeSpan.FromSeconds(0.8);
            _toastTimer.Start();
        }
    }

    // ---------------- Abas ----------------

    void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && int.TryParse(b.Tag?.ToString(), out int i)) SelectTab(i);
    }

    void SelectTab(int i)
    {
        _tab = i;
        FrameworkElement[] panels = { MediaPanel, TimerPanel, TodoPanel, ShelfPanel, AssistantPanel };
        Button[] tabs = { TabMedia, TabTimer, TabTodo, TabShelf, TabAssistant };
        for (int k = 0; k < panels.Length; k++)
        {
            panels[k].Visibility = k == i ? Visibility.Visible : Visibility.Collapsed;
            tabs[k].Foreground = k == i ? Brushes.White : DimBrush;
            tabs[k].Background = k == i ? ActiveTabBrush : Brushes.Transparent;
        }

        var (w, h) = ExpandedSize();
        ExpandedView.Width = w;
        ExpandedView.Height = h;
        if (_mode == Mode.Expanded) AnimateSize(w, h);
        RefreshExpanded();
    }

    void RefreshExpanded()
    {
        var now = DateTime.Now;
        SetText(ExpClock, now.ToString("HH:mm"));
        SetText(ExpDate, now.ToString("ddd, d MMM", Loc.Culture));
        UpdateHeaderStats();
        if (_tab == 0) Dispatcher.BeginInvoke(UpdateProgress, DispatcherPriority.Loaded);
        if (_tab == 1) UpdateTimerUi();
        if (_tab == TabIndexAssistant) ScrollChat();
    }

    // ---------------- Mídia ----------------

    void OnMediaChanged()
    {
        if (_media.ArtVersion != _artVersion)
        {
            _artVersion = _media.ArtVersion;
            var art = _media.Art;
            CompactArtBrush.ImageSource = art;
            BigArtBrush.ImageSource = art;
            BallArtBrush.ImageSource = art;
            BigArtGlyph.Visibility = art == null ? Visibility.Visible : Visibility.Collapsed;
            Resources["AccentBrush"] = new SolidColorBrush(art != null ? ColorUtil.Accent(art) : Colors.White);
            // A capa costuma chegar depois do título: atualiza o aviso de "música nova" se ele ainda estiver na tela.
            if (_trackToastVisible && _mode == Mode.Toast && art != null) ToastArtBrush.ImageSource = art;
        }

        bool has = _media.HasMedia;
        MediaTitle.Text = _media.Title;
        MediaArtist.Text = _media.Artist;
        MediaApp.Text = _media.AppName;
        PlayPauseBtn.Content = _media.IsPlaying ? GlyphPause : GlyphPlay;
        MediaContent.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        NoMedia.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        ProgressRow.Visibility = _media.Duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Hidden;
        UpdateProgress();

        bool newTrack = has && _lastTitle != null && _media.Title != _lastTitle && _media.IsPlaying;
        _lastTitle = _media.Title;

        RefreshCompact();
        if (newTrack && S.NotifyTrack)
        {
            ShowToast("", _media.Title, _media.Artist, null, _media.Art);
            _trackToastVisible = true;
        }
    }

    void UpdateProgress()
    {
        var duration = _media.Duration;
        if (duration <= TimeSpan.Zero) return;
        var pos = _media.EstimatedPosition;
        double f = Math.Clamp(pos.TotalSeconds / duration.TotalSeconds, 0, 1);
        SeekFill.Width = f * SeekTrack.ActualWidth;
        SetText(PosText, FormatTime(pos));
        SetText(DurText, FormatTime(duration));
    }

    static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    void PlayPause_Click(object sender, RoutedEventArgs e) => _media.PlayPause();
    void Next_Click(object sender, RoutedEventArgs e) => _media.Next();
    void Prev_Click(object sender, RoutedEventArgs e) => _media.Previous();

    void Seek_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_media.Duration <= TimeSpan.Zero || SeekTrack.ActualWidth <= 0) return;
        double f = Math.Clamp(e.GetPosition(SeekTrack).X / SeekTrack.ActualWidth, 0, 1);
        _media.Seek(TimeSpan.FromSeconds(_media.Duration.TotalSeconds * f));
        UpdateProgress();
    }

    // ---------------- Timer de foco (Pomodoro) ----------------

    TimeSpan CurrentTimerLength => TimeSpan.FromMinutes(_isBreak ? _data.BreakMinutes : _data.FocusMinutes);

    void UpdateTimerUi()
    {
        var left = _timerLeft < TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Ceiling(_timerLeft.TotalSeconds));
        var text = $"{(int)left.TotalMinutes:00}:{left.Seconds:00}";
        SetText(TimerBig, text);
        SetText(CompactTimer, text);
        if (_timerRunning) SetText(BallText, text);

        var brush = _isBreak ? GreenBrush : TomatoBrush;
        TimerMode.Text = _isBreak ? Loc.T("timer.break") : Loc.T("timer.focus");
        TimerMode.Foreground = brush;
        CompactTimer.Foreground = brush;
        if (_timerRunning) BallText.Foreground = brush;
        TimerPlay.Content = _timerRunning ? GlyphPause : GlyphPlay;
        TimerInfo.Text = _cycles == 1 ? Loc.T("timer.cycles1") : Loc.F("timer.cyclesN", _cycles);
        TimerMinus.IsEnabled = TimerPlus.IsEnabled = !_timerRunning;
    }

    void TimerPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_timerRunning)
        {
            _timerLeft = _timerEnd - DateTime.Now;
            _timerRunning = false;
        }
        else
        {
            if (_timerLeft <= TimeSpan.Zero) _timerLeft = CurrentTimerLength;
            _timerEnd = DateTime.Now + _timerLeft;
            _timerRunning = true;
        }
        UpdateTimerUi();
        RefreshCompact();
    }

    void TimerReset_Click(object sender, RoutedEventArgs e)
    {
        _timerRunning = false;
        _timerLeft = CurrentTimerLength;
        UpdateTimerUi();
        RefreshCompact();
    }

    void TimerSkip_Click(object sender, RoutedEventArgs e)
    {
        _timerRunning = false;
        _isBreak = !_isBreak;
        _timerLeft = CurrentTimerLength;
        UpdateTimerUi();
        RefreshCompact();
    }

    void TimerAdjust_Click(object sender, RoutedEventArgs e)
    {
        if (_timerRunning || sender is not Button b || !int.TryParse(b.Tag?.ToString(), out int sign)) return;
        if (_isBreak) _data.BreakMinutes = Math.Clamp(_data.BreakMinutes + sign, 1, 30);
        else _data.FocusMinutes = Math.Clamp(_data.FocusMinutes + sign * 5, 5, 120);
        _timerLeft = CurrentTimerLength;
        SaveData();
        UpdateTimerUi();
    }

    void TimerFinished()
    {
        _timerRunning = false;
        if (!_isBreak) _cycles++;
        _isBreak = !_isBreak;
        _timerLeft = CurrentTimerLength;
        if (S.PlaySounds) SystemSounds.Exclamation.Play();
        UpdateTimerUi();
        RefreshCompact();
        ShowToast("", _isBreak ? Loc.T("timer.breakTime") : Loc.T("timer.backToFocus"),
            _isBreak ? Loc.F("timer.rest", _data.BreakMinutes) : Loc.F("timer.nextSession", _data.FocusMinutes));
    }

    // ---------------- Tarefas ----------------

    // Entende a data/hora em qualquer parte do texto ("Reunião segunda às 18h", "alarme amanhã 7h").
    TodoItem? AddTodo(string raw, bool alarm = false)
    {
        raw = raw.Trim();
        if (raw.Length == 0) return null;

        var parsed = DateParser.Parse(raw, Loc.Lang, DateTime.Now);
        var item = new TodoItem
        {
            Text = parsed.Text.Length > 0 ? parsed.Text : parsed.Alarm || alarm ? Loc.T("alarm.title") : Loc.T("task.defaultText"),
            Due = parsed.When,
            Alarm = alarm || parsed.Alarm,
            Daily = parsed.Daily,
        };
        _todos.Add(item);
        SaveData();
        UpdateTodoEmpty();
        return item;
    }

    void CheckTodos(DateTime now)
    {
        foreach (var t in _todos.Where(t => !t.Done && !t.Notified && t.Due <= now).ToList())
        {
            if (t.Daily)
            {
                // Repete amanhã no mesmo horário.
                while (t.Due <= now) t.Due = t.Due!.Value.AddDays(1);
            }
            else
            {
                t.Notified = true;
            }
            t.Refresh();

            if (t.Alarm)
            {
                StartAlarm(t);
            }
            else
            {
                if (S.PlaySounds) SystemSounds.Asterisk.Play();
                ShowToast("", t.Text, Loc.F("t.reminder", now.ToString("HH:mm")), seconds: 8);
                if (S.SpeakReplies) _ = _voice.SpeakAsync(t.Text);
            }
            SaveData();
        }
    }

    // ---------------- Alarme ----------------

    TodoItem? _alarmItem;
    System.Media.SoundPlayer? _alarmSound;
    DispatcherTimer? _alarmTimeout, _alarmBeep;

    public bool AlarmRinging => _alarmItem != null;

    void StartAlarm(TodoItem item)
    {
        StopAlarmSound();
        _alarmItem = item;
        AlarmText.Text = item.Text;
        AlarmTime.Text = DateTime.Now.ToString("HH:mm");
        AlarmPanel.Visibility = Visibility.Visible;

        if (!IsVisible) Show(); // aparece até em tela cheia
        SetMode(Mode.Expanded);

        // Som de alarme do Windows em loop; se não existir, bipes.
        var wav = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Alarm01.wav");
        if (File.Exists(wav))
        {
            _alarmSound = new System.Media.SoundPlayer(wav);
            _alarmSound.PlayLooping();
        }
        else
        {
            _alarmBeep = MakeTimer(900, () => SystemSounds.Exclamation.Play());
            _alarmBeep.Start();
        }

        // Para sozinho depois de 10 minutos.
        _alarmTimeout = MakeTimer(10 * 60 * 1000, () => StopAlarm());
        _alarmTimeout.Start();
        Log.Info("Alarme tocando: " + item.Text);
    }

    void StopAlarmSound()
    {
        _alarmSound?.Stop();
        _alarmSound = null;
        _alarmBeep?.Stop();
        _alarmBeep = null;
        _alarmTimeout?.Stop();
        _alarmTimeout = null;
    }

    public void StopAlarm()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(StopAlarm); return; }
        StopAlarmSound();
        _alarmItem = null;
        AlarmPanel.Visibility = Visibility.Collapsed;
        ApplyVisibility();
    }

    public void SnoozeAlarm(int minutes = 5)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SnoozeAlarm(minutes)); return; }
        if (_alarmItem is { } item)
        {
            // Volta a tocar daqui a X minutos (um alarme diário já foi remarcado para amanhã: cria um avulso).
            var again = item.Daily ? new TodoItem { Text = item.Text, Alarm = true } : item;
            again.Due = DateTime.Now.AddMinutes(minutes);
            again.Notified = false;
            if (item.Daily) _todos.Add(again);
            again.Refresh();
            SaveData();
        }
        StopAlarm();
    }

    void AlarmStop_Click(object sender, RoutedEventArgs e) => StopAlarm();
    void AlarmSnooze_Click(object sender, RoutedEventArgs e) => SnoozeAlarm();

    void UpdateTodoEmpty() => TodoEmpty.Visibility = _todos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // A janela normalmente não pega foco; para digitar, precisa ativar.
    void Input_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        SetNoActivate(false);
        Activate();
    }

    void TodoInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddTodo(TodoInput.Text);
            TodoInput.Clear();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            TodoInput.Clear();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    void TodoInput_TextChanged(object sender, TextChangedEventArgs e) =>
        TodoHint.Visibility = string.IsNullOrEmpty(TodoInput.Text) ? Visibility.Visible : Visibility.Collapsed;

    void TodoAdd_Click(object sender, RoutedEventArgs e)
    {
        AddTodo(TodoInput.Text);
        TodoInput.Clear();
    }

    void TodoCheck_Click(object sender, RoutedEventArgs e) => SaveData();

    void TodoDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TodoItem t) return;
        _todos.Remove(t);
        SaveData();
        UpdateTodoEmpty();
    }

    // ---------------- Prateleira de arquivos ----------------

    void AddShelf(string path, bool save = true)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        if (_shelf.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))) return;

        var name = System.IO.Path.GetFileName(path.TrimEnd('\\'));
        _shelf.Add(new ShelfItem(path, string.IsNullOrEmpty(name) ? path : name, Native.GetFileIcon(path)));
        if (save) SaveData();
        UpdateShelfEmpty();
    }

    void UpdateShelfEmpty() => ShelfEmpty.Visibility = _shelf.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    void Pill_DragEnter(object sender, DragEventArgs e)
    {
        if (_dragging || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        DropRing.Visibility = Visibility.Visible;
        if (_tab != TabIndexShelf) SelectTab(TabIndexShelf);
        if (_mode != Mode.Expanded) SetMode(Mode.Expanded);
    }

    void Pill_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_dragging && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Pill_DragLeave(object sender, DragEventArgs e)
    {
        if (!IsCursorOverPill()) DropRing.Visibility = Visibility.Collapsed;
    }

    void Pill_Drop(object sender, DragEventArgs e)
    {
        DropRing.Visibility = Visibility.Collapsed;
        e.Handled = true;
        if (_dragging) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            foreach (var f in files) AddShelf(f);
    }

    void ShelfItem_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // não arrasta a ilha
        _pressed = (sender as FrameworkElement)?.Tag as ShelfItem;
        _pressPoint = e.GetPosition(this);
    }

    void ShelfItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed == null || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6) return;

        var item = _pressed;
        _pressed = null;
        _dragging = true;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch { }
        finally
        {
            _dragging = false;
        }

        if (!File.Exists(item.Path) && !Directory.Exists(item.Path))
        {
            _shelf.Remove(item);
            SaveData();
            UpdateShelfEmpty();
        }
    }

    void ShelfItem_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var item = _pressed;
        _pressed = null;
        if (item == null) return;
        try { Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true }); } catch { }
    }

    void ShelfItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ShelfItem item) return;
        _shelf.Remove(item);
        SaveData();
        UpdateShelfEmpty();
    }

    void ShelfClear_Click(object sender, RoutedEventArgs e)
    {
        _shelf.Clear();
        SaveData();
        UpdateShelfEmpty();
    }

    // ---------------- Assistente ----------------

    MediaService IAssistantHost.Media => _media;
    AudioService IAssistantHost.Audio => _audio;
    IntPtr IAssistantHost.OwnWindow => _hwnd;
    IntPtr IAssistantHost.LastExternalWindow => _sys.LastExternalWindow;

    void UpdateChatEmpty() => ChatEmpty.Visibility = _chat.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    void ScrollChat() => Dispatcher.BeginInvoke(() => ChatScroll.ScrollToEnd(), DispatcherPriority.Loaded);

    ChatEntry AddChat(string kind, string text, string glyph = "")
    {
        var entry = new ChatEntry { Kind = kind, Text = text, Glyph = glyph };
        _chat.Add(entry);
        while (_chat.Count > 80) _chat.RemoveAt(0);
        UpdateChatEmpty();
        ScrollChat();
        return entry;
    }

    // Abre a ilha na aba do assistente com o campo de texto pronto para digitar.
    void OpenAssistantInput()
    {
        if (_tab != TabIndexAssistant) SelectTab(TabIndexAssistant);
        SetMode(Mode.Expanded);
        SetNoActivate(false);
        Activate();
        AskInput.Focus();
    }

    async void StartListening()
    {
        if (!IsVisible) return;
        if (_voice.IsListening)
        {
            _voice.Stop();
            return;
        }
        if (!S.VoiceEnabled)
        {
            OpenAssistantInput();
            return;
        }

        MicBtn.Foreground = TomatoBrush;
        if (_mode == Mode.Expanded)
        {
            if (_tab != TabIndexAssistant) SelectTab(TabIndexAssistant);
            AskInput.Text = "";
        }
        else
        {
            ShowToast(GlyphMic, Loc.T("t.listening"), Loc.T("t.listeningSub"), sticky: true);
        }

        try
        {
            var text = await _voice.ListenAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                EndSticky();
                if (_mode != Mode.Expanded) ShowToast(GlyphMic, Loc.T("t.heardNothing"), Loc.F("t.pressAgain", S.Hotkey));
                return;
            }
            AskInput.Text = "";
            await SubmitAsync(text, fromVoice: true);
        }
        catch (Exception ex)
        {
            Log.Error("Voz", ex);
            EndSticky();
            AddChat("error", ex.Message, "");
            if (_mode != Mode.Expanded) ShowToast("", Loc.T("t.mic"), ex.Message, seconds: 7);
        }
        finally
        {
            MicBtn.Foreground = Brushes.White;
            AskHint.Text = Loc.T("chat.hint");
        }
    }

    bool _conversation;

    // ---------------- Atualizações ----------------

    public Updater.Release? AvailableUpdate { get; private set; }
    public event Action? UpdateChanged;
    DispatcherTimer? _updateTimer;

    void StartUpdateChecks()
    {
        // 20 s depois de abrir (para não pesar a inicialização) e depois a cada 12 h.
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(12);
            if (S.CheckUpdates) await CheckForUpdatesAsync(manual: false);
        };
        _updateTimer.Start();
    }

    // Devolve o release novo (ou null). No automático, só avisa uma vez por versão.
    public async Task<Updater.Release?> CheckForUpdatesAsync(bool manual)
    {
        Updater.Release? found = null;
        try { found = await Updater.CheckAsync(CancellationToken.None); }
        catch (Exception e) { if (manual) throw; Log.Error("Procurar atualização", e); }
        AvailableUpdate = found;
        UpdateChanged?.Invoke();
        if (found != null && !manual && S.SkippedVersion != found.Tag)
        {
            S.SkippedVersion = found.Tag; // avisa uma vez; as configurações continuam mostrando o botão
            SaveData();
            ShowToast("\uE896", Loc.F("upd.toast", found.Tag), Loc.T("upd.toastSub"), seconds: 9);
        }
        return found;
    }

    // Baixa, confere e instala; a ilha fecha e a versão nova abre sozinha.
    public async Task InstallUpdateAsync(Updater.Release release, Action<double> progress, CancellationToken ct)
    {
        await Updater.PrepareAsync(release, progress, ct);
        Updater.LaunchPending();
        ((App)Application.Current).Quit();
    }

    public async Task<string> CheckUpdateAsync(bool install, CancellationToken ct)
    {
        if (!Dispatcher.CheckAccess()) return await Dispatcher.Invoke(() => CheckUpdateAsync(install, ct));
        Updater.Release? found;
        try { found = await CheckForUpdatesAsync(manual: true); }
        catch (Exception e) { return Loc.F("upd.error", e.Message); }
        if (found == null) return Loc.F("upd.none", Updater.Current.ToString(3));
        if (!install) return Loc.F("upd.available", found.Tag, Updater.Current.ToString(3));
        if (S.ConfirmCommands && !await ConfirmAsync(Loc.F("upd.toast", found.Tag), Loc.T("upd.confirm")))
            return Loc.T("chat.canceled");
        ShowToast("\uE896", Loc.F("upd.downloading", found.Tag), null, 0, sticky: true);
        try
        {
            await InstallUpdateAsync(found, p => Dispatcher.BeginInvoke(() => ShowToast("\uE896", Loc.F("upd.downloading", found.Tag), null, p, sticky: true)), ct);
            return Loc.T("upd.restarting");
        }
        catch (Exception e)
        {
            EndSticky();
            return Loc.F("upd.error", e.Message);
        }
    }

    // "para", "cancela", "esquece", "stop", "never mind", "olvídalo"…
    static bool IsCancelPhrase(string text) => System.Text.RegularExpressions.Regex.IsMatch(
        CommandRouter.Fold(text.ToLowerInvariant()).Trim(' ', '.', '!', ',', '?'),
        @"^(?:para|pare|parar|chega|cancela|cancelar|cancele|esquece|esqueca|deixa pra la|deixa para la|nada|stop|cancel|never ?mind|forget it|nothing|detente|basta|olvidalo|no importa)(?: (?:isso|tudo|ai|it|that|eso))?$");

    async Task ContinueConversationAsync(Task<TimeSpan> speaking)
    {
        var duration = await speaking;
        if (S.WakeWordEnabled && S.VoiceEnabled)
        {
            _conversation = true;
            _voice.ContinueConversation(duration);
            await Task.Delay(duration);
            if (_busy != null || !_conversation) return;
            if (_mode == Mode.Expanded) AskHint.Text = Loc.T("hint.listening");
            else ShowToast(GlyphMic, Loc.T("t.listening"), Loc.T("t.listeningSub"), sticky: true);
        }
        else
        {
            // Sem palavra de ativação: abre o microfone uma vez, como se você tivesse apertado o atalho.
            await Task.Delay(duration + TimeSpan.FromMilliseconds(300));
            if (_busy == null && !_voice.IsListening) StartListening();
        }
    }

    async Task SubmitAsync(string text, bool fromVoice = false)
    {
        text = text.Trim();
        if (text.Length == 0 || _busy != null) return;

        if (_tab != TabIndexAssistant) SelectTab(TabIndexAssistant);
        AddChat("user", text);

        _busy = new CancellationTokenSource();
        ChatActionBtn.Content = ""; // parar
        if (_mode != Mode.Expanded) ShowToast(GlyphChat, Loc.T("t.thinking"), text, sticky: true);

        try
        {
            var answer = await _assistant.AskAsync(text, note => AddChat("assistant", note), _busy.Token);
            AddChat("assistant", answer);
            EndSticky();
            if (_mode != Mode.Expanded) ShowToast(GlyphChat, Loc.T("t.assistant"), answer, seconds: Math.Clamp(answer.Length / 14.0, 4, 10));
            var speaking = S.SpeakReplies ? _voice.SpeakAsync(answer) : Task.FromResult(TimeSpan.Zero);
            // Terminou com uma pergunta ("posso ajudar com mais alguma coisa?"): já fica ouvindo a resposta.
            if (fromVoice && answer.TrimEnd().EndsWith("?")) _ = ContinueConversationAsync(speaking);
        }
        catch (OperationCanceledException)
        {
            AddChat("error", Loc.T("chat.canceled"), "");
            EndSticky();
        }
        catch (Exception ex)
        {
            Log.Error("Assistente", ex);
            var msg = FriendlyError(ex);
            AddChat("error", msg, "");
            EndSticky();
            if (_mode != Mode.Expanded) ShowToast("", Loc.T("t.assistant"), msg, seconds: 7);
        }
        finally
        {
            ResolveConfirm(false);
            _busy.Dispose();
            _busy = null;
            ChatActionBtn.Content = "";
        }
    }

    static string FriendlyError(Exception ex)
    {
        var name = ex.GetType().Name;
        if (name.Contains("Authentication") || name.Contains("Unauthorized") || name.Contains("401"))
            return Loc.T("err.apiKey");
        if (name.Contains("RateLimit") || name.Contains("429"))
            return Loc.T("err.rateLimit");
        if (ex is HttpRequestException || name.Contains("Connection") || name.Contains("Timeout"))
            return Loc.T("err.network");
        return ex.Message;
    }

    void AskInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var text = AskInput.Text;
            AskInput.Clear();
            e.Handled = true;
            _ = SubmitAsync(text);
        }
        else if (e.Key == Key.Escape)
        {
            AskInput.Clear();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    void AskInput_TextChanged(object sender, TextChangedEventArgs e) =>
        AskHint.Visibility = string.IsNullOrEmpty(AskInput.Text) ? Visibility.Visible : Visibility.Collapsed;

    void Mic_Click(object sender, RoutedEventArgs e)
    {
        if (!S.VoiceEnabled)
        {
            OpenSettings(3);
            return;
        }
        StartListening();
    }

    // Copia a conversa inteira (útil para colar num chat, num e-mail ou mandar um erro).
    void CopyChat_Click(object sender, RoutedEventArgs e)
    {
        if (_chat.Count == 0) return;
        var text = string.Join("\n", _chat.Select(c => c.Kind switch
        {
            "user" => Loc.T("copy.you") + ": " + c.Text,
            "assistant" => "Island: " + c.Text,
            "error" => Loc.T("copy.error") + ": " + c.Text,
            _ => "  • " + c.Text + (string.IsNullOrEmpty(c.Detail) ? "" : " — " + c.Detail),
        }));
        try
        {
            Clipboard.SetText(text);
            CopyChatBtn.Content = ""; // ✓ por um instante
            var t = MakeTimer(1200, () => { });
            t.Tick += (_, _) => { CopyChatBtn.Content = ""; t.Stop(); };
            t.Start();
        }
        catch { }
    }

    void ChatAction_Click(object sender, RoutedEventArgs e)
    {
        if (_busy != null)
        {
            ResolveConfirm(false);
            _busy.Cancel();
            return;
        }
        _chat.Clear();
        _assistant.Reset();
        UpdateChatEmpty();
    }

    public ChatEntry Step(string glyph, string text)
    {
        if (!Dispatcher.CheckAccess()) return Dispatcher.Invoke(() => Step(glyph, text));
        if (_mode == Mode.Toast && _sticky)
        {
            ToastIcon.Text = glyph;
            ToastTitle.Text = text;
        }
        return AddChat("step", text, glyph);
    }

    public Task<bool> ConfirmAsync(string title, string detail)
    {
        if (!Dispatcher.CheckAccess()) return Dispatcher.Invoke(() => ConfirmAsync(title, detail));

        ResolveConfirm(false);
        _confirm = new TaskCompletionSource<bool>();
        ConfirmTitle.Text = title;
        ConfirmDetail.Text = detail;
        ConfirmBar.Visibility = Visibility.Visible;
        if (S.PlaySounds) SystemSounds.Asterisk.Play();

        if (!IsVisible) Show();
        if (_tab != TabIndexAssistant) SelectTab(TabIndexAssistant);
        SetMode(Mode.Expanded);
        return _confirm.Task;
    }

    void ResolveConfirm(bool allowed)
    {
        ConfirmBar.Visibility = Visibility.Collapsed;
        var c = _confirm;
        _confirm = null;
        c?.TrySetResult(allowed);
        if (c != null) AddChat("step", allowed ? Loc.T("confirm.allowed") : Loc.T("confirm.denied"), allowed ? "" : "");
    }

    void ConfirmYes_Click(object sender, RoutedEventArgs e) => ResolveConfirm(true);
    void ConfirmNo_Click(object sender, RoutedEventArgs e) => ResolveConfirm(false);

    public void StartFocusTimer(int? minutes)
    {
        _isBreak = false;
        if (minutes is int m && m > 0) _data.FocusMinutes = Math.Clamp(m, 1, 180);
        _timerLeft = CurrentTimerLength;
        _timerEnd = DateTime.Now + _timerLeft;
        _timerRunning = true;
        UpdateTimerUi();
        RefreshCompact();
        SaveData();
    }

    public TodoItem? AddTask(string text, bool alarm = false) => AddTodo(text, alarm);

    public IReadOnlyList<TodoItem> PendingTasks =>
        _todos.Where(t => !t.Done).OrderBy(t => t.Due ?? DateTime.MaxValue).ToList();

    // Apaga ou conclui as tarefas que combinam com o texto ("" = todas as pendentes).
    public List<TodoItem> RemoveTasks(string query, bool complete)
    {
        var q = Fold(query);
        var matches = _todos.Where(t => !t.Done && (q.Length == 0 || Fold(t.Text).Contains(q))).ToList();
        foreach (var t in matches)
        {
            if (complete) t.Done = true;
            else _todos.Remove(t);
        }
        if (matches.Count > 0)
        {
            SaveData();
            UpdateTodoEmpty();
        }
        return matches;
    }

    static string Fold(string s) => new string(s.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD)
        .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray()).Trim();

    // ---------------- Persistência ----------------

    public void SaveData()
    {
        _data.Todos = _todos.ToList();
        _data.Shelf = _shelf.Select(s => s.Path).ToList();
        Storage.Save(_data);
    }
}
