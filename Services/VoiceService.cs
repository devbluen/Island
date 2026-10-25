using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Whisper.net;
using Windows.Media.SpeechSynthesis;

namespace Island;

// Voz 100% no seu PC:
// - o áudio vem do microfone escolhido (NAudio/WASAPI);
// - um detector simples de fala separa as frases (início e fim pela energia do som);
// - o Whisper (whisper.cpp, local) transforma cada frase em texto.
// Nada de áudio sai do computador.
public sealed class VoiceService : IDisposable
{
    const int Rate = 16000, FrameSamples = Rate / 50; // quadros de 20 ms

    // ---------------- Configuração ----------------
    string _micId = "";
    string _model = "base";
    bool _wakeWanted, _monitor;
    double _sensitivity = 5;
    Regex? _wakeRegex; // frases extras do usuário

    // ---------------- Captura ----------------
    WasapiCapture? _capture;
    string? _captureMic;
    readonly object _gate = new();
    double _resamplePos;
    float _lastSample;
    readonly List<float> _frame = new(FrameSamples);

    // ---------------- Detector de fala ----------------
    readonly Queue<float[]> _preRoll = new();
    List<float>? _utterance;
    int _loudFrames, _silentFrames, _speechFrames;
    float _noise = 0.004f, _level;
    TaskCompletionSource<float[]?>? _command;
    DateTime _commandStarted;
    bool _commandHeardSpeech;

    // ---------------- Whisper ----------------
    WhisperFactory? _factory;
    string? _factoryModel;
    readonly SemaphoreSlim _whisperLock = new(1, 1);
    SoundPlayer? _player;

    public event Action<string?>? WakeDetected;           // com o resto da frase, se você já falou o pedido junto
    public event Action<string, bool>? WakeHeard;         // o que foi transcrito e se ativou
    public event Action<string>? WakeStatus;
    public event Action? Transcribing;                     // terminou de falar; convertendo em texto

    public bool IsListening => _command != null;

    // Para testes: com false, nunca abre o microfone; o áudio entra por FeedAudio.
    public bool UseMicrophone { get; set; } = true;
    public void FeedAudio(byte[] buffer, int bytes, WaveFormat format) => OnAudio(buffer, bytes, format);
    public float Level => _level;
    public string CurrentMicName { get; private set; } = "";

    public static string ModelsFolder => Path.Combine(Storage.Folder, "models");
    static string ModelFileName(string model) => model switch
    {
        "turbo" => "ggml-large-v3-turbo-q5_0.bin",
        "small" => "ggml-small-q5_1.bin",
        _ => "ggml-base-q5_1.bin",
    };

    public static string ModelFile(string model) => Path.Combine(ModelsFolder, ModelFileName(model));
    public static bool HasModel(string model) => File.Exists(ModelFile(model));

    // ---------------- Configuração ----------------

    public void Configure(string micId, string model, bool wakeEnabled, string[] extraPhrases, double sensitivity,
                          string wakeName = "island", IEnumerable<string>? learnedSamples = null, string language = "pt")
    {
        _language = language is "en" or "es" ? language : "pt";
        _wakeName = wakeName is "ilha" or "jarvis" ? wakeName : "island";
        _learned = (learnedSamples ?? Enumerable.Empty<string>())
            .Select(s => Words(s)).Where(w => w.Length > 0).ToArray();
        bool micChanged = micId != _micId;
        _micId = micId;
        _model = model is "small" or "turbo" ? model : "base";
        _sensitivity = Math.Clamp(sensitivity, 1, 10);
        _wakeWanted = wakeEnabled;
        _wakeRegex = BuildWakeRegex(extraPhrases);
        if (micChanged) StopCapture();
        UpdateCapture();
        if (_factoryModel != _model && (wakeEnabled || UseMicrophone)) _ = WarmUpAsync();
    }

