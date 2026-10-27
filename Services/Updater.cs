using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Island;

// Novas versões: consulta os Releases do repositório oficial no GitHub, baixa o .zip, confere o SHA-256 que o
// próprio GitHub informa e troca os arquivos do programa. Configurações, modelos e memória ficam no AppData,
// fora da pasta do programa, e não são tocados.
public static class Updater
{
    public const string Repo = "devbluen/Island";
    const string AssetName = "Island-win-x64.zip";

    public sealed record Release(Version Version, string Tag, string Notes, string Page, string AssetUrl, long Size, string Sha256);

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Island-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public static Version Current
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    // "v1.2.0" / "1.2" / "1.2.0-beta" → 1.2.0
    public static Version? ParseTag(string tag)
    {
        var core = tag.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        if (!Version.TryParse(core.Contains('.') ? core : core + ".0", out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    // Último release publicado (null se não há ou se a internet falhou).
    public static async Task<Release?> LatestAsync(CancellationToken ct)
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (ParseTag(tag) is not { } version) return null;
        var asset = root.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => a.GetProperty("name").GetString() == AssetName);
        if (asset.ValueKind != JsonValueKind.Object) return null;
        var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
        return new Release(version, tag,
            root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            root.GetProperty("html_url").GetString() ?? "",
            asset.GetProperty("browser_download_url").GetString() ?? "",
            asset.GetProperty("size").GetInt64(),
            digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : "");
    }

    // Só devolve o release se ele for mais novo do que este programa.
    public static async Task<Release?> CheckAsync(CancellationToken ct)
    {
        var latest = await LatestAsync(ct);
        return latest != null && latest.Version > Current ? latest : null;
    }

    // Baixa, confere e prepara a troca. Depois disso é só fechar a ilha: um script espera ela sair,
    // copia os arquivos novos e abre a versão nova.
    public static async Task PrepareAsync(Release release, Action<double> progress, CancellationToken ct)
    {
        if (release.Sha256.Length != 64)
            throw new InvalidOperationException("O GitHub não informou a assinatura (SHA-256) deste arquivo; por segurança, não vou instalar.");
        if (!release.AssetUrl.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O endereço do arquivo não é do repositório oficial; não vou instalar.");

        var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        if (!CanWrite(installDir))
            throw new InvalidOperationException($"Sem permissão para escrever em {installDir}. Mova a pasta da Island para um lugar seu (ex.: Documentos) ou atualize manualmente.");

        var work = Path.Combine(Path.GetTempPath(), "IslandUpdate", release.Tag);
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        var zip = Path.Combine(work, AssetName);

        // Download com progresso.
        using (var resp = await Http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? release.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zip);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress(done / (double)total);
            }
        }

        // Confere a assinatura: o arquivo tem que ser exatamente o que foi publicado.
        string hash;
        await using (var fs = File.OpenRead(zip))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        if (hash != release.Sha256)
        {
            File.Delete(zip);
            throw new InvalidOperationException("O arquivo baixado não bate com a assinatura publicada no GitHub. Atualização cancelada.");
        }

        var extracted = Path.Combine(work, "files");
        ZipFile.ExtractToDirectory(zip, extracted);
        var source = Directory.GetFiles(extracted, "Island.exe", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName).FirstOrDefault()
            ?? throw new InvalidOperationException("O pacote baixado não tem o Island.exe.");

        // Script de troca: espera esta ilha fechar, copia (sem apagar nada que já exista) e abre a nova.
        var script = Path.Combine(work, "update.cmd");
        var exe = Path.Combine(installDir, "Island.exe");
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine($":wait");
        sb.AppendLine($"tasklist /FI \"PID eq {Environment.ProcessId}\" | find \"{Environment.ProcessId}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)");
        sb.AppendLine($"robocopy \"{source}\" \"{installDir}\" /E /R:5 /W:1 /NFL /NDL /NJH /NJS /NP >nul");
        sb.AppendLine($"start \"\" \"{exe}\"");
        sb.AppendLine($"rmdir /s /q \"{extracted}\"");
        sb.AppendLine($"del \"{zip}\"");
        File.WriteAllText(script, sb.ToString(), new UTF8Encoding(false));
        PendingScript = script;
    }

    public static string? PendingScript { get; private set; }

    // Roda o script (em segundo plano, sem janela). Chame logo antes de fechar a ilha.
    public static void LaunchPending()
    {
        if (PendingScript == null) return;
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{PendingScript}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        });
        Log.Info("Atualização: instalando e reiniciando");
    }

    static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".update-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
