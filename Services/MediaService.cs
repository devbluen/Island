using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Island;

// Usa a API oficial do Windows de "mídia em reprodução" (a mesma do painel de volume do Windows 11).
// Funciona com Spotify, YouTube no navegador, Media Player, etc. Tudo roda no thread da UI.
public sealed class MediaService
{
    readonly Dispatcher _ui;
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    byte[]? _artBytes;
    TimeSpan _position, _start;
    DateTimeOffset _positionAt;

    public event Action? Changed;

    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string AppName { get; private set; } = "";
    public bool IsPlaying { get; private set; }
    public BitmapSource? Art { get; private set; }
    public int ArtVersion { get; private set; }
    public TimeSpan Duration { get; private set; }

    public bool HasMedia => (_session != null || _demo) && !string.IsNullOrWhiteSpace(Title);

    bool _demo;

    // Modo screenshots: uma música fictícia no lugar da sua.
    public void ShowDemo(string title, string artist, string app, BitmapSource art, TimeSpan duration, TimeSpan position)
    {
        _demo = true;
        Title = title;
        Artist = artist;
        AppName = app;
        Art = art;
        ArtVersion++;
        Duration = duration;
        _position = position;
        _positionAt = DateTimeOffset.Now;
        IsPlaying = true;
        Changed?.Invoke();
    }

    public TimeSpan EstimatedPosition
    {
        get
        {
            var p = _position;
            if (IsPlaying) p += DateTimeOffset.Now - _positionAt;
            if (p > Duration) p = Duration;
            return p < TimeSpan.Zero ? TimeSpan.Zero : p;
        }
    }

    public MediaService(Dispatcher ui) => _ui = ui;

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += (_, _) => _ui.InvokeAsync(() => Attach(_manager.GetCurrentSession()));
        Attach(_manager.GetCurrentSession());
    }

    void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }

        _session = session;
        _artBytes = null;

        if (session == null)
        {
            Title = Artist = AppName = "";
            IsPlaying = false;
            Duration = TimeSpan.Zero;
            Art = null;
            ArtVersion++;
            Changed?.Invoke();
            return;
        }

        session.MediaPropertiesChanged += OnPropertiesChanged;
        session.PlaybackInfoChanged += OnPlaybackChanged;
        session.TimelinePropertiesChanged += OnTimelineChanged;

        AppName = FriendlyAppName(session.SourceAppUserModelId);
        ReadPlayback();
        _ = ReadPropertiesAsync();
    }

    void OnPropertiesChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) =>
        _ui.InvokeAsync(ReadPropertiesAsync);

    void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) =>
        _ui.InvokeAsync(ReadPlayback);

    void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) =>
        _ui.InvokeAsync(() => { ReadTimeline(); Changed?.Invoke(); });

    async Task ReadPropertiesAsync()
    {
        var s = _session;
        if (s == null) return;
        try
        {
            var p = await s.TryGetMediaPropertiesAsync();
            if (s != _session || p == null) return;

            Title = p.Title ?? "";
            Artist = string.IsNullOrEmpty(p.Artist) ? p.AlbumArtist ?? "" : p.Artist;

            // O Spotify troca o título antes e a capa depois (em outro aviso, com o mesmo título),
            // então a capa é sempre relida e só é trocada se a imagem mudou de verdade.
            var bytes = p.Thumbnail != null ? await ReadBytesAsync(p.Thumbnail) : null;
            if (s != _session) return;
            if (!SameBytes(bytes, _artBytes))
            {
                _artBytes = bytes;
                Art = bytes != null ? DecodeArt(bytes) : null;
                ArtVersion++;
            }
        }
        catch { }
        if (s == _session) Changed?.Invoke();
    }

    void ReadPlayback()
    {
        if (_session == null) return;
        try
        {
            var playing = _session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            _position = EstimatedPosition;
            _positionAt = DateTimeOffset.Now;
            IsPlaying = playing;
            ReadTimeline();
        }
        catch { }
        Changed?.Invoke();
    }

    void ReadTimeline()
    {
        if (_session == null) return;
        try
        {
            var t = _session.GetTimelineProperties();
            _start = t.StartTime;
            Duration = t.EndTime - t.StartTime;
            _position = t.Position - t.StartTime;
            _positionAt = t.LastUpdatedTime;
            var now = DateTimeOffset.Now;
            if (_positionAt > now || _positionAt.Year < 2000) _positionAt = now;
        }
        catch { }
    }

    static async Task<byte[]?> ReadBytesAsync(IRandomAccessStreamReference reference)
    {
        try
        {
            using var ras = await reference.OpenReadAsync();
            using var stream = ras.AsStreamForRead();
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return ms.Length > 0 ? ms.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }

    static bool SameBytes(byte[]? a, byte[]? b) =>
        a == b || (a != null && b != null && a.AsSpan().SequenceEqual(b));

    static BitmapSource? DecodeArt(byte[] bytes)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 256;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    static string FriendlyAppName(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        var s = id;
        int bang = s.LastIndexOf('!');
        if (bang >= 0) s = s[(bang + 1)..];
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        int dot = s.LastIndexOf('.');
        if (dot >= 0 && dot < s.Length - 1) s = s[(dot + 1)..];

        return s.ToLowerInvariant() switch
        {
            "msedge" => "Edge",
            "chrome" => "Chrome",
            "firefox" => "Firefox",
            "zunemusic" => "Media Player",
            "spotify" => "Spotify",
            _ => s.Length > 0 ? char.ToUpper(s[0]) + s[1..] : s,
        };
    }

    public async void PlayPause()
    {
        try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { }
    }

    public async void Next()
    {
        try { if (_session != null) await _session.TrySkipNextAsync(); } catch { }
    }

    public async void Previous()
    {
        try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { }
    }

    public async void Seek(TimeSpan position)
    {
        if (_session == null) return;
        _position = position;
        _positionAt = DateTimeOffset.Now;
        try { await _session.TryChangePlaybackPositionAsync((_start + position).Ticks); } catch { }
    }
}