    // Mantém o microfone aberto só quando algo precisa dele.
    void UpdateCapture()
    {
        if (!UseMicrophone) return;
        bool need = _wakeWanted || _monitor || _command != null;
        if (need && !HasModel(_model) && !_monitor && _command == null)
        {
            WakeStatus?.Invoke(Loc.T("vs.needModel"));
            StopCapture();
            return;
        }
        if (need) StartCapture();
        else StopCapture();
        if (!_wakeWanted) WakeStatus?.Invoke(Loc.T("vs.off"));
    }

    public void SetMonitor(bool on)
    {
        _monitor = on;
        UpdateCapture();
    }

    // O microfone padrão do Windows mudou (só importa se você usa o "padrão").
    public void OnDefaultMicrophoneChanged()
    {
        if (_micId.Length > 0) return;
        StopCapture();
        UpdateCapture();
    }

    void StartCapture()
    {
        lock (_gate)
        {
            if (_capture != null) return;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                MMDevice device;
                try
                {
                    device = _micId.Length > 0 ? enumerator.GetDevice(_micId) : enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                    if (device.State != DeviceState.Active) device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                }
                catch
                {
                    device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                }
                CurrentMicName = device.FriendlyName;

                var capture = new WasapiCapture(device, true, 50);
                capture.DataAvailable += (_, e) => OnAudio(e.Buffer, e.BytesRecorded, capture.WaveFormat);
                capture.RecordingStopped += (_, e) =>
                {
                    if (e.Exception == null) return;
                    // Microfone desconectado etc.: tenta de novo daqui a pouco.
                    WakeStatus?.Invoke(Loc.F("vs.micUnavailable", e.Exception.Message));
                    lock (_gate) { if (_capture == capture) _capture = null; }
                    capture.Dispose();
                    Task.Delay(3000).ContinueWith(_ => UpdateCapture());
                };
                _resamplePos = 0;
                _frame.Clear();
                capture.StartRecording();
                _capture = capture;
                _captureMic = _micId;
                if (_wakeWanted) WakeStatus?.Invoke(Loc.F("vs.listening", Loc.T("wake." + _wakeName), CurrentMicName));
            }
            catch (Exception e)
            {
                _capture = null;
                WakeStatus?.Invoke(Loc.F("vs.micOpenFail", e.Message));
            }
        }
    }

    void StopCapture()
    {
        WasapiCapture? c;
        lock (_gate)
        {
            c = _capture;
            _capture = null;
            _utterance = null;
        }
        if (c == null) return;
        try { c.StopRecording(); } catch { }
        c.Dispose();
        _level = 0;
    }

    // ---------------- Áudio → quadros de 20 ms a 16 kHz mono ----------------

    void OnAudio(byte[] buffer, int bytes, WaveFormat fmt)
    {
        int channels = fmt.Channels;
        bool isFloat = fmt.BitsPerSample == 32;
        int bytesPerSample = fmt.BitsPerSample / 8;
        int frames = bytes / (bytesPerSample * channels);
        double step = (double)fmt.SampleRate / Rate;

        for (int i = 0; i < frames; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                int offset = (i * channels + c) * bytesPerSample;
                sum += isFloat ? BitConverter.ToSingle(buffer, offset) : BitConverter.ToInt16(buffer, offset) / 32768f;
            }
            float sample = sum / channels;

            // Reamostragem linear para 16 kHz.
            while (_resamplePos <= 1.0)
            {
                _frame.Add(_lastSample + (sample - _lastSample) * (float)_resamplePos);
                _resamplePos += step;
                if (_frame.Count == FrameSamples)
                {
                    OnFrame(_frame.ToArray());
                    _frame.Clear();
                }
            }
            _resamplePos -= 1.0;
            _lastSample = sample;
        }
    }

    // ---------------- Detector de fala ----------------

    void OnFrame(float[] frame)
    {
        double sq = 0;
        float peak = 0;
        foreach (var s in frame)
        {
            sq += s * s;
            peak = Math.Max(peak, Math.Abs(s));
        }
        float rms = (float)Math.Sqrt(sq / frame.Length);
        _level = Math.Max(_level * 0.8f, peak);

        // Sensibilidade 1 (exige falar alto) … 10 (pega até sussurro).
        float factor = (float)(5.5 - _sensitivity * 0.38);
        float minimum = (float)(0.012 - _sensitivity * 0.001);
        float threshold = Math.Max(minimum, _noise * factor);

        var command = _command;
        if (command != null && !_commandHeardSpeech && DateTime.Now - _commandStarted > TimeSpan.FromSeconds(7))
        {
            FinishCommand(null); // não falou nada
            return;
        }

        if (_utterance == null)
        {
            // Ruído de fundo: aprende devagar enquanto ninguém fala.
            if (rms < threshold) _noise = Math.Max(0.0015f, _noise * 0.97f + rms * 0.03f);

            _preRoll.Enqueue(frame);
            while (_preRoll.Count > 15) _preRoll.Dequeue(); // 300 ms antes da fala

            _loudFrames = rms > threshold ? _loudFrames + 1 : 0;
            if (_loudFrames >= 3)
            {
                _utterance = new List<float>(Rate * 4);
                _utteranceStart = DateTime.Now;
                foreach (var f in _preRoll) _utterance.AddRange(f);
                _preRoll.Clear();
                _silentFrames = 0;
                _speechFrames = _loudFrames;
                if (command != null) _commandHeardSpeech = true;
            }
            return;
        }

        _utterance.AddRange(frame);
        if (rms > threshold * 0.7f)
        {
            _silentFrames = 0;
            _speechFrames++;
        }
        else
        {
            _silentFrames++;
        }

        int endSilence = command != null ? 50 : 40; // 1 s ao dar um comando, 0,8 s no resto
        bool tooLong = _utterance.Count > Rate * 15;
        if (_silentFrames < endSilence && !tooLong) return;

        var samples = _utterance.ToArray();
        int speech = _speechFrames;
        _utterance = null;
        _loudFrames = 0;
        if (speech < 12) return; // menos de ~0,25 s de fala: provavelmente um barulho

        if (command != null)
        {
            FinishCommand(samples);
        }
        else if (_utteranceStart < _followUpUntil && _utteranceStart >= _followUpFrom)
        {
            // Falou "Ei Island" e parou (ou a ilha te fez uma pergunta): esta é a frase do pedido.
            _followUpUntil = DateTime.MinValue;
            Enqueue(() => FollowUpAsync(samples));
        }
        else if (_wakeWanted)
        {
            Enqueue(() => CheckWakeAsync(samples));
        }
    }

    // Fila: as frases são processadas uma de cada vez, sem descartar nenhuma (no máximo 3 esperando).
    Task _queue = Task.CompletedTask;
    int _pending;

    void Enqueue(Func<Task> work)
    {
        if (Interlocked.Increment(ref _pending) > 3)
        {
            Interlocked.Decrement(ref _pending);
            return;
        }
        lock (_gate)
        {
            _queue = _queue.ContinueWith(async _ =>
            {
                try { await work(); }
                catch (Exception e) { Error?.Invoke(e); }
                finally { Interlocked.Decrement(ref _pending); }
            }).Unwrap();
        }
    }

    public event Action<Exception>? Error;

    DateTime _followUpUntil = DateTime.MinValue, _followUpFrom = DateTime.MinValue, _utteranceStart;

    // A ilha fez uma pergunta: depois que ela terminar de falar, a próxima frase é a resposta (sem palavra de ativação).
    public void ContinueConversation(TimeSpan speaking, int seconds = 8)
    {
        _followUpFrom = DateTime.Now + speaking; // o que for dito antes disso é a própria voz da ilha no alto-falante
        _followUpUntil = _followUpFrom.AddSeconds(seconds);
        ExpireFollowUpAfter(speaking + TimeSpan.FromSeconds(seconds + 0.5));
    }

    void ExpireFollowUpAfter(TimeSpan delay)
    {
        var until = _followUpUntil;
        _ = Task.Delay(delay).ContinueWith(_ =>
        {
            if (_followUpUntil == until && _followUpUntil != DateTime.MinValue && DateTime.Now >= _followUpUntil && _utterance == null)
            {
                _followUpUntil = DateTime.MinValue;
                FollowUpExpired?.Invoke();
            }
        });
    }

    // Avisado quando a janela de 7 s depois do "Ei Island" passou sem você falar.
    public event Action? FollowUpExpired;

    async Task FollowUpAsync(float[] samples)
    {
        Transcribing?.Invoke();
        var text = await TranscribeAsync(samples, careful: true);
        int end = FindWake(text);
        if (end >= 0) text = AfterWake(text, end);
        text = text.Trim(' ', ',', '.');
        if (text.Length >= 2) WakeDetected?.Invoke(text);
        else FollowUpExpired?.Invoke();
    }

    // ---------------- Palavra de ativação ----------------

    // Frases extras que você cadastrou nas configurações.
    static Regex? BuildWakeRegex(string[] extra)
    {
        var alternatives = extra.Select(p => p.Trim()).Where(p => p.Length > 2)
            .Select(p => @"\b" + string.Join(@"[\s,!.]+", p.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape)) + @"\b")
            .ToList();
        return alternatives.Count == 0 ? null
            : new Regex($"(?:{string.Join("|", alternatives)})[\\s,!.?]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    static readonly HashSet<string> Greetings = new() { "ei", "hey", "hei", "ey", "oi", "eai", "ola", "alo", "hi", "ok", "okay", "oye", "hola" };

    // Como o Whisper costuma escrever cada nome quando ouve em português.
    static readonly Regex IslandStrict = new(@"^(island|islan|ilha|eilha|eiland|eisland|eailand|ailand|ailend|ayland|aylend|iland|ilend)$");
    static readonly Regex IslandLike = new(@"^(island|islan|ilha|aylend|ayland|(ai|ay|i|ei)s?l[aeiy]{0,2}n?[dtkgs]?e?|l[iy]n[dtkg]?)$"); // "aisle", "ilind", "link"…
    static readonly Regex IlhaName = new(@"^(ilha|ilhas|ilia|eilha|oilha)$");
    static readonly Regex JarvisName = new(@"^(jarvis|jarves|jarvi|jarbas|jarviz)$");

    string _wakeName = "island";

    (Regex Strict, Regex Loose) WakeNames => _wakeName switch
    {
        "ilha" => (IlhaName, IlhaName),
        "jarvis" => (JarvisName, JarvisName),
        _ => (IslandStrict, IslandLike),
    };

    string _language = "pt";

    // Dica para o Whisper: o nome da ativação e palavras comuns dos pedidos, no idioma escolhido.
    string WakePrompt
    {
        get
        {
            var name = _wakeName switch { "ilha" => "Ilha", "jarvis" => "Jarvis", _ => "Island" };
            return _language switch
            {
                "en" => $"Hey {name}, open the project in VS Code, build, play on Spotify, volume, pause.",
                "es" => $"Oye {name}, abre el proyecto en VS Code, compila, pon en Spotify, volumen, pausa.",
                _ => $"Ei {name}, abre o projeto no VS Code, compila, toca no Spotify, volume, pausa.",
            };
        }
    }

    public bool IsWakeListening => _capture != null && _wakeWanted;

    static string Fold(string s) => new(s.ToLowerInvariant().Normalize(NormalizationForm.FormD)
        .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());

    // Acha o "Ei, Island" no texto. Devolve onde termina (para pegar o pedido que veio junto) ou -1.
    int FindWake(string text)
    {
        var (strict, loose) = WakeNames;
        var words = Regex.Matches(text, @"\p{L}+").ToList();
        for (int i = 0; i < words.Count && i < 4; i++) // precisa estar no começo da frase
        {
            var w = Fold(words[i].Value);
            if (i == 0 && strict.IsMatch(w))
                return words[i].Index + words[i].Length; // "Island, abre…" / "Jarvis, abre…"
            if (i + 1 >= words.Count) break;

            int next = i + 1;
            bool greeting = Greetings.Contains(w);
            if (w == "e" && Fold(words[next].Value) == "ai" && next + 1 < words.Count)
            {
                greeting = true; // "E aí, Island"
                next++;
            }
            var name = Fold(words[next].Value);
            // Depois de "ei/hey/oi/e aí" aceita variações soltas ("Ei, Link"); depois de "ô", só o nome certo.
            if ((greeting && loose.IsMatch(name)) || (w is "o" or "em" && i == 0 && strict.IsMatch(name)))
                return words[next].Index + words[next].Length;

            // "Ei, ai, Zilance": o Whisper partiu o "Ai-land" em dois pedaços.
            if (_wakeName == "island" && greeting && w != "e" && name is "ai" or "ay" or "i" && next + 1 < words.Count)
                return words[next + 1].Index + words[next + 1].Length;
        }

        // Grafia aproximada ("Javis", "Kavis", "Javs" para Jarvis; "Ilia" para Ilha): compara por semelhança.
        for (int i = 0; i < Math.Min(words.Count, 3); i++)
        {
            var w = Fold(words[i].Value);
            bool afterGreeting = i > 0 && Greetings.Contains(Fold(words[i - 1].Value));
            bool close = _wakeName switch
            {
                "jarvis" => (i == 0 || afterGreeting) && w.Length >= 4 && Similarity(w, "jarvis") >= 0.66,
                "ilha" => afterGreeting && Similarity(w, "ilha") >= 0.74,
                _ => afterGreeting && w.Length >= 4 && new[] { "island", "ailand", "ailend" }.Any(t => Similarity(w, t) >= 0.66),
            };
            if (close) return words[i].Index + words[i].Length;
        }
        if (_wakeRegex?.Match(text) is { Success: true } m) return m.Index + m.Length;
        return FindLearned(text, words);
    }

    // ---------------- Treino com a sua voz ----------------

    string[][] _learned = Array.Empty<string[]>();

    static string[] Words(string text) => Regex.Matches(Fold(text), @"\p{L}+").Select(m => m.Value).ToArray();

    // Compara o começo da frase com o jeito que o Whisper escreveu a SUA pronúncia durante o treino.
    int FindLearned(string text, List<Match> words)
    {
        if (_learned.Length == 0 || words.Count == 0) return -1;
        var folded = words.Select(w => Fold(w.Value)).ToArray();
        foreach (var sample in _learned)
        {
            // Testa o mesmo número de palavras do exemplo (e uma a mais/menos, porque o Whisper às vezes junta ou separa).
            for (int k = Math.Max(1, sample.Length - 1); k <= Math.Min(folded.Length, sample.Length + 1); k++)
            {
                if (Similarity(string.Join(" ", folded.Take(k)), string.Join(" ", sample)) >= 0.72)
                    return words[k - 1].Index + words[k - 1].Length;
            }
        }
        return -1;
    }

    static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return 1.0 - d[a.Length, b.Length] / (double)Math.Max(a.Length, b.Length);
    }

    // Grava uma frase e devolve exatamente o que o Whisper entendeu (usado no treino).
    public Task<string?> RecordSampleAsync() => ListenCoreAsync(raw: true);

    // Para testes.
    public int FindWakeIn(string text) => FindWake(text);

    static string AfterWake(string text, int end) => text[end..].Trim(' ', ',', '.', '!', '?', ';', ':');

    // Carrega o modelo de voz antes de precisar (a primeira vez, principalmente na placa de vídeo, demora alguns segundos).
    public async Task WarmUpAsync()
    {
        if (!HasModel(_model)) return;
        try { await TranscribeAsync(new float[Rate / 2]); } catch { }
    }

    async Task CheckWakeAsync(float[] samples)
    {
        if (!HasModel(_model)) return;
        // Frases longas: a palavra de ativação vem no começo, então olha só os primeiros 3 s (mais leve).
        const int head = Rate * 3;
        var text = await TranscribeAsync(samples.Length > head ? samples[..head] : samples);
        int found = FindWake(text);
        WakeHeard?.Invoke(text, found >= 0);
        if (found < 0) return;

        // Achou. Se tem pedido junto, transcreve de novo com cuidado (mais preciso) para pegar o pedido certo.
        var full = AfterWake(text, found).Length >= 2 || samples.Length > head
            ? await TranscribeAsync(samples, careful: true)
            : text;
        int end = FindWake(full);
        var rest = end >= 0 ? AfterWake(full, end) : AfterWake(text, found);

        if (rest.Length >= 2)
        {
            WakeDetected?.Invoke(rest);
        }
        else
        {
            // Só "Ei Island": a próxima frase (em até 7 s) é o pedido.
            _followUpFrom = DateTime.MinValue;
            _followUpUntil = DateTime.Now.AddSeconds(7);
            WakeDetected?.Invoke(null);
            ExpireFollowUpAfter(TimeSpan.FromSeconds(7.5));
        }
    }

    // ---------------- Comando ----------------

    // Espera você falar uma frase e devolve o texto (null se não falou nada).
    public Task<string?> ListenAsync() => ListenCoreAsync(raw: false);

    async Task<string?> ListenCoreAsync(bool raw)
    {
        if (!HasModel(_model))
            throw new InvalidOperationException(Loc.T("vs.needModel"));

        var tcs = new TaskCompletionSource<float[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commandStarted = DateTime.Now;
        _commandHeardSpeech = false;
        _utterance = null;
        _command = tcs;
        UpdateCapture();
        if (_capture == null && UseMicrophone)
        {
            _command = null;
            throw new InvalidOperationException(Loc.T("vs.micOpenFailHelp"));
        }

        var samples = await tcs.Task;
        UpdateCapture();
        if (samples == null) return null;

        Transcribing?.Invoke();
        var text = await TranscribeAsync(samples, careful: !raw);
        if (raw) return text.Trim().Length > 0 ? text.Trim() : null;
        // Se falou "Ei Island" junto do pedido, tira isso do texto.
        int end = FindWake(text);
        text = end >= 0 ? AfterWake(text, end) : text.Trim(' ', ',', '.');
        return text.Length > 0 ? text : null;
    }

    void FinishCommand(float[]? samples)
    {
        var tcs = _command;
        _command = null;
        tcs?.TrySetResult(samples);
    }

    // Segunda vez no atalho: encerra a escuta (usa o que já foi falado).
    public void Stop()
    {
        if (_command == null) return;
        var partial = _utterance?.ToArray();
        _utterance = null;
        FinishCommand(partial is { Length: > Rate / 3 } ? partial : null);
    }

    // ---------------- Whisper ----------------

    static readonly Regex Hallucinations = new(
        @"legendas? (pela|por)|amara\.org|inscreva-se|obrigad[oa] por assistir|^\W*(\[.*\]|\(.*\)|♪+)\W*$",
        RegexOptions.IgnoreCase);

    // careful = beam search (testa várias hipóteses): mais preciso, ~2x mais lento. Usado nos pedidos.
    public async Task<string> TranscribeAsync(float[] samples, bool careful = false)
    {
        await _whisperLock.WaitAsync();
        try
        {
            return await Task.Run(async () =>
            {
                if (_factory == null || _factoryModel != _model)
                {
                    _factory?.Dispose();
                    _factory = WhisperFactory.FromPath(ModelFile(_model));
                    _factoryModel = _model;
                }
                var builder = _factory.CreateBuilder()
                    .WithLanguage(_language)
                    .WithPrompt(WakePrompt)
                    .WithThreads(Math.Clamp(Environment.ProcessorCount - 2, 2, 8));
                using var processor = careful ? builder.WithBeamSearchSamplingStrategy().Build() : builder.Build();

                // Microfone baixo (comum em Bluetooth): ajusta o volume da frase antes de transcrever.
                float peak = samples.Length == 0 ? 0 : samples.Max(Math.Abs);
                if (peak is > 0.002f and < 0.6f)
                {
                    float gain = Math.Min(0.8f / peak, 25f);
                    samples = samples.Select(s => s * gain).ToArray();
                }

                var sb = new StringBuilder();
                await foreach (var seg in processor.ProcessAsync(samples)) sb.Append(seg.Text);
                var text = sb.ToString().Trim();
                text = Hallucinations.IsMatch(text) ? "" : text;
                if (SaveRecordings && peak > 0.002f) SaveRecording(samples, text);
                return text;
            });
        }
        finally
        {
            _whisperLock.Release();
        }
    }

    // ---------------- Gravações para diagnóstico (só no PC) ----------------

    public bool SaveRecordings { get; set; }
    public static string RecordingsFolder => Path.Combine(Storage.Folder, "gravacoes");

    static void SaveRecording(float[] samples, string text)
    {
        try
        {
            Directory.CreateDirectory(RecordingsFolder);
            var name = Path.Combine(RecordingsFolder, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            using (var w = new WaveFileWriter(name + ".wav", WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1)))
                w.WriteSamples(samples, 0, samples.Length);
            File.WriteAllText(name + ".txt", text);
            // Guarda só as 20 mais recentes.
            foreach (var old in Directory.GetFiles(RecordingsFolder, "*.wav").OrderByDescending(f => f).Skip(20))
            {
                File.Delete(old);
                File.Delete(Path.ChangeExtension(old, ".txt"));
            }
        }
        catch { }
    }

    // Baixa o modelo do repositório oficial do whisper.cpp (Hugging Face).
    public static async Task DownloadModelAsync(string model, Action<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ModelsFolder);
        var file = ModelFile(model);
        var url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{ModelFileName(model)}";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? 0, done = 0;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(file + ".part"))
        {
            var buf = new byte[1 << 16];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress(done / (double)total);
            }
        }
        File.Move(file + ".part", file, true);
    }

    // ---------------- Sons e fala ----------------

    // Dois toques curtos e suaves (gerados aqui mesmo, sem arquivo de som).
    public void Chime()
    {
        try
        {
            const int rate = 22050;
            var notes = new[] { (880.0, 0.09), (1318.5, 0.14) };
            int total = notes.Sum(n => (int)(n.Item2 * rate));
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write("RIFF"u8); w.Write(36 + total * 2); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(total * 2);
            foreach (var (freq, dur) in notes)
            {
                int n = (int)(dur * rate);
                for (int i = 0; i < n; i++)
                {
                    double env = Math.Min(1, i / (rate * 0.01)) * Math.Exp(-4.0 * i / n);
                    w.Write((short)(Math.Sin(2 * Math.PI * freq * i / rate) * env * 9000));
                }
            }
            ms.Position = 0;
            _player?.Stop();
            _player = new SoundPlayer(ms);
            _player.Play();
        }
        catch { }
    }

    // Voz natural escolhida (Piper). null = voz do Windows.
    public string? TtsVoice { get; set; }

    int _speakGen;

    // Para a fala na hora (e descarta a que ainda está sendo preparada). Usado quando você chama a ilha de novo.
    public void StopSpeaking()
    {
        _speakGen++;
        try { _player?.Stop(); } catch { }
    }

    // Devolve quanto tempo a fala dura (para saber quando a ilha parou de falar).
    public async Task<TimeSpan> SpeakAsync(string text)
    {
        int gen = ++_speakGen;
        try
        {
            byte[] wav;
            if (TtsVoice != null && PiperTts.IsInstalled(TtsVoice))
            {
                wav = await PiperTts.SynthesizeAsync(text, TtsVoice);
            }
            else
            {
                using var synth = new SpeechSynthesizer();
                var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Language.StartsWith(_language, StringComparison.OrdinalIgnoreCase));
                if (voice != null) synth.Voice = voice;
                using var stream = await synth.SynthesizeTextToStreamAsync(text);
                var buffer = new MemoryStream();
                await stream.AsStreamForRead().CopyToAsync(buffer);
                wav = buffer.ToArray();
            }
            if (gen != _speakGen) return TimeSpan.Zero; // foi interrompida enquanto preparava
            _player?.Stop();
            _player = new SoundPlayer(new MemoryStream(wav));
            _player.Play();
            int byteRate = wav.Length > 44 ? BitConverter.ToInt32(wav, 28) : 0;
            return byteRate > 0 ? TimeSpan.FromSeconds((wav.Length - 44) / (double)byteRate) : TimeSpan.Zero;
        }
        catch (Exception e)
        {
            Error?.Invoke(e);
            return TimeSpan.Zero;
        }
    }

    public void Dispose()
    {
        StopCapture();
        _factory?.Dispose();
    }
}
