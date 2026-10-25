using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Island;

// Vozes neurais naturais rodando no PC (Piper, código aberto: github.com/rhasspy/piper).
// Muito mais humanas que a voz antiga do Windows, e nada sai do computador.
public static class PiperTts
{
    public sealed record Voice(string Id, string Lang, string Name, bool Female, string RemotePath);

    public static readonly Voice[] Voices =
    {
        new("pt_BR-faber-medium", "pt", "Faber", false, "pt/pt_BR/faber/medium"),
        new("pt_BR-cadu-medium", "pt", "Cadu", false, "pt/pt_BR/cadu/medium"),
        new("pt_BR-jeff-medium", "pt", "Jeff", false, "pt/pt_BR/jeff/medium"),
        new("en_US-ryan-high", "en", "Ryan", false, "en/en_US/ryan/high"),
        new("en_US-lessac-high", "en", "Lessac", true, "en/en_US/lessac/high"),
        new("en_US-amy-medium", "en", "Amy", true, "en/en_US/amy/medium"),
        new("es_MX-claude-high", "es", "Claude", true, "es/es_MX/claude/high"),
        new("es_ES-davefx-medium", "es", "Davefx", false, "es/es_ES/davefx/medium"),
        new("es_MX-ald-medium", "es", "Ald", false, "es/es_MX/ald/medium"),
    };

    const string EngineUrl = "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip";
    const string VoicesUrl = "https://huggingface.co/rhasspy/piper-voices/resolve/main/";

    static string Folder => Path.Combine(Storage.Folder, "tts");
    static string Exe => Path.Combine(Folder, "piper", "piper.exe");
    static string Model(string id) => Path.Combine(Folder, "voices", id + ".onnx");

    public static Voice? Find(string id) => Voices.FirstOrDefault(v => v.Id == id);
    public static Voice DefaultFor(string lang) => Voices.First(v => v.Lang == lang);

    public static bool IsInstalled(string id) =>
        File.Exists(Exe) && File.Exists(Model(id)) && File.Exists(Model(id) + ".json");

    // Baixa o motor (uma vez, ~22 MB) e a voz (~60 MB). progress: 0–1.
    public static async Task DownloadAsync(Voice voice, Action<double> progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        Directory.CreateDirectory(Path.Combine(Folder, "voices"));

        if (!File.Exists(Exe))
        {
            var zip = Path.Combine(Folder, "piper.zip");
            await DownloadFileAsync(http, EngineUrl, zip, p => progress(p * 0.25), ct);
            ZipFile.ExtractToDirectory(zip, Folder, true);
            File.Delete(zip);
        }

        var url = VoicesUrl + voice.RemotePath + "/" + voice.Id;
        await DownloadFileAsync(http, url + ".onnx.json", Model(voice.Id) + ".json", _ => { }, ct);
        await DownloadFileAsync(http, url + ".onnx", Model(voice.Id), p => progress(0.25 + p * 0.75), ct);
    }

    static async Task DownloadFileAsync(HttpClient http, string url, string file, Action<double> progress, CancellationToken ct)
    {
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

    // Gera a fala e devolve o áudio WAV.
    public static async Task<byte[]> SynthesizeAsync(string text, string id, CancellationToken ct = default)
    {
        var output = Path.Combine(Path.GetTempPath(), $"island-tts-{Guid.NewGuid():N}.wav");
        var psi = new ProcessStartInfo(Exe, $"--model \"{Model(id)}\" --output_file \"{output}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(Exe)!,
        };
        using var p = Process.Start(psi)!;
        _ = p.StandardError.ReadToEndAsync(ct); // o Piper escreve o progresso aqui; precisa ser lido
        // Uma linha só: quebras de linha virariam frases separadas com pausas estranhas.
        await p.StandardInput.WriteLineAsync(text.Replace('\n', ' ').Replace('\r', ' '));
        p.StandardInput.Close();
        await p.WaitForExitAsync(ct);
        try
        {
            return await File.ReadAllBytesAsync(output, ct);
        }
        finally
        {
            try { File.Delete(output); } catch { }
        }
    }
}
