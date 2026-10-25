using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Data;

namespace Island;

public sealed class AppSettings
{
    // Geral
    public string Language { get; set; } = "";      // pt | en | es ("" = ainda não escolhido: usa o idioma do Windows)
    public bool SetupDone { get; set; }             // o assistente de primeira execução já foi concluído

    // Aparência
    public string Shape { get; set; } = "pill"; // pill | ball
    public double Scale { get; set; } = 1.0;
    public bool LockPosition { get; set; }
    public double? PillX { get; set; } // centro da ilha compacta, em DIPs; null = encaixada no topo
    public double? PillY { get; set; }
    public bool ShowClockWhenIdle { get; set; } = true;

    // Comportamento
    public bool ExpandOnHover { get; set; } = true;
    public bool HideInFullscreen { get; set; } = true;
    public bool ShowSystemStats { get; set; } = true;
    public double ToastSeconds { get; set; } = 2.2;

    // Avisos
    public bool NotifyVolume { get; set; } = true;
    public bool NotifyBrightness { get; set; } = true;
    public bool NotifyCapsLock { get; set; } = true;
    public bool NotifyLanguage { get; set; } = true;
    public bool NotifyPower { get; set; } = true;
    public bool NotifyLowBattery { get; set; } = true;
    public bool NotifyNetwork { get; set; } = true;
    public bool NotifyAudioDevice { get; set; } = true;
    public bool NotifyTrack { get; set; } = true;
    public bool PlaySounds { get; set; } = true;

    // Assistente
    public string Provider { get; set; } = "local"; // local (Ollama) | claude | none (só comandos prontos)
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "qwen3:8b";
    public string ApiKeyProtected { get; set; } = "";
    public string Model { get; set; } = "claude-opus-5-5";
    public string Effort { get; set; } = "medium";
    public bool ConfirmCommands { get; set; } = true;
    public string ProjectFolders { get; set; } = DefaultProjectFolders();
    public string PreferredEditor { get; set; } = "code";
    public bool VoiceEnabled { get; set; } = true;
    public bool SpeakReplies { get; set; }
    public string TtsVoice { get; set; } = "";      // "" = voz natural padrão do idioma; "windows" = voz antiga do Windows; ou id da voz Piper
    public string Hotkey { get; set; } = "Ctrl+Alt+Space";
    public bool WakeWordEnabled { get; set; } = true;
    public string WakePhrases { get; set; } = "";
    public string WakeName { get; set; } = "island"; // island ("Ei, Island") | ilha ("Ei, Ilha") | jarvis
    public List<string> WakeSamples { get; set; } = new(); // como o Whisper escreveu a sua pronúncia no treino
    public double WakeSensitivity { get; set; } = 5; // sensibilidade do microfone: 1 = só voz alta, 10 = pega sussurro
    public string MicrophoneId { get; set; } = "";   // "" = microfone padrão do Windows
    public string VoiceModel { get; set; } = "turbo"; // turbo (máximo) | small (preciso) | base (leve)

    public string[] WakePhraseList() => WakePhrases.Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public string ExtraInstructions { get; set; } = "";

    // O que o assistente aprendeu com você ("meu site de anti cheat é xxx.com"), um fato por item. Só neste PC.
    public List<string> Memories { get; set; } = new();

    // Para a caixa de texto das configurações: um fato por linha.
    [System.Text.Json.Serialization.JsonIgnore]
    public string MemoriesText
    {
        get => string.Join(Environment.NewLine, Memories);
        set => Memories = value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    // Spotify (API oficial; o Client ID não é segredo, o token fica criptografado)
    public string SpotifyClientId { get; set; } = "";
    public string SpotifyRefreshProtected { get; set; } = "";

    // Guarda as últimas gravações de voz (só no PC) para ajudar a ajustar o reconhecimento.
    public bool SaveVoiceRecordings { get; set; }

    public string[] ProjectFolderList() => ProjectFolders
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(Environment.ExpandEnvironmentVariables)
        .ToArray();

    static string DefaultProjectFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(home, "source", "repos"),
            Path.Combine(home, "Projects"),
            Path.Combine(home, "Documents", "GitHub"),
            Path.Combine(home, "Desktop"),
        };
        return string.Join("\n", candidates.Where(Directory.Exists));
    }
}

// A chave da API é criptografada com o DPAPI do Windows: só o seu usuário, neste PC, consegue lê-la.
public static class Secret
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Island.ApiKey");

    public static string Protect(string plain) => string.IsNullOrWhiteSpace(plain) ? ""
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain.Trim()), Entropy, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return null;
        }
    }

    public static string? ApiKey(AppSettings s) =>
        Unprotect(s.ApiKeyProtected) is { Length: > 0 } key ? key : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
}

// IsChecked="{Binding Shape, Converter={StaticResource Eq}, ConverterParameter=ball}"
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? parameter : Binding.DoNothing;
}
