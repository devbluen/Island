using System;
using System.IO;

namespace Island;

// Registro de erros em %AppData%\Island\logs\island.log (só no PC).
// Serve para entender problemas: dá para abrir pelas configurações e mandar para quem for ajudar.
public static class Log
{
    static readonly object Gate = new();
    public static string Folder => Path.Combine(Storage.Folder, "logs");
    public static string FilePath => Path.Combine(Folder, "island.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string context, Exception e) =>
        Write("ERRO", $"{context}: {e.GetType().Name}: {e.Message}\n    {e.StackTrace?.Replace("\n", "\n    ")}");

    public static void Error(string context, string message) => Write("ERRO", $"{context}: {message}");

    static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > 1_000_000)
                    File.Move(FilePath, Path.Combine(Folder, "island.old.log"), true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}\n");
            }
        }
        catch { }
    }
}
