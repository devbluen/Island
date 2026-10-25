using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Island;

// Assistente de primeira execução: deixa tudo pronto em qualquer PC (idioma, voz, IA local).
public partial class SetupWindow : Window
{
    const int Steps = 4;
    static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59));
    static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x20));
    static readonly Brush Blue = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));

    readonly MainWindow _main;
    readonly AppSettings _s;
    readonly Hardware.Gpu? _gpu;
    readonly string? _recommendedAi;
    readonly DispatcherTimer _level = new() { Interval = TimeSpan.FromMilliseconds(60) };
    CancellationTokenSource? _work;
    int _step;
    bool _loading = true;

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public SetupWindow(MainWindow main)
    {
        _main = main;
        _s = main.Settings;
        InitializeComponent();
        DataContext = _s;

        _gpu = Hardware.BestGpu();
        _recommendedAi = Hardware.RecommendOllamaModel(_gpu);

        // Primeira vez: já deixa marcado o que serve para este PC.
        if (!_s.SetupDone)
        {
            if (string.IsNullOrEmpty(_s.Language)) _s.Language = Loc.SystemLanguage();
            _s.VoiceModel = Hardware.RecommendVoiceModel(_gpu);
            _s.OllamaModel = _recommendedAi ?? "qwen3:4b";
            _s.Provider = _recommendedAi != null ? "local" : "none";
            _main.ApplySettings();
        }

        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => Changed()));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => Changed()));
        AddHandler(LostFocusEvent, new RoutedEventHandler((_, _) => Changed()));

        SourceInitialized += (_, _) =>
        {
            int dark = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, 4);
        };
        Loaded += (_, _) =>
        {
            _loading = false;
            Refresh();
        };
        Closed += (_, _) =>
        {
            _level.Stop();
            _work?.Cancel();
            _main.Voice.SetMonitor(false);
        };
        _level.Tick += (_, _) =>
        {
            var track = (FrameworkElement)MicLevel.Parent;
            MicLevel.Width = Math.Clamp(_main.Voice.Level * 1.6, 0, 1) * track.ActualWidth;
        };
        ShowStep(0);
    }

    void Changed()
    {
        if (_loading) return;
        _main.ApplySettings();
        _main.SaveData();
        Refresh();
    }

    void Refresh()
    {
        // Passo 1: o PC
        GpuText.Text = _gpu is { VramGb: > 0 } g ? Loc.F("wiz.pcGpu", g.Name, g.VramGb) : Loc.T("wiz.pcNoGpu");
        RamText.Text = Loc.F("wiz.pcRam", Hardware.RamGb());
        var voiceName = Hardware.RecommendVoiceModel(_gpu) switch { "turbo" => Loc.T("set.vmMax"), "small" => Loc.T("set.vmPrecise"), _ => Loc.T("set.vmFast") };
        RecommendText.Text = Loc.F("wiz.pcRecommend", voiceName, _recommendedAi ?? Loc.T("set.none"));

        // Passo 2: voz
        bool hasVoice = VoiceService.HasModel(_s.VoiceModel);
        VoiceDownloadBtn.Visibility = hasVoice || _work != null ? Visibility.Collapsed : Visibility.Visible;
        if (_work == null)
        {
            VoiceStatus.Text = hasVoice ? Loc.T("set.vmReady") : Loc.T("set.vmNeeded");
            VoiceStatus.Foreground = hasVoice ? Green : Amber;
        }
        IlhaChip.Visibility = Loc.Lang == "pt" ? Visibility.Visible : Visibility.Collapsed;

        // Passo 3: IA
        LocalPanel.Visibility = _s.Provider == "local" ? Visibility.Visible : Visibility.Collapsed;
        ClaudePanel.Visibility = _s.Provider == "claude" ? Visibility.Visible : Visibility.Collapsed;
        AiNote.Text = _s.Provider == "local" && _recommendedAi == null
            ? (Loc.Lang switch
            {
                "en" => "This PC has no graphics card with enough memory for the local AI to run fast. Built-in commands or Claude are recommended.",
                "es" => "Este PC no tiene tarjeta gráfica con memoria suficiente para que la IA local sea rápida. Se recomiendan los comandos integrados o Claude.",
                _ => "Este PC não tem placa de vídeo com memória suficiente para a IA local ficar rápida. Recomendado: só comandos prontos ou Claude.",
            })
            : "";
        if (_s.Provider == "local" && _work == null) _ = CheckOllamaAsync();

        // Passo 4
        DoneText.Text = Loc.F("wiz.doneText", Loc.T("wake." + _s.WakeName), _s.Hotkey);
        BuildMics();
    }

    // ---------------- Navegação ----------------

    void ShowStep(int step)
    {
        _step = Math.Clamp(step, 0, Steps - 1);
        StackPanel[] steps = { Step0, Step1, Step2, Step3 };
        for (int i = 0; i < steps.Length; i++) steps[i].Visibility = i == _step ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < Dots.Children.Count; i++)
            ((Border)Dots.Children[i]).Background = i <= _step ? Blue : new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255));

        BackBtn.Visibility = _step == 0 ? Visibility.Hidden : Visibility.Visible;
        SkipBtn.Visibility = _step == Steps - 1 ? Visibility.Hidden : Visibility.Visible;
        NextBtn.SetResourceReference(ContentProperty, _step == Steps - 1 ? "L.wiz.finish" : "L.wiz.next");

        bool voiceStep = _step == 1;
        _main.Voice.SetMonitor(voiceStep && _s.VoiceEnabled);
        if (voiceStep) _level.Start(); else _level.Stop();

        // Chegou na voz sem o modelo: já começa a baixar o recomendado.
        if (voiceStep && !VoiceService.HasModel(_s.VoiceModel) && _work == null) VoiceDownload_Click(this, new RoutedEventArgs());
        if (!_loading) Refresh();
    }

    void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step == Steps - 1)
        {
            Finish();
            return;
        }
        ShowStep(_step + 1);
    }

    void Back_Click(object sender, RoutedEventArgs e) => ShowStep(_step - 1);

    void Skip_Click(object sender, RoutedEventArgs e) => Finish();

    void Finish()
    {
        _s.SetupDone = true;
        _main.ApplySettings();
        _main.SaveData();
        Close();
    }

    // ---------------- Voz ----------------

    async void VoiceDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_work != null) return;
        var model = _s.VoiceModel;
        _work = new CancellationTokenSource();
        VoiceDownloadBtn.Visibility = Visibility.Collapsed;
        VoiceStatus.Foreground = Brushes.White;
        try
        {
            await VoiceService.DownloadModelAsync(model, p => Dispatcher.BeginInvoke(() => VoiceStatus.Text = Loc.F("set.vmDownloading", p)), _work.Token);
            _main.ApplySettings();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Download do modelo de voz", ex);
            VoiceStatus.Text = Loc.F("set.downloadFailed", ex.Message);
        }
        finally
        {
            _work = null;
            if (IsLoaded) Refresh();
        }
    }

    void BuildMics()
    {
        MicList.Children.Clear();
        var mics = _main.AudioService.ListMicrophones().Where(m => m.Connected).ToList();
        var defaultName = mics.FirstOrDefault(m => m.IsDefault)?.Name;

        void Add(string tag, string label)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Chip"), GroupName = "mic", Content = label, Tag = tag, IsChecked = _s.MicrophoneId == tag };
            rb.Checked += (_, _) =>
            {
                if (_s.MicrophoneId == (string)rb.Tag) return;
                _s.MicrophoneId = (string)rb.Tag;
                _main.ApplySettings();
                _main.SaveData();
            };
            MicList.Children.Add(rb);
        }

        Add("", defaultName == null ? Loc.T("set.micDefault") : $"{Loc.T("set.micDefault")} ({defaultName})");
        foreach (var m in mics) Add(m.Id, m.Name);
    }

    // ---------------- IA local ----------------

    async Task CheckOllamaAsync()
    {
        OllamaStatus.Text = Loc.T("set.checking");
        OllamaStatus.Foreground = Brushes.White;
        var models = await OllamaAssistant.ListModelsAsync(_s.OllamaUrl);
        if (models == null && OllamaSetup.IsInstalled() && _work == null)
        {
            // Instalado mas parado: tenta ligar sozinho.
            if (await OllamaSetup.EnsureRunningAsync(_s.OllamaUrl, CancellationToken.None))
                models = await OllamaAssistant.ListModelsAsync(_s.OllamaUrl);
        }

        bool installed = models != null || OllamaSetup.IsInstalled();
        bool hasModel = models != null && OllamaAssistant.HasModel(models, _s.OllamaModel);
        InstallBtn.Visibility = installed || _work != null ? Visibility.Collapsed : Visibility.Visible;
        PullBtn.Visibility = models != null && !hasModel && _work == null ? Visibility.Visible : Visibility.Collapsed;

        if (models == null)
        {
            OllamaStatus.Text = Loc.T("set.ollamaNotRunning");
            OllamaStatus.Foreground = Amber;
        }
        else if (hasModel)
        {
            OllamaStatus.Text = Loc.F("set.ollamaReady", _s.OllamaModel);
            OllamaStatus.Foreground = Green;
        }
        else
        {
            OllamaStatus.Text = Loc.F("set.ollamaNoModel", _s.OllamaModel);
            OllamaStatus.Foreground = Amber;
        }
    }

    void Check_Click(object sender, RoutedEventArgs e) => _ = CheckOllamaAsync();

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_work != null) return;
        _work = new CancellationTokenSource();
        InstallBtn.Visibility = Visibility.Collapsed;
        try
        {
            LocalProgress.Text = Loc.T("set.installing");
            await OllamaSetup.InstallAsync(s => Dispatcher.BeginInvoke(() => LocalProgress.Text = Loc.T("set.installing") + "  " + s), _work.Token);
            await OllamaSetup.EnsureRunningAsync(_s.OllamaUrl, _work.Token);
            LocalProgress.Text = "";
        }
        catch (Exception ex)
        {
            Log.Error("Instalação do Ollama", ex);
            LocalProgress.Text = Loc.F("set.downloadFailed", ex.Message);
        }
        finally
        {
            _work = null;
        }
        await CheckOllamaAsync();
        // Instalou: já baixa o modelo.
        if (PullBtn.Visibility == Visibility.Visible) Pull_Click(this, new RoutedEventArgs());
    }

    async void Pull_Click(object sender, RoutedEventArgs e)
    {
        if (_work != null) return;
        _work = new CancellationTokenSource();
        PullBtn.Visibility = Visibility.Collapsed;
        var model = _s.OllamaModel;
        try
        {
            await OllamaAssistant.PullAsync(_s.OllamaUrl, model, (status, pct) => Dispatcher.BeginInvoke(() =>
                LocalProgress.Text = pct >= 0 ? Loc.F("set.downloading", model, pct) : $"{model}: {status}"), _work.Token);
            LocalProgress.Text = Loc.T("set.downloadDone");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Download do modelo de IA", ex);
            LocalProgress.Text = Loc.F("set.downloadFailed", ex.Message);
        }
        finally
        {
            _work = null;
        }
        await CheckOllamaAsync();
    }

    // ---------------- Claude ----------------

    void KeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _s.ApiKeyProtected = Secret.Protect(KeyBox.Password);
        _main.SaveData();
    }

    void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string url)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
