using System;
using System.Threading.Tasks;
using System.Windows;

namespace Island;

// Cenas do modo de screenshots (Island.exe --screenshots <pasta>). Só roda com Demo.Active.
public partial class MainWindow
{
    internal FrameworkElement DemoRoot => (FrameworkElement)Content;
    internal FrameworkElement DemoPill => PillHost;

    internal async Task RunDemoScenesAsync(Func<string, Task> capture)
    {
        if (!Demo.Active) return;
        var art = Demo.AlbumArt();
        var (title, artist) = Demo.Track;
        _media.ShowDemo(title, artist, "Spotify", art, TimeSpan.FromSeconds(214), TimeSpan.FromSeconds(83));

        SetMode(Mode.Compact);
        await capture("compact");

        S.Shape = "ball";
        ApplySettings();
        SetMode(Mode.Compact);
        await capture("ball");
        S.Shape = "pill";
        ApplySettings();

        ShowToast("", title, artist, null, art);
        _toastTimer.Stop();
        await capture("toast-track");

        ShowToast("",Loc.T("t.volume"), null, 0.64);
        _toastTimer.Stop();
        await capture("toast-volume");

        ShowToast(GlyphMic, Loc.T("t.listening"), Loc.T("t.listeningSub"));
        _toastTimer.Stop();
        await capture("toast-listening");

        SetMode(Mode.Expanded);
        SelectTab(0);
        await capture("media");

        _chat.Clear();
        AddChat("user", Demo.T("Open my Steam library", "Abre a biblioteca da Steam", "Abre mi biblioteca de Steam"));
        AddChat("step", "Steam › " + Demo.T("Library", "Biblioteca", "Biblioteca"), "");
        AddChat("assistant", Demo.T("Opening your Steam library.", "Abrindo a sua biblioteca da Steam.", "Abriendo tu biblioteca de Steam."));
        AddChat("user", Demo.T("Remind me about the team meeting tomorrow at 6 pm", "Me lembra da reunião com o time amanhã às 18h", "Recuérdame la reunión con el equipo mañana a las 18h"));
        AddChat("assistant", Demo.T("Got it — I'll remind you tomorrow at 6:00 PM.", "Combinado: vou te lembrar amanhã às 18:00.", "Listo: te lo recuerdo mañana a las 18:00."));
        SelectTab(TabIndexAssistant);
        await capture("assistant");

        SelectTab(2);
        await capture("tasks");

        _timerLeft = TimeSpan.FromSeconds(18 * 60 + 42);
        _timerEnd = DateTime.Now + _timerLeft;
        _timerRunning = true;
        _cycles = 2;
        UpdateTimerUi();
        SelectTab(1);
        await capture("timer");
        _timerRunning = false;
        UpdateTimerUi();

        SetMode(Mode.Compact);
    }
}
