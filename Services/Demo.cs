using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Island;

// Modo de demonstração (Island.exe --screenshots <pasta>): dados fictícios para as imagens do GitHub.
// Enquanto ele está ligado, nada do seu perfil é lido nem gravado (veja Storage, AudioService e OllamaAssistant).
public static class Demo
{
    public static bool Active { get; set; }
    public static string Lang { get; set; } = "en";

    static bool Pt => Lang == "pt";
    static bool Es => Lang == "es";

    public static string T(string en, string pt, string es) => Pt ? pt : Es ? es : en;

    public static AppData Data()
    {
        var now = DateTime.Now;
        var tomorrow = now.Date.AddDays(1);
        var data = new AppData
        {
            Settings = new AppSettings
            {
                Language = Lang,
                SetupDone = true,
                WakeName = "jarvis",
                SpeakReplies = true,
                ProjectFolders = Pt ? @"C:\Users\voce\Projetos" : @"C:\Users\you\Projects",
            },
            Todos = new List<TodoItem>
            {
                new() { Text = T("Team meeting", "Reunião com o time", "Reunión con el equipo"), Due = tomorrow.AddHours(18) },
                new() { Text = T("Pay the electricity bill", "Pagar a conta de luz", "Pagar la luz"), Due = now.Date.AddDays(3).AddHours(10) },
                new() { Text = T("Gym", "Academia", "Gimnasio"), Due = tomorrow.AddHours(7), Alarm = true, Daily = true },
            },
        };
        foreach (var t in data.Todos) t.Refresh();
        return data;
    }

    public static List<AudioService.Mic> Microphones() => new()
    {
        new("demo-usb", T("Microphone (USB Audio)", "Microfone (USB Audio)", "Micrófono (USB Audio)"), true, true),
        new("demo-headset", "Headset (Bluetooth)", false, true),
        new("demo-webcam", T("Webcam microphone", "Microfone da webcam", "Micrófono de la webcam"), false, true),
    };

    // Música fictícia: nome inventado e uma "capa" desenhada aqui mesmo.
    public static (string Title, string Artist) Track => ("Aurora Drive", "Lumen Coast");

    public static BitmapSource AlbumArt()
    {
        const int size = 300;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var sky = new LinearGradientBrush(Color.FromRgb(0x2B, 0x14, 0x5E), Color.FromRgb(0xFF, 0x5E, 0x7E), 90);
            dc.DrawRectangle(sky, null, new Rect(0, 0, size, size));
            var sun = new LinearGradientBrush(Color.FromRgb(0xFF, 0xD1, 0x66), Color.FromRgb(0xFF, 0x7A, 0x59), 90);
            dc.DrawEllipse(sun, null, new Point(size / 2.0, size * 0.62), 82, 82);
            // faixas sobre o sol, estilo synthwave
            var band = new SolidColorBrush(Color.FromArgb(0xFF, 0xF2, 0x52, 0x7A));
            for (int i = 0; i < 5; i++)
                dc.DrawRectangle(band, null, new Rect(0, size * 0.6 + i * 13, size, 3 + i * 1.5));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1A, 0x0B, 0x3A)), null, new Rect(0, size * 0.78, size, size * 0.22));
            var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0x6B, 0xC1)), 1.5);
            for (int i = 0; i <= 12; i++)
                dc.DrawLine(grid, new Point(size / 2.0, size * 0.78), new Point(i * size / 12.0 * 2 - size / 2.0, size));
            for (int i = 1; i <= 4; i++)
                dc.DrawLine(grid, new Point(0, size * 0.78 + i * i * 4.5), new Point(size, size * 0.78 + i * i * 4.5));
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }
}
