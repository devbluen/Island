using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Island;

// Liga a ilha à SUA conta do Spotify pela API oficial (login no navegador, sem senha passando pela ilha).
// Permite buscar e tocar música, artista, álbum ou playlist no seu Spotify.
public sealed class SpotifyService
{
    public const string RedirectUri = "http://127.0.0.1:8977/callback";
    // Só leitura da sua biblioteca e playlists (para "toca minhas curtidas" / "toca minha playlist X") + controle do player.
    const string Scopes = "user-modify-playback-state user-read-playback-state user-library-read playlist-read-private playlist-read-collaborative";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    readonly Func<AppSettings> _settings;
    string? _access;
    DateTime _accessExpires;

    // Chamado quando o Spotify entrega uma chave nova (ele troca a cada renovação): a ilha salva no disco.
    public Action? TokensChanged { get; set; }

    public SpotifyService(Func<AppSettings> settings) => _settings = settings;

    AppSettings S => _settings();

    public bool IsConnected => !string.IsNullOrEmpty(Secret.Unprotect(S.SpotifyRefreshProtected));

    // ---------------- Conectar (OAuth com PKCE: só precisa do Client ID) ----------------

    public async Task ConnectAsync(CancellationToken ct)
    {
        var clientId = S.SpotifyClientId.Trim();
        if (clientId.Length == 0) throw new InvalidOperationException("Cole o Client ID do seu app do Spotify primeiro.");

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(12));

        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:8977/");
        listener.Start();

