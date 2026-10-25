using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Island;

// Uma linha no histórico do assistente: sua fala, a resposta, ou um passo que ele executou.
public sealed class ChatEntry : INotifyPropertyChanged
{
    string _text = "", _detail = "", _glyph = "";

    public string Kind { get; init; } = "step"; // user | assistant | step | error

    public string Text { get => _text; set => Set(ref _text, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public string Glyph { get => _glyph; set => Set(ref _glyph, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
