using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;
using Anthropic;
using Forms = System.Windows.Forms;

namespace Island;

public partial class SettingsWindow : Window
{
    readonly MainWindow _main;
    readonly AppSettings _s;
    bool _loading = true;

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public SettingsWindow(MainWindow main)
    {
        _main = main;
        _s = main.Settings;
        InitializeComponent();
        DataContext = _s;

        AutostartSwitch.IsChecked = Autostart.IsEnabled;
        KeyStatus.Text = Secret.ApiKey(_s) is { Length: > 0 } ? Loc.T("set.keyConfigured") : Loc.T("set.keyNone");

        // Qualquer mudança é salva e aplicada na hora.
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => Changed()));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => Changed()));
        AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((_, _) => Changed()));
        AddHandler(LostFocusEvent, new RoutedEventHandler((_, _) => Changed()));

        SourceInitialized += (_, _) =>
        {
            int dark = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, ref dark, 4);
        };
        Loaded += (_, _) => _loading = false;

        UpdateWakeInfo();
        _main.WakeInfo += UpdateWakeInfo;
        _main.MicChanged += BuildMicList;
        _main.UpdateChanged += UpdateUpdateUi;
        UpdateUpdateUi();
        Closed += (_, _) =>
        {
            _main.UpdateChanged -= UpdateUpdateUi;
            _main.SaveData(); // texto "Sobre você" e a memória editada à mão
            _main.WakeInfo -= UpdateWakeInfo;
            _main.MicChanged -= BuildMicList;
            _levelTimer.Stop();
            _pull?.Cancel();
            _voiceDownload?.Cancel();
            _ttsDownload?.Cancel();
            _main.Voice.SetMonitor(false);
        };

        UpdateProviderUi();
        UpdateVoiceModelUi();
        UpdateTrainUi();
        UpdateSpotifyUi();
        _ = CheckOllamaAsync();
        BuildMicList();
        BuildTtsList();
        _levelTimer.Tick += (_, _) => UpdateMicLevel();
        _levelTimer.Start();
    }

    // ---------------- IA local / Claude ----------------

    readonly System.Windows.Threading.DispatcherTimer _levelTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    System.Threading.CancellationTokenSource? _pull;
    bool _buildingMics;

    void UpdateProviderUi()
    {
        LocalOptions.Visibility = _s.Provider == "local" ? Visibility.Visible : Visibility.Collapsed;
        ClaudeOptions.Visibility = _s.Provider == "claude" ? Visibility.Visible : Visibility.Collapsed;
    }

    static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59));
    static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x20));

    async System.Threading.Tasks.Task CheckOllamaAsync()
    {
        OllamaStatus.Text = "Verificando…";
        OllamaStatus.Foreground = Brushes.White;
        var models = await OllamaAssistant.ListModelsAsync(_s.OllamaUrl);
        if (models == null)
        {
            OllamaStatus.Text = Loc.T("set.ollamaNotRunning");
            OllamaStatus.Foreground = Amber;
            PullBtn.IsEnabled = false;
            return;
        }
        PullBtn.IsEnabled = _pull == null;
        if (OllamaAssistant.HasModel(models, _s.OllamaModel))
        {
            OllamaStatus.Text = Loc.F("set.ollamaReady", _s.OllamaModel);
            OllamaStatus.Foreground = Green;
            PullBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            OllamaStatus.Text = Loc.F("set.ollamaNoModel", _s.OllamaModel);
            OllamaStatus.Foreground = Amber;
            PullBtn.Visibility = Visibility.Visible;
        }
    }

    void CheckOllama_Click(object sender, RoutedEventArgs e) => _ = CheckOllamaAsync();

    async void PullModel_Click(object sender, RoutedEventArgs e)
    {
        if (_pull != null) return;
        _pull = new System.Threading.CancellationTokenSource();
        PullBtn.IsEnabled = false;
        var model = _s.OllamaModel;
        try
        {
            await OllamaAssistant.PullAsync(_s.OllamaUrl, model, (status, pct) => Dispatcher.BeginInvoke(() =>
                PullStatus.Text = pct >= 0 ? Loc.F("set.downloading", model, pct) : $"{model}: {status}"), _pull.Token);
            PullStatus.Text = Loc.T("set.downloadDone");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PullStatus.Text = Loc.F("set.downloadFailed", ex.Message);
        }
        finally
        {
            _pull = null;
            PullBtn.IsEnabled = true;
        }
        await CheckOllamaAsync();
    }

    // ---------------- Microfones ----------------

    void BuildMicList()
    {
        _buildingMics = true;
        MicList.Children.Clear();
        var mics = _main.AudioService.ListMicrophones();
        var defaultName = mics.FirstOrDefault(m => m.IsDefault && m.Connected)?.Name;

        void AddChip(string tag, string label, bool enabled)
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Chip"),
                GroupName = "mic",
                Content = label,
                Tag = tag,
                IsChecked = _s.MicrophoneId == tag,
                IsEnabled = enabled,
                Opacity = enabled ? 1 : 0.4,
            };
            rb.Checked += (_, _) =>
            {
                if (_buildingMics) return;
                _s.MicrophoneId = (string)rb.Tag;
                Changed();
            };
            MicList.Children.Add(rb);
        }

        AddChip("", defaultName == null ? Loc.T("set.micDefault") : $"{Loc.T("set.micDefault")} ({defaultName})", true);
        foreach (var mic in mics)
            AddChip(mic.Id, mic.Connected ? mic.Name : mic.Name + "  " + Loc.T("set.micDisconnected"), mic.Connected);
        _buildingMics = false;
    }

    void UpdateMicLevel()
    {
        bool show = IsVisible && PageAssistant.Visibility == Visibility.Visible;
        _main.Voice.SetMonitor(show && _s.VoiceEnabled);
        if (!show) return;
        var track = (FrameworkElement)MicLevel.Parent;
        MicLevel.Width = Math.Clamp(_main.Voice.Level * 1.6, 0, 1) * track.ActualWidth;
    }

    // ---------------- Modelo de voz (Whisper) ----------------

    System.Threading.CancellationTokenSource? _voiceDownload;

    // ---------------- Voz do assistente (falar as respostas) ----------------

    System.Threading.CancellationTokenSource? _ttsDownload;

    void BuildTtsList()
    {
        TtsList.Children.Clear();
        var current = _main.TtsVoiceId ?? "windows";

        void Add(string tag, string label)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Chip"), GroupName = "tts", Content = label, Tag = tag, IsChecked = current == tag };
            rb.Checked += async (_, _) =>
            {
                if (_buildingMics || _s.TtsVoice == tag) return;
                _s.TtsVoice = tag;
                Changed();
                if (tag != "windows" && !PiperTts.IsInstalled(tag)) await DownloadTtsAsync(tag);
                TtsPreview_Click(this, new RoutedEventArgs());
            };
            TtsList.Children.Add(rb);
        }

        _buildingMics = true;
        foreach (var v in PiperTts.Voices.Where(v => v.Lang == Loc.Lang))
            Add(v.Id, $"{v.Name} · {Loc.T(v.Female ? "set.ttsFemale" : "set.ttsMale")}{(PiperTts.IsInstalled(v.Id) ? "" : " ↓")}");
        Add("windows", Loc.T("set.ttsWindows"));
        _buildingMics = false;
    }

    async System.Threading.Tasks.Task DownloadTtsAsync(string id)
    {
        if (PiperTts.Find(id) is not { } voice || _ttsDownload != null) return;
        _ttsDownload = new System.Threading.CancellationTokenSource();
        TtsPreviewBtn.IsEnabled = false;
        try
        {
            await PiperTts.DownloadAsync(voice, p => Dispatcher.BeginInvoke(() => TtsStatus.Text = Loc.F("set.vmDownloading", p)), _ttsDownload.Token);
            TtsStatus.Text = Loc.T("set.vmReady");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Download da voz", ex);
            TtsStatus.Text = Loc.F("set.downloadFailed", ex.Message);
        }
        finally
        {
            _ttsDownload = null;
            TtsPreviewBtn.IsEnabled = true;
            BuildTtsList();
        }
    }

    void TtsPreview_Click(object sender, RoutedEventArgs e)
    {
        _main.Voice.TtsVoice = _main.TtsVoiceId;
        _ = _main.Voice.SpeakAsync(Loc.T("set.ttsSample"));
    }

    // ---------------- Spotify ----------------

    void UpdateSpotifyUi()
    {
        bool connected = _main.Spotify.IsConnected;
        SpotifyStatus.Text = connected
            ? Loc.T("set.spConnected")
            : Loc.T("set.spNotConnected");
        SpotifyStatus.Foreground = connected ? Green : Amber;
        SpotifySetup.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        SpotifyConnectBtn.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        SpotifyDisconnectBtn.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
    }

    void CopyRedirect_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(SpotifyService.RedirectUri); } catch { }
    }

    async void SpotifyConnect_Click(object sender, RoutedEventArgs e)
    {
        Changed(); // garante que o Client ID digitado foi salvo
        SpotifyConnectBtn.IsEnabled = false;
        SpotifyStatus.Foreground = Brushes.White;
        SpotifyStatus.Text = Loc.T("set.spWaiting");
        try
        {
            await _main.Spotify.ConnectAsync(System.Threading.CancellationToken.None);
            _main.SaveData();
        }
        catch (Exception ex)
        {
            SpotifyStatus.Foreground = Amber;
            SpotifyStatus.Text = Loc.F("set.spFailed", ex.Message);
            SpotifyConnectBtn.IsEnabled = true;
            return;
        }
        SpotifyConnectBtn.IsEnabled = true;
        UpdateSpotifyUi();
    }

    void SpotifyDisconnect_Click(object sender, RoutedEventArgs e)
    {
        _main.Spotify.Disconnect();
        _main.SaveData();
        UpdateSpotifyUi();
    }

    // ---------------- Treino da palavra de ativação ----------------

    bool _training;

    string WakeLabel => _s.WakeName switch { "ilha" => "Ei, Ilha", "jarvis" => "Jarvis", _ => "Ei, Island" };

    void UpdateTrainUi()
    {
        if (_training) return;
        ForgetTrainBtn.Visibility = _s.WakeSamples.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TrainBtn.Content = _s.WakeSamples.Count > 0 ? Loc.T("set.trainAgain") : Loc.T("set.trainStart");
        TrainStatus.Text = _s.WakeSamples.Count > 0
            ? Loc.F("set.trainLearned", string.Join("  ·  ", _s.WakeSamples.Select(w => $"“{w}”")))
            : "";
    }

    async void TrainVoice_Click(object sender, RoutedEventArgs e)
    {
        if (_training) return;
        if (!VoiceService.HasModel(_s.VoiceModel))
        {
            TrainStatus.Text = Loc.T("set.trainNeedModel");
            return;
        }
        _training = true;
        TrainBtn.IsEnabled = ForgetTrainBtn.IsEnabled = false;
        var heard = new List<string>();
        int misses = 0;
        try
        {
            while (heard.Count < 3 && misses < 4)
            {
                TrainStatus.Foreground = Brushes.White;
                TrainStatus.Text = Loc.F("set.trainSay", WakeLabel, heard.Count + 1);
                _main.Voice.Chime();
                await System.Threading.Tasks.Task.Delay(450);
                var text = await _main.Voice.RecordSampleAsync();
                if (string.IsNullOrWhiteSpace(text))
                {
                    misses++;
                    TrainStatus.Text = Loc.T("set.trainNoHear");
                    await System.Threading.Tasks.Task.Delay(1000);
                    continue;
                }
                heard.Add(text.Trim(' ', '.', '!', '?', ','));
                TrainStatus.Text = Loc.F("set.trainHeard", heard[^1]);
                await System.Threading.Tasks.Task.Delay(1100);
            }

            if (heard.Count == 0)
            {
                TrainStatus.Text = Loc.T("set.trainFailed");
                return;
            }
            _s.WakeSamples = heard.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _main.ApplySettings();
            _main.SaveData();
            _training = false;
            UpdateTrainUi();
            TrainStatus.Foreground = Green;
            TrainStatus.Text = Loc.F("set.trainDone", TrainStatus.Text);
        }
        catch (Exception ex)
        {
            TrainStatus.Text = "Erro no treino: " + ex.Message;
        }
        finally
        {
            _training = false;
            TrainBtn.IsEnabled = ForgetTrainBtn.IsEnabled = true;
        }
    }

    void ForgetTraining_Click(object sender, RoutedEventArgs e)
    {
        _s.WakeSamples = new List<string>();
        Changed();
        UpdateTrainUi();
    }

    void UpdateVoiceModelUi()
    {
        BaseModelInfo.Text = VoiceService.HasModel("base") ? "57 MB · " + Loc.T("set.vmDownloaded") : "57 MB";
        SmallModelInfo.Text = VoiceService.HasModel("small") ? "181 MB · " + Loc.T("set.vmDownloaded") : "181 MB";
        TurboModelInfo.Text = VoiceService.HasModel("turbo") ? "547 MB · " + Loc.T("set.vmDownloaded") : "547 MB · " + Loc.T("set.vmNeedsGpu");
        bool missing = !VoiceService.HasModel(_s.VoiceModel);
        VoiceModelBtn.Visibility = missing && _voiceDownload == null ? Visibility.Visible : Visibility.Collapsed;
        if (_voiceDownload == null)
            VoiceModelStatus.Text = missing ? Loc.T("set.vmNeeded") : "";
    }

    async void DownloadVoiceModel_Click(object sender, RoutedEventArgs e)
    {
        var model = _s.VoiceModel;
        _voiceDownload = new System.Threading.CancellationTokenSource();
        UpdateVoiceModelUi();
        try
        {
            await VoiceService.DownloadModelAsync(model, p => Dispatcher.BeginInvoke(() =>
                VoiceModelStatus.Text = Loc.F("set.vmDownloading", p)), _voiceDownload.Token);
            VoiceModelStatus.Text = Loc.T("set.vmReady");
            _main.ApplySettings(); // religa a escuta agora que o modelo existe
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            VoiceModelStatus.Text = Loc.F("set.downloadFailed", ex.Message);
        }
        finally
        {
            _voiceDownload = null;
            UpdateVoiceModelUi();
        }
    }

    void UpdateWakeInfo()
    {
        WakeOptions.Visibility = _s.WakeWordEnabled ? Visibility.Visible : Visibility.Collapsed;
        WakeStatusLabel.Text = "● " + _main.WakeStatusText;
        WakeStatusLabel.Foreground = _main.Voice.IsWakeListening
            ? new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59))
            : (Brush)FindResource("Dim");
        WakeHeardLabel.Text = string.IsNullOrEmpty(_main.LastWakeHeard)
            ? Loc.T("set.heardEmpty")
            : Loc.T("set.heardList") + "\n" + _main.LastWakeHeard;
    }

    public void ShowPage(int page)
    {
        foreach (var rb in FindNav())
            if (rb.Tag?.ToString() == page.ToString()) rb.IsChecked = true;
    }

    System.Collections.Generic.IEnumerable<RadioButton> FindNav()
    {
        var root = (Panel)((Border)((Grid)Content).Children[0]).Child;
        foreach (var child in root.Children)
            if (child is RadioButton rb) yield return rb;
    }

    void Changed()
    {
        if (_loading) return;
        var provider = _s.Provider;
        var model = _s.OllamaModel;
        _main.ApplySettings();
        _main.SaveData();
        UpdateWakeInfo();
        UpdateProviderUi();
        UpdateVoiceModelUi();
        if (Loc.Lang != _shownLang)
        {
            // Idioma mudou: refaz os textos que o código escreve.
            _shownLang = Loc.Lang;
            UpdateTrainUi();
            UpdateSpotifyUi();
            BuildMicList();
            BuildTtsList();
            _ = CheckOllamaAsync();
        }
        if (provider == "local" && (model != _lastCheckedModel)) { _lastCheckedModel = model; _ = CheckOllamaAsync(); }
    }

    string? _lastCheckedModel;
    string _shownLang = Loc.Lang;

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageLook == null) return;
        int i = int.Parse(((FrameworkElement)sender).Tag.ToString()!);
        UIElement[] pages = { PageLook, PageNotify, PageBehavior, PageAssistant, PagePrivacy };
        for (int k = 0; k < pages.Length; k++) pages[k].Visibility = k == i ? Visibility.Visible : Visibility.Collapsed;
    }

    void ResetPosition_Click(object sender, RoutedEventArgs e)
    {
        _s.PillX = _s.PillY = null;
        Changed();
    }

    void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading) Autostart.Set(AutostartSwitch.IsChecked == true);
    }

    void KeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _s.ApiKeyProtected = Secret.Protect(KeyBox.Password);
        KeyStatus.Text = KeyBox.Password.Length > 0 ? Loc.T("set.keySaved") : Loc.T("set.keyRemoved");
        KeyStatus.Foreground = (Brush)FindResource("Faint");
        _main.SaveData();
    }

    async void TestKey_Click(object sender, RoutedEventArgs e)
    {
        var key = Secret.ApiKey(_s);
        if (string.IsNullOrWhiteSpace(key))
        {
            KeyStatus.Text = Loc.T("set.keyPaste");
            return;
        }
        KeyStatus.Text = Loc.T("set.keyTesting");
        try
        {
            var client = new AnthropicClient { ApiKey = key };
            var model = await client.Models.Retrieve(_s.Model);
            KeyStatus.Text = Loc.F("set.keyWorks", model.DisplayName);
            KeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59));
        }
        catch (Exception ex)
        {
            KeyStatus.Text = Loc.F("set.keyFails", ex.Message);
            KeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x5E));
        }
    }

    void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.Tab) return;

        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None)
        {
            HotkeyBox.Text = Loc.T("set.hotkeyNeedsMod");
            return;
        }
        _s.Hotkey = HotkeyService.Format(mods, key);
        HotkeyBox.Text = _s.Hotkey;
        Changed();
        if (!_main.HotkeyRegistered) HotkeyBox.Text = _s.Hotkey + "  " + Loc.T("set.hotkeyInUse");
    }

    void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new Forms.FolderBrowserDialog { Description = Loc.T("set.addFolderTitle"), UseDescriptionForTitle = true };
        if (dlg.ShowDialog() != Forms.DialogResult.OK) return;
        _s.ProjectFolders = string.IsNullOrWhiteSpace(_s.ProjectFolders) ? dlg.SelectedPath : _s.ProjectFolders.TrimEnd() + "\n" + dlg.SelectedPath;
        FoldersBox.Text = _s.ProjectFolders;
        Changed();
    }

    void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string url)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Log.Folder);
        if (File.Exists(Log.FilePath)) Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
        else Process.Start("explorer.exe", Log.Folder);
    }

    // ---------------- Atualizações ----------------

    System.Threading.CancellationTokenSource? _updateDownload;

    void UpdateUpdateUi()
    {
        var u = _main.AvailableUpdate;
        UpdateStatus.Text = Loc.F("upd.current", Updater.Current.ToString(3)) + (u != null ? "  ·  " + Loc.F("upd.newFound", u.Tag) : "");
        InstallUpdateBtn.Visibility = UpdateNotesBtn.Visibility = u != null ? Visibility.Visible : Visibility.Collapsed;
        if (u != null) InstallUpdateBtn.Content = Loc.F("upd.installNow", u.Tag);
    }

    async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateBtn.IsEnabled = false;
        UpdateStatus.Text = Loc.T("upd.checking");
        try
        {
            var found = await _main.CheckForUpdatesAsync(manual: true);
            UpdateUpdateUi();
            if (found == null) UpdateStatus.Text = Loc.F("upd.none", Updater.Current.ToString(3));
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = Loc.F("upd.error", ex.Message);
        }
        finally
        {
            CheckUpdateBtn.IsEnabled = true;
        }
    }

    async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_main.AvailableUpdate is not { } release) return;
        InstallUpdateBtn.IsEnabled = CheckUpdateBtn.IsEnabled = false;
        _updateDownload = new System.Threading.CancellationTokenSource();
        try
        {
            await _main.InstallUpdateAsync(release, p => Dispatcher.BeginInvoke(() =>
                UpdateStatus.Text = Loc.F("upd.downloading", release.Tag) + $" {p:P0}"), _updateDownload.Token);
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = Loc.F("upd.error", ex.Message);
            InstallUpdateBtn.IsEnabled = CheckUpdateBtn.IsEnabled = true;
        }
    }

    void UpdateNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_main.AvailableUpdate is { } u) Process.Start(new ProcessStartInfo(u.Page) { UseShellExecute = true });
    }

    void RunSetup_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _main.OpenSetup();
    }

    void OpenData_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Storage.Folder);
        Process.Start("explorer.exe", Storage.Folder);
    }
}
