using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Island;

// Instala e liga o Ollama (IA local, gratuita, código aberto) para quem ainda não tem.
public static class OllamaSetup
{
    static string AppDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama");

    public static bool IsInstalled() =>
        File.Exists(Path.Combine(AppDir, "ollama.exe")) || FindInPath("ollama.exe") != null;

    static string? FindInPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), exe);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    // 1º tenta o winget (gerenciador oficial do Windows); se não houver, baixa o instalador oficial do site do Ollama.
    public static async Task InstallAsync(Action<string> status, CancellationToken ct)
    {
        if (FindInPath("winget.exe") != null || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe")))
        {
            status("winget install Ollama.Ollama…");
            var psi = new ProcessStartInfo("winget",
                "install --id Ollama.Ollama -e --silent --accept-package-agreements --accept-source-agreements")
            { UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync(ct);
            if (IsInstalled()) return;
        }

        status("ollama.com/download…");
        var setup = Path.Combine(Path.GetTempPath(), "OllamaSetup.exe");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) })
        await using (var src = await http.GetStreamAsync("https://ollama.com/download/OllamaSetup.exe", ct))
        await using (var dst = File.Create(setup))
            await src.CopyToAsync(dst, ct);

        using var installer = Process.Start(new ProcessStartInfo(setup, "/SILENT") { UseShellExecute = true })!;
        await installer.WaitForExitAsync(ct);
        if (!IsInstalled()) throw new InvalidOperationException("Ollama installer did not finish.");
    }

    // Garante que o serviço está respondendo (abre o app do Ollama se preciso).
    public static async Task<bool> EnsureRunningAsync(string url, CancellationToken ct)
    {
        if (await OllamaAssistant.ListModelsAsync(url) != null) return true;
        var app = Path.Combine(AppDir, "ollama app.exe");
        var exe = File.Exists(app) ? app : FindInPath("ollama.exe");
        if (exe == null) return false;
        try
        {
            Process.Start(exe.EndsWith("ollama.exe", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo(exe, "serve") { UseShellExecute = false, CreateNoWindow = true }
                : new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch { return false; }

        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(1000, ct);
            if (await OllamaAssistant.ListModelsAsync(url) != null) return true;
        }
        return false;
    }
}