        var url = "https://accounts.spotify.com/authorize?response_type=code"
                  + "&client_id=" + Uri.EscapeDataString(clientId)
                  + "&scope=" + Uri.EscapeDataString(Scopes)
                  + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
                  + "&code_challenge_method=S256&code_challenge=" + challenge
                  + "&state=" + state;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        var contextTask = listener.GetContextAsync();
        var done = await Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes(3), ct));
        if (done != contextTask) throw new TimeoutException("O login no Spotify não foi concluído a tempo.");

        var ctx = await contextTask;
        var q = System.Web.HttpUtility.ParseQueryString(ctx.Request.Url?.Query ?? "");
        bool ok = q["state"] == state && q["code"] != null;
        var page = ok
            ? "<h2 style='font-family:sans-serif'>Pronto! A Island foi conectada ao Spotify. Pode fechar esta aba.</h2>"
            : $"<h2 style='font-family:sans-serif'>Não deu certo: {WebUtility.HtmlEncode(q["error"] ?? "resposta inválida")}</h2>";
        var bytes = Encoding.UTF8.GetBytes(page);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.OutputStream.WriteAsync(bytes, ct);
        ctx.Response.Close();
        if (!ok) throw new InvalidOperationException("O Spotify recusou: " + (q["error"] ?? "resposta inválida"));

        var json = await PostTokenAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = q["code"]!,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
        }, ct);
        SaveTokens(json);
    }

    public void Disconnect()
    {
        S.SpotifyRefreshProtected = "";
        _access = null;
    }

    static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static async Task<JsonDocument> PostTokenAsync(System.Collections.Generic.Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await Http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("Spotify: " + body);
        return JsonDocument.Parse(body);
    }

    void SaveTokens(JsonDocument json)
    {
        var root = json.RootElement;
        _access = root.GetProperty("access_token").GetString();
        _accessExpires = DateTime.Now.AddSeconds(root.GetProperty("expires_in").GetInt32() - 60);
        if (root.TryGetProperty("refresh_token", out var r) && r.GetString() is { Length: > 0 } refresh)
        {
            S.SpotifyRefreshProtected = Secret.Protect(refresh);
            TokensChanged?.Invoke();
        }
    }

    async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_access != null && DateTime.Now < _accessExpires) return _access;
        var refresh = Secret.Unprotect(S.SpotifyRefreshProtected);
        if (string.IsNullOrEmpty(refresh)) throw new InvalidOperationException("Spotify não conectado.");
        using var json = await PostTokenAsync(new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
            ["client_id"] = S.SpotifyClientId.Trim(),
        }, ct);
        SaveTokens(json);
        return _access!;
    }

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? jsonBody, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, "https://api.spotify.com/v1" + url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        if (jsonBody != null) req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return await Http.SendAsync(req, ct);
    }

    // ---------------- Buscar e tocar ----------------

    public sealed record Found(string Uri, string Name, string Detail, string Kind);

    public async Task<Found?> SearchAsync(string query, string kind, CancellationToken ct)
    {
        kind = kind is "artist" or "album" or "playlist" ? kind : "track";
        using var resp = await SendAsync(HttpMethod.Get,
            $"/search?type={kind}&limit=5&q={Uri.EscapeDataString(query)}", null, ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("Busca no Spotify falhou: " + await resp.Content.ReadAsStringAsync(ct));
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var items = doc.RootElement.GetProperty(kind + "s").GetProperty("items");
        foreach (var it in items.EnumerateArray())
        {
            if (it.ValueKind != JsonValueKind.Object) continue; // a API às vezes devolve null no meio
            var name = it.GetProperty("name").GetString() ?? "";
            var detail = kind switch
            {
                "track" or "album" => string.Join(", ", it.GetProperty("artists").EnumerateArray().Select(a => a.GetProperty("name").GetString())),
                "playlist" => it.TryGetProperty("owner", out var o) && o.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? "" : "",
                _ => "",
            };
            return new Found(it.GetProperty("uri").GetString()!, name, detail, kind);
        }
        return null;
    }

    // Toca no seu Spotify. Devolve uma frase curta para a ilha mostrar.
    //
    // O app do Spotify só obedece a API depois de já ter tocado algo nesta sessão; "recém-aberto" ele aceita a ordem
    // e ignora. Por isso: música → abre o link dela (o app sempre toca); álbum/playlist/artista → se o app estiver
    // "frio", toca a 1ª faixa pelo link para acordá-lo e só então pede o resto pela API, conferindo se tocou.
    public async Task<string> PlayAsync(string query, string kind, CancellationToken ct)
    {
        if (kind == "liked") return await PlayLikedAsync(ct);
        if (string.IsNullOrWhiteSpace(query)) return await ResumeAsync(ct);

        // Playlist: primeiro procura nas SUAS playlists (a busca pública não acha as privadas).
        var found = (kind == "playlist" ? await MyPlaylistAsync(query, ct) : null) ?? await SearchAsync(query, kind, ct);
        if (found == null) return Loc.F("sp.notFound", query);

        var reply = found.Kind switch
        {
            "track" => Loc.F("sp.track", found.Name, found.Detail),
            "artist" => Loc.F("sp.artist", found.Name),
            "album" => Loc.F("sp.album", found.Name, found.Detail),
            _ => Loc.F("sp.playlist", found.Name),
        };

        var device = await EnsureDeviceAsync(ct);
        var url = "/me/player/play" + (device != null ? "?device_id=" + Uri.EscapeDataString(device) : "");

        if (found.Kind == "track")
        {
            // App "frio": o link toca. App já tocando algo: só a API troca a música.
            if (!(await StateAsync(ct)).HasItem)
            {
                OpenUri(found.Uri);
                return reply;
            }
            using (await SendAsync(HttpMethod.Put, url, JsonSerializer.Serialize(new { uris = new[] { found.Uri } }), ct)) { }
            await Task.Delay(1200, ct);
            if (!(await StateAsync(ct)).Playing) OpenUri(found.Uri);
            return reply;
        }

        var first = await FirstTrackAsync(found, ct);
        var (_, hasItem) = await StateAsync(ct);
        if (!hasItem && first != null)
        {
            OpenUri(first);
            await Task.Delay(2500, ct);
        }

        using (var resp = await SendAsync(HttpMethod.Put, url, JsonSerializer.Serialize(new { context_uri = found.Uri }), ct))
        {
            if (!resp.IsSuccessStatusCode && first == null)
                return Loc.F("sp.rejected", await resp.Content.ReadAsStringAsync(ct));
        }

        await Task.Delay(1500, ct);
        var (playing, _) = await StateAsync(ct);
        if (!playing && first != null) OpenUri(first); // plano B: pelo menos a 1ª faixa toca
        return reply;
    }

    // "Toca minhas músicas curtidas": a lista "Músicas Curtidas" da sua conta, em ordem aleatória.
    public async Task<string> PlayLikedAsync(CancellationToken ct)
    {
        string? userId = null;
        using (var me = await SendAsync(HttpMethod.Get, "/me", null, ct))
            if (me.IsSuccessStatusCode)
                using (var doc = JsonDocument.Parse(await me.Content.ReadAsStringAsync(ct)))
                    userId = doc.RootElement.GetProperty("id").GetString();
        if (userId == null) return Loc.F("sp.rejected", "/me");

        var device = await EnsureDeviceAsync(ct);
        var query = device != null ? "?device_id=" + Uri.EscapeDataString(device) : "";

        // App "frio" ignora a API: toca uma curtida pelo link (ele sempre obedece) e depois troca para a lista inteira.
        if (!(await StateAsync(ct)).HasItem)
        {
            var first = await FirstLikedAsync(ct);
            if (first != null) OpenUri(first);
            else WindowTools.PressKeys("playpause"); // sem permissão de ler a biblioteca: retoma o que tocou por último
            await Task.Delay(2500, ct);
            device ??= await DeviceAsync(ct);
            query = device != null ? "?device_id=" + Uri.EscapeDataString(device) : "";
        }

        using (await SendAsync(HttpMethod.Put, "/me/player/shuffle?state=true" + (query.Length > 0 ? "&" + query[1..] : ""), null, ct)) { }
        using (var resp = await SendAsync(HttpMethod.Put, "/me/player/play" + query,
                   JsonSerializer.Serialize(new { context_uri = $"spotify:user:{userId}:collection" }), ct))
        {
            if (!resp.IsSuccessStatusCode) return Loc.F("sp.rejected", await resp.Content.ReadAsStringAsync(ct));
        }
        await Task.Delay(1200, ct);
        if (!(await StateAsync(ct)).Playing)
        {
            OpenUri("spotify:collection:tracks"); // pelo menos mostra a lista
            return Loc.T("sp.likedOpened");
        }
        return Loc.T("sp.liked");
    }

    async Task<string?> FirstLikedAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Get, "/me/tracks?limit=20", null, ct);
            if (!resp.IsSuccessStatusCode) return null; // conectado antes desta versão: falta a permissão (reconectar resolve)
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var items = doc.RootElement.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("track").GetProperty("uri").GetString()).Where(u => u != null).ToList();
            return items.Count == 0 ? null : items[Random.Shared.Next(items.Count)];
        }
        catch
        {
            return null;
        }
    }

    // Procura pelo nome entre as playlists que você criou ou segue.
    async Task<Found?> MyPlaylistAsync(string query, CancellationToken ct)
    {
        try
        {
            var q = Simplify(query);
            Found? best = null;
            int bestScore = 0;
            for (int offset = 0; offset < 200; offset += 50)
            {
                using var resp = await SendAsync(HttpMethod.Get, $"/me/playlists?limit=50&offset={offset}", null, ct);
                if (!resp.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var items = doc.RootElement.GetProperty("items").EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).ToList();
                foreach (var it in items)
                {
                    var name = it.GetProperty("name").GetString() ?? "";
                    var n = Simplify(name);
                    int score = n == q ? 3 : n.StartsWith(q) || q.StartsWith(n) && n.Length >= 3 ? 2 : n.Contains(q) && q.Length >= 3 ? 1 : 0;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        var owner = it.TryGetProperty("owner", out var o) && o.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? "" : "";
                        best = new Found(it.GetProperty("uri").GetString()!, name, owner, "playlist");
                    }
                }
                if (bestScore == 3 || items.Count < 50) break;
            }
            return best;
        }
        catch
        {
            return null;
        }
    }

    static string Simplify(string s) => new(s.ToLowerInvariant().Normalize(NormalizationForm.FormD)
        .Where(char.IsLetterOrDigit).ToArray());

    // "Toca uma música" sem dizer qual: continua o que estava tocando.
    public async Task<string> ResumeAsync(CancellationToken ct)
    {
        var device = await EnsureDeviceAsync(ct);
        var (playing, hasItem) = await StateAsync(ct);
        if (playing) return Loc.T("r.ok");
        if (hasItem)
        {
            var url = "/me/player/play" + (device != null ? "?device_id=" + Uri.EscapeDataString(device) : "");
            using var resp = await SendAsync(HttpMethod.Put, url, null, ct);
            await Task.Delay(1000, ct);
            if ((await StateAsync(ct)).Playing) return Loc.T("sp.resumed");
        }
        // App "frio": a tecla de mídia faz o Spotify retomar a última música.
        WindowTools.PressKeys("playpause");
        return Loc.T("sp.resumed");
    }

    // Pausa pela conta (quando o app não aparece como mídia no Windows, ex.: tocando em outro aparelho).
    public async Task<bool> PauseAsync(CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Put, "/me/player/pause", null, ct);
        await Task.Delay(500, ct);
        return !(await StateAsync(ct)).Playing;
    }

    static void OpenUri(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });

    async Task<string?> EnsureDeviceAsync(CancellationToken ct)
    {
        var device = await DeviceAsync(ct);
        if (device != null) return device;
        // Spotify fechado: abre o app e espera ele aparecer como dispositivo.
        OpenUri("spotify:");
        for (int i = 0; i < 12 && device == null; i++)
        {
            await Task.Delay(1000, ct);
            device = await DeviceAsync(ct);
        }
        return device;
    }

    async Task<(bool Playing, bool HasItem)> StateAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Get, "/me/player", null, ct);
            if (!resp.IsSuccessStatusCode) return (false, false);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (body.Length == 0) return (false, false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            bool playing = root.TryGetProperty("is_playing", out var p) && p.ValueKind == JsonValueKind.True;
            bool item = root.TryGetProperty("item", out var i) && i.ValueKind == JsonValueKind.Object;
            return (playing, item);
        }
        catch
        {
            return (false, false);
        }
    }

    // 1ª faixa de um álbum, playlist ou artista (para "acordar" o app).
    async Task<string?> FirstTrackAsync(Found f, CancellationToken ct)
    {
        var id = f.Uri.Split(':').Last();
        var market = Loc.Lang switch { "en" => "US", "es" => "ES", _ => "BR" };
        var url = f.Kind switch
        {
            "album" => $"/albums/{id}/tracks?limit=1",
            "playlist" => $"/playlists/{id}/tracks?limit=1",
            _ => $"/artists/{id}/top-tracks?market={market}",
        };
        try
        {
            using var resp = await SendAsync(HttpMethod.Get, url, null, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            JsonElement first = f.Kind switch
            {
                "album" => root.GetProperty("items").EnumerateArray().FirstOrDefault(),
                "playlist" => root.GetProperty("items").EnumerateArray().Select(x => x.TryGetProperty("track", out var t) ? t : default).FirstOrDefault(),
                _ => root.GetProperty("tracks").EnumerateArray().FirstOrDefault(),
            };
            return first.ValueKind == JsonValueKind.Object && first.TryGetProperty("uri", out var u) ? u.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    // Prefere o dispositivo ativo; senão, este computador.
    async Task<string?> DeviceAsync(CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Get, "/me/player/devices", null, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var devices = doc.RootElement.GetProperty("devices").EnumerateArray().ToList();
        var pick = devices.FirstOrDefault(d => d.GetProperty("is_active").GetBoolean());
        if (pick.ValueKind == JsonValueKind.Undefined)
            pick = devices.FirstOrDefault(d => d.GetProperty("type").GetString() == "Computer");
        if (pick.ValueKind == JsonValueKind.Undefined && devices.Count > 0) pick = devices[0];
        return pick.ValueKind == JsonValueKind.Undefined ? null : pick.GetProperty("id").GetString();
    }
}
