using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Island;

public sealed class AppData
{
    public List<TodoItem> Todos { get; set; } = new();
    public List<string> Shelf { get; set; } = new();
    public int FocusMinutes { get; set; } = 25;
    public int BreakMinutes { get; set; } = 5;
    public AppSettings Settings { get; set; } = new();
}

public sealed class TodoItem : INotifyPropertyChanged
{
    bool _done;

    public string Text { get; set; } = "";
    public DateTime? Due { get; set; }
    public bool Notified { get; set; }

    public bool Done
    {
        get => _done;
        set { _done = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Done))); }
    }

    public bool Alarm { get; set; } // toca até você parar (em vez de só avisar)
    public bool Daily { get; set; } // repete todo dia no mesmo horário

    [JsonIgnore]
    public string DueLabel => Due is DateTime d ? DateParser.Label(d, DateTime.Now) : "";

    [JsonIgnore]
    public string Icons => (Alarm ? "" : "") + (Daily ? " " : "");

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record ShelfItem(string Path, string Name, ImageSource? Icon);

// Tudo fica em %AppData%\Island\data.json — nada sai do computador.
public static class Storage
{
    public static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island");

    static string FilePath => Path.Combine(Folder, "data.json");

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppData Load()
    {
        if (Demo.Active) return Demo.Data(); // screenshots: nunca lê os seus dados
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppData>(File.ReadAllText(FilePath)) ?? new AppData();
        }
        catch { }
        return new AppData();
    }

    public static void Save(AppData data)
    {
        if (Demo.Active) return; // screenshots: nunca grava nada
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, Options));
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }
}
