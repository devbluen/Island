using System;
using System.Linq;
using Microsoft.Win32;

namespace Island;

// Descobre o que o PC aguenta, para escolher modelos de voz e de IA do tamanho certo.
public static class Hardware
{
    public sealed record Gpu(string Name, double VramGb);

    // Placa de vídeo com mais memória dedicada (lida do registro do driver, que informa valores acima de 4 GB).
    public static Gpu? BestGpu()
    {
        Gpu? best = null;
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return null;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(sub);
                if (k == null) continue;
                var name = k.GetValue("DriverDesc") as string ?? "";
                if (name.Length == 0 || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)) continue;

                double bytes = k.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                    _ => k.GetValue("HardwareInformation.MemorySize") switch
                    {
                        int i => (uint)i,
                        long l => l,
                        byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                        _ => 0,
                    },
                };
                var gb = Math.Round(bytes / (1024.0 * 1024 * 1024), 1);
                if (best == null || gb > best.VramGb) best = new Gpu(name, gb);
            }
        }
        catch { }
        return best;
    }

    public static double RamGb() => Math.Round(SystemStats.Memory().TotalGb);

    // Modelo de voz (Whisper): o maior que roda rápido aqui.
    public static string RecommendVoiceModel(Gpu? gpu) => gpu?.VramGb switch
    {
        >= 4 => "turbo",
        >= 2 => "small",
        _ => "base",
    };

    // Modelo de IA local (Ollama), ou null se o PC não roda IA local com folga.
    public static string? RecommendOllamaModel(Gpu? gpu) => gpu?.VramGb switch
    {
        >= 10 => "qwen3:8b",
        >= 6 => "qwen3:4b",
        >= 4 => "qwen3:1.7b",
        _ => null,
    };
}
