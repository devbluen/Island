using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Island;

// Island.exe --screenshots <pasta>: gera as imagens do README com dados fictícios.
// A ilha se desenha direto num PNG (sem print da tela), então nada do seu desktop aparece.
public static class ScreenshotMaker
{
    const double Dpi = 2; // imagens nítidas (2x)

    public static async Task RunAsync(string outDir)
    {
        Demo.Active = true;
        try
        {
            foreach (var lang in new[] { "en", "pt" })
            {
                Demo.Lang = lang;
                var dir = Path.Combine(outDir, lang);
                Directory.CreateDirectory(dir);

                var win = new MainWindow();
                win.Show();
                await Task.Delay(900);

                BitmapSource? compact = null, toast = null, assistant = null;
                await win.RunDemoScenesAsync(async name =>
                {
                    await Task.Delay(1100); // espera as animações terminarem
                    var shot = CaptureIsland(win);
                    Save(OnWallpaper(shot, Math.Max(1100, shot.PixelWidth + 360), shot.PixelHeight + 140), Path.Combine(dir, name + ".png"));
                    if (name == "compact") compact = shot;
                    if (name == "toast-track") toast = shot;
                    if (name == "assistant") assistant = shot;
                });
                if (compact != null && toast != null && assistant != null)
                    Save(Hero(compact, toast, assistant), Path.Combine(dir, "hero.png"));

                foreach (var (page, name) in new[] { (0, "settings-appearance"), (3, "settings-assistant") })
                {
                    win.OpenSettings(page);
                    await Task.Delay(1200);
                    if (FindSettings() is { } sw)
                    {
                        Save(WindowCard(sw), Path.Combine(dir, name + ".png"));
                        sw.Close();
                    }
                }

                win.AllowClose = true;
                win.Cleanup();
                win.Close();
            }
        }
        catch (Exception e)
        {
            Log.Error("Screenshots", e);
        }
        Application.Current.Shutdown();
    }

    static SettingsWindow? FindSettings()
    {
        foreach (Window w in Application.Current.Windows)
            if (w is SettingsWindow s) return s;
        return null;
    }

    // Recorta só a ilha (com a sombra) da janela transparente.
    static BitmapSource CaptureIsland(MainWindow win)
    {
        var root = win.DemoRoot;
        var full = Render(root, root.ActualWidth, root.ActualHeight);
        var pill = win.DemoPill;
        var bounds = pill.TransformToAncestor(root).TransformBounds(new Rect(pill.RenderSize));
        bounds.Inflate(28, 28);
        bounds.Intersect(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        var crop = new Int32Rect((int)(bounds.X * Dpi), (int)(bounds.Y * Dpi), (int)(bounds.Width * Dpi), (int)(bounds.Height * Dpi));
        var cropped = new CroppedBitmap(full, crop);
        cropped.Freeze();
        return cropped;
    }

    static RenderTargetBitmap Render(Visual visual, double width, double height)
    {
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(width * Dpi), (int)Math.Ceiling(height * Dpi), 96 * Dpi, 96 * Dpi, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    // Fundo neutro: gradiente escuro com duas manchas de luz, como um papel de parede.
    static Grid Wallpaper(double w, double h)
    {
        var grid = new Grid { Width = w, Height = h, ClipToBounds = true };
        grid.Background = new LinearGradientBrush(Color.FromRgb(0x10, 0x14, 0x2B), Color.FromRgb(0x2A, 0x16, 0x45), 35);
        grid.Children.Add(new Border
        {
            Background = new RadialGradientBrush(Color.FromArgb(0x70, 0x0A, 0x84, 0xFF), Colors.Transparent),
            Width = w * 0.9, Height = h * 1.3, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(-w * 0.3, 0, 0, -h * 0.5),
        });
        grid.Children.Add(new Border
        {
            Background = new RadialGradientBrush(Color.FromArgb(0x60, 0xFF, 0x5E, 0x7E), Colors.Transparent),
            Width = w * 0.8, Height = h * 1.2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -h * 0.5, -w * 0.25, 0),
        });
        return grid;
    }

    static Image Img(BitmapSource src) => new()
    {
        Source = src,
        Width = src.PixelWidth / Dpi,
        Height = src.PixelHeight / Dpi,
        Stretch = Stretch.Fill,
    };

    // Pixels de saída = tamanho lógico × Dpi; o canvas recebe medidas lógicas.
    static BitmapSource OnWallpaper(BitmapSource island, double widthPx, double heightPx)
    {
        double w = widthPx / Dpi, h = heightPx / Dpi;
        var grid = Wallpaper(w, h);
        var img = Img(island);
        img.HorizontalAlignment = HorizontalAlignment.Center;
        img.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(img);
        return Layout(grid);
    }

    static BitmapSource Hero(BitmapSource compact, BitmapSource toast, BitmapSource assistant)
    {
        double w = 2200 / Dpi, h = 1100 / Dpi;
        var grid = Wallpaper(w, h);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
        left.Children.Add(Img(compact));
        var t = Img(toast);
        t.Margin = new Thickness(0, 30, 0, 0);
        left.Children.Add(t);
        row.Children.Add(left);
        row.Children.Add(Img(assistant));
        grid.Children.Add(row);
        return Layout(grid);
    }

    // A janela de configurações como um "cartão" com cantos arredondados e sombra.
    static BitmapSource WindowCard(Window window)
    {
        var content = (FrameworkElement)window.Content;
        double cw = content.ActualWidth, ch = content.ActualHeight;
        var shot = Render(content, cw, ch);

        double w = cw + 160, h = ch + 160;
        var grid = Wallpaper(w, h);
        var card = new Border
        {
            Width = cw, Height = ch, CornerRadius = new CornerRadius(12),
            Background = window.Background,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 10, Opacity = 0.55, Color = Colors.Black },
        };
        var inner = new Border { CornerRadius = new CornerRadius(12), ClipToBounds = true };
        var img = Img(shot);
        inner.Child = img;
        inner.Clip = new RectangleGeometry(new Rect(0, 0, cw, ch), 12, 12);
        card.Child = inner;
        grid.Children.Add(card);
        return Layout(grid);
    }

    static BitmapSource Layout(FrameworkElement el)
    {
        el.Measure(new Size(el.Width, el.Height));
        el.Arrange(new Rect(0, 0, el.Width, el.Height));
        el.UpdateLayout();
        return Render(el, el.Width, el.Height);
    }

    static void Save(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
