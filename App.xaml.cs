using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Island;

public partial class App : Application
{
    Mutex? _mutex;
    Forms.NotifyIcon? _tray;
    MainWindow? _win;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Island.exe --screenshots <pasta>: imagens do README com dados fictícios (não toca nos seus dados).
        if (e.Args.Length >= 2 && e.Args[0] == "--screenshots")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = ScreenshotMaker.RunAsync(e.Args[1]);
            return;
        }

        _mutex = new Mutex(true, @"Local\Island.SingleInstance", out bool created);
        if (!created)
        {
            Shutdown();
            return;
        }

        // Erro inesperado: registra no log e segue funcionando em vez de fechar.
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Erro inesperado", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            if (ex.ExceptionObject is Exception e) Log.Error("Erro fatal", e);
        };
        Log.Info("Island iniciada");

        _win = new MainWindow();
        _win.Show();
        CreateTray();
    }

    void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();

        var settings = new Forms.ToolStripMenuItem("", null, (_, _) => _win?.OpenSettings());
        var setup = new Forms.ToolStripMenuItem("", null, (_, _) => _win?.OpenSetup());
        var resetPos = new Forms.ToolStripMenuItem("", null, (_, _) =>
        {
            if (_win == null) return;
            _win.Settings.PillX = _win.Settings.PillY = null;
            _win.ApplySettings();
            _win.SaveData();
        });

        var startup = new Forms.ToolStripMenuItem("") { Checked = Autostart.IsEnabled, CheckOnClick = true };
        startup.CheckedChanged += (_, _) => Autostart.Set(startup.Checked);

        var hide = new Forms.ToolStripMenuItem("") { CheckOnClick = true };
        hide.CheckedChanged += (_, _) => _win?.SetUserHidden(hide.Checked);

        var data = new Forms.ToolStripMenuItem("", null, (_, _) =>
        {
            System.IO.Directory.CreateDirectory(Storage.Folder);
            Process.Start("explorer.exe", Storage.Folder);
        });
        var quit = new Forms.ToolStripMenuItem("", null, (_, _) => Quit());

        menu.Items.AddRange(new Forms.ToolStripItem[]
        {
            settings, setup, resetPos, new Forms.ToolStripSeparator(), startup, hide, new Forms.ToolStripSeparator(), data, quit,
        });

        void UpdateTexts()
        {
            settings.Text = Loc.T("tray.settings");
            setup.Text = Loc.T("tray.setup");
            resetPos.Text = Loc.T("tray.resetPos");
            startup.Text = Loc.T("tray.autostart");
            hide.Text = Loc.T("tray.hide");
            data.Text = Loc.T("tray.data");
            quit.Text = Loc.T("tray.quit");
        }
        UpdateTexts();
        Loc.Changed += UpdateTexts;

        _tray = new Forms.NotifyIcon
        {
            Icon = MakeTrayIcon(),
            Text = "Island",
            Visible = true,
            ContextMenuStrip = menu,
        };
    }

    void Quit()
    {
        if (_win != null)
        {
            _win.AllowClose = true;
            _win.Cleanup();
            _win.Close();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _mutex?.Dispose();
        base.OnExit(e);
    }

    // Desenha o ícone da bandeja (uma pílula branca) em vez de carregar um arquivo .ico.
    // O mesmo ícone do programa (Assets/island.ico), no tamanho certo para a bandeja.
    static Drawing.Icon MakeTrayIcon()
    {
        using var stream = GetResourceStream(new Uri("pack://application:,,,/Assets/island.ico")).Stream;
        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }
}

static class Autostart
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Island";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue(Name) is string;
        }
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key, true) ?? Registry.CurrentUser.CreateSubKey(Key);
        if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue(Name, false);
    }
}
