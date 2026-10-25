using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Island;

// Pesquisa na internet sem chave de API: resultados do DuckDuckGo + manchetes do Google Notícias.
// Só o texto da pesquisa sai do PC (nunca o áudio).
public static class WebSearch
{
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd(Loc.Lang switch { "en" => "en-US,en;q=0.8", "es" => "es-ES,es;q=0.8", _ => "pt-BR,pt;q=0.9" });
        return http;
    }

    public sealed record Result(string Title, string Url, string Snippet, string Source = "");

    public static async Task<string> SearchAsync(string query, CancellationToken ct)
    {
        var web = SafeAsync(() => DuckDuckGoAsync(query, ct));
        var news = SafeAsync(() => NewsAsync(query, ct));
        await Task.WhenAll(web, news);

        var sb = new StringBuilder();
        sb.AppendLine($"Pesquisa: \"{query}\" ({DateTime.Now:dd/MM/yyyy HH:mm})");
        if (news.Result.Count > 0)
        {
            sb.AppendLine("\nNotícias recentes:");
            foreach (var r in news.Result.Take(6))
                sb.AppendLine($"- {r.Title} ({r.Source}{(r.Snippet.Length > 0 ? ", " + r.Snippet : "")})"); // o link do Google Notícias é enorme e só redireciona
        }
        if (web.Result.Count > 0)
        {
            sb.AppendLine("\nResultados da web:");
            foreach (var r in web.Result.Take(8))
                sb.AppendLine($"- {r.Title}: {r.Snippet} ({r.Url})");
        }
        // Já lê o primeiro resultado: modelos pequenos às vezes respondem só com os títulos.
        if (web.Result.FirstOrDefault() is { } top)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(6));
                var page = await FetchAsync(top.Url, cts.Token);
                sb.AppendLine("\nConteúdo do primeiro resultado:\n" + (page.Length > 2500 ? page[..2500] + "…" : page));
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { }
        }
        if (news.Result.Count == 0 && web.Result.Count == 0) sb.AppendLine("Nenhum resultado (sem internet?).");
        else sb.AppendLine("\n(Se os trechos não trazem os números/fatos pedidos, leia a página mais relevante com fetch_page ANTES de responder. Responda com os dados, citando o site; não fale de ferramentas nem mande o usuário procurar.)");
        return sb.ToString();
    }

    static async Task<List<Result>> SafeAsync(Func<Task<List<Result>>> f)
    {
        try { return await f(); }
        catch (Exception e) when (e is not OperationCanceledException) { Log.Error("Pesquisa na web", e); return new(); }
    }

    static async Task<List<Result>> DuckDuckGoAsync(string query, CancellationToken ct)
    {
        var region = Loc.Lang switch { "en" => "us-en", "es" => "es-es", _ => "br-pt" };
        var html = await Http.GetStringAsync($"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}&kl={region}", ct);
        var list = new List<Result>();
        foreach (Match m in Regex.Matches(html, "<a[^>]*class=\"result__a\"[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>.*?class=\"result__snippet\"[^>]*>(.*?)</a>", RegexOptions.Singleline))
        {
            var url = WebUtility.HtmlDecode(m.Groups[1].Value);
            // Os links vêm embrulhados: //duckduckgo.com/l/?uddg=<url real>
            var real = Regex.Match(url, "uddg=([^&]+)");
            if (real.Success) url = Uri.UnescapeDataString(real.Groups[1].Value);
            if (url.Contains("duckduckgo.com/y.js")) continue; // anúncio
            list.Add(new Result(Text(m.Groups[2].Value), url, Text(m.Groups[3].Value)));
        }
        return list;
    }

    static async Task<List<Result>> NewsAsync(string query, CancellationToken ct)
    {
        var (hl, gl, ceid) = Loc.Lang switch { "en" => ("en-US", "US", "US:en"), "es" => ("es-419", "MX", "MX:es-419"), _ => ("pt-BR", "BR", "BR:pt-419") };
        var xml = await Http.GetStringAsync($"https://news.google.com/rss/search?q={Uri.EscapeDataString(query)}&hl={hl}&gl={gl}&ceid={ceid}", ct);
        var doc = XDocument.Parse(xml);
        return doc.Descendants("item").Select(i =>
        {
            var date = DateTime.TryParse(i.Element("pubDate")?.Value, out var d) ? d.ToString("dd/MM HH:mm") : "";
            return new Result(i.Element("title")?.Value ?? "", i.Element("link")?.Value ?? "", date, i.Element("source")?.Value ?? "");
        }).Where(r => r.Title.Length > 0).ToList();
    }

    // Baixa uma página e devolve só o texto (sem scripts, menus e afins).
    public static async Task<string> FetchAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return "Só leio links http/https.";
        var html = await Http.GetStringAsync(uri, ct);
        var title = Text(Regex.Match(html, "<title[^>]*>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups[1].Value);
        html = Regex.Replace(html, @"<(script|style|noscript|svg|nav|footer|header|aside|form)\b.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<(br|p|div|li|h\d|tr)\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        var text = Text(html, keepLines: true);
        if (text.Length > 4000) text = text[..4000] + "…"; // cabe no contexto da IA local
        return $"{title}\n{uri}\n\n{text}";
    }

    static string Text(string html, bool keepLines = false)
    {
        var t = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "));
        if (!keepLines) return Regex.Replace(t, @"\s+", " ").Trim();
        var lines = t.Split('\n').Select(l => Regex.Replace(l, @"\s+", " ").Trim()).Where(l => l.Length > 2);
        return string.Join("\n", lines);
    }
}
