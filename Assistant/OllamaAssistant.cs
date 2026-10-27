using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Island;

public interface IBrain
{
    Task<string> AskAsync(string userText, Action<string> onNote, CancellationToken ct);
    void Reset();
}

// IA rodando no próprio PC pelo Ollama (grátis; nada sai do computador).
public sealed class OllamaAssistant : IBrain
{
    const int MaxTurns = 12;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    readonly IAssistantHost _host;
    readonly AgentTools _tools;
    readonly List<JsonObject> _history = new();
    DateTime _lastUse = DateTime.MinValue;

    public OllamaAssistant(IAssistantHost host, AgentTools tools)
    {
        _host = host;
        _tools = tools;
    }

    public void Reset() => _history.Clear();

    public async Task<string> AskAsync(string userText, Action<string> onNote, CancellationToken ct)
    {
        var s = _host.Settings;
        if (DateTime.Now - _lastUse > TimeSpan.FromMinutes(15)) _history.Clear();
        _lastUse = DateTime.Now;
        _history.Add(new JsonObject { ["role"] = "user", ["content"] = userText });

        try
        {
            var tools = JsonSerializer.SerializeToNode(_tools.OllamaDefinitions());
            for (int turn = 0; turn < MaxTurns; turn++)
            {
                ct.ThrowIfCancellationRequested();
                var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = SystemPrompt(s) } };
                foreach (var m in _history) messages.Add(m.DeepClone());

                var body = new JsonObject
                {
                    ["model"] = s.OllamaModel,
                    ["stream"] = false,
                    ["think"] = false,
                    ["keep_alive"] = "30m",
                    ["options"] = new JsonObject { ["temperature"] = 0.2, ["num_ctx"] = 8192 },
                    ["tools"] = tools?.DeepClone(),
                    ["messages"] = messages,
                };

                var json = await PostAsync(s, "/api/chat", body, ct);
                var msg = JsonNode.Parse(json)?["message"] as JsonObject
                          ?? throw new InvalidOperationException("Resposta inesperada da IA local.");

                var content = CleanText(msg["content"]?.GetValue<string>() ?? "");
                var calls = msg["tool_calls"] as JsonArray;

                var assistant = new JsonObject { ["role"] = "assistant", ["content"] = content };
                if (calls is { Count: > 0 }) assistant["tool_calls"] = calls.DeepClone();
                _history.Add(assistant);

                if (calls is not { Count: > 0 }) return content.Length > 0 ? content : "Pronto.";
                if (content.Length > 0) onNote(content);

                foreach (var call in calls)
                {
                    var fn = call?["function"];
                    var name = fn?["name"]?.GetValue<string>() ?? "";
                    var args = fn?["arguments"];
                    if (args is JsonValue v && v.TryGetValue<string>(out var raw)) args = JsonNode.Parse(raw);

                    var input = args is JsonObject o
                        ? o.ToDictionary(kv => kv.Key, kv => kv.Value is null ? default : JsonSerializer.SerializeToElement(kv.Value))
                        : new Dictionary<string, JsonElement>();

                    var output = await _tools.ExecuteAsync(name, input, ct);
                    _history.Add(new JsonObject { ["role"] = "tool", ["tool_name"] = name, ["content"] = output.Text });
                }
            }
            return "Parei aqui: foram passos demais para um pedido só.";
        }
        catch
        {
            _history.Clear();
            throw;
        }
    }

    static async Task<string> PostAsync(AppSettings s, string path, JsonNode body, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await Http.PostAsync(s.OllamaUrl.TrimEnd('/') + path,
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("A IA local (Ollama) não está rodando. Abra o Ollama ou instale em ollama.com.");
        }

        using (resp)
        {
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode) return json;

            string error = json;
            try { error = JsonNode.Parse(json)?["error"]?.GetValue<string>() ?? json; } catch { }
            if (resp.StatusCode == HttpStatusCode.NotFound || error.Contains("not found"))
                throw new InvalidOperationException($"O modelo \"{s.OllamaModel}\" não está baixado. Baixe em Configurações › Assistente.");
            if (error.Contains("does not support tools"))
                throw new InvalidOperationException($"O modelo \"{s.OllamaModel}\" não sabe usar ferramentas. Use, por exemplo, qwen3:8b.");
            throw new InvalidOperationException("IA local: " + error);
        }
    }

    // Alguns modelos ainda mandam o raciocínio entre <think>…</think>.
    static string CleanText(string text) => Regex.Replace(text, @"<think>[\s\S]*?</think>", "").Trim();

    static string SystemPrompt(AppSettings s)
    {
        var folders = s.ProjectFolderList();
        var prompt = $"""
            Você é o assistente da Island, um painel no Windows do usuário. Você age no computador dele usando as ferramentas.
            Hoje é {DateTime.Now.ToString("dddd, dd/MM/yyyy HH:mm", Loc.Culture)}.

            Regras:
            - Responda SEMPRE no idioma: {Loc.T("prompt.language")}. Uma ou duas frases curtas, sem markdown.
            - Para fazer algo no PC, chame a ferramenta adequada em vez de explicar como fazer.
            - Projetos: use find_projects para achar o caminho real; nunca invente caminhos.
            - Compilar/rodar: leia o package.json ou .csproj do projeto e use run_command na pasta dele. Servidores de desenvolvimento vão com visible=true.
            - Volume de UM app (Spotify, Discord, um jogo): set_app_volume. Volume geral do PC: set_volume.
            - Você TEM acesso à internet: para notícias, resultados de eleições/jogos, preços, clima, cotações ou qualquer fato atual, use web_search (e fetch_page para detalhes) e responda com os dados encontrados, citando a fonte. Nunca diga que não tem acesso a informações em tempo real.
            - Abas do navegador: browser_tab. Para "fecha tudo menos X e Y" use action=close_others com keep="X, Y" (uma chamada só). Use action=list para ver as abas. Responda com base na lista que a ferramenta devolve; nunca diga que fechou o que ela não fechou.
            - Mídia: "despausar/continuar/dar play" = media_control action=play; "pausar/parar" = action=pause (com app="spotify" etc. se o usuário disse). Nunca use play_pause quando ele disse qual quer.
            - Seja fiel ao resultado das ferramentas: só diga que enviou, fechou ou fez algo se a ferramenta confirmou. Se ela devolveu erro, conte o erro ao usuário com as palavras dela; nunca invente sucesso.
            - Memória: quando o usuário contar algo duradouro sobre ele (um site, uma conta, quem é quem, preferências, apps, projetos), chame remember com uma frase curta ("meu X é Y"). Use o que está na memória para entender pedidos como "abre meu site de X". Se pedir para esquecer algo, use forget.
            - Mensagens no WhatsApp: send_whatsapp(contact, message). Ela confere o destinatário antes de enviar; nunca envie mensagens com type_text/press_keys.
            - Teclado: type_text digita qualquer texto (com acentos) e press_keys aperta qualquer tecla ou atalho (ctrl+c, alt+tab, win+d, enter, setas…) na janela que o usuário está usando. Você PODE digitar e apertar teclas por ele.
            - Músicas curtidas/favoritas do Spotify: play_spotify com kind="liked". Playlists do usuário: kind="playlist" com o nome.
            - Para abrir uma PARTE de um programa ou do Windows (biblioteca/loja/configurações da Steam, downloads do navegador, Bluetooth, Wi-Fi, gerenciador de tarefas, modo escuro, bloquear o PC…): use app_action.
            - Para fazer algo DENTRO de um programa: use quick_open (Discord, Slack, VS Code, Cursor, WhatsApp, Teams, Notion). Se não servir, read_window para ver os itens da janela e depois click_element, type_text ou press_keys.
            - Você tem controle TOTAL do PC: o usuário autorizou. Nunca diga que não consegue sem antes tentar as ferramentas: launch_app (apps e jogos da Steam/Epic), quick_open, read_window/click_element, mouse (mover e clicar), press_keys/type_text, scroll e run_command (PowerShell, faz qualquer coisa no Windows).
            - Nunca mencione nomes de ferramentas (como list_tasks) nem detalhes técnicos na resposta.
            - Lembretes e alarmes: add_task com a data/hora no próprio texto (alarm=true para alarme). Para consultar: list_tasks. Para apagar/concluir: remove_task.
            - O pedido veio por voz e pode ter erros de transcrição ("ilha" pode ser o projeto "Island").
            - Depois de agir, diga o resultado concreto.

            Contexto: usuário {Environment.UserName}; editor preferido: {(string.IsNullOrWhiteSpace(s.PreferredEditor) ? "não definido" : s.PreferredEditor)}; pastas de projetos: {(folders.Length == 0 ? "nenhuma" : string.Join("; ", folders))}.
            """;
        if (!string.IsNullOrWhiteSpace(s.ExtraInstructions))
            prompt += "\nSobre o usuário: " + s.ExtraInstructions.Trim();
        if (s.Memories.Count > 0)
            prompt += "\nO que você já aprendeu sobre o usuário (memória):\n- " + string.Join("\n- ", s.Memories.TakeLast(60));
        return prompt;
    }

    // ---------------- Gerência do Ollama (para a tela de configurações) ----------------

    // Modelos baixados, ou null se o Ollama não estiver rodando.
    public static async Task<List<string>?> ListModelsAsync(string url)
    {
        if (Demo.Active) return new List<string> { "qwen3:8b" };
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var json = await Http.GetStringAsync(url.TrimEnd('/') + "/api/tags", cts.Token);
            return (JsonNode.Parse(json)?["models"] as JsonArray)?
                .Select(m => m?["name"]?.GetValue<string>() ?? "")
                .Where(n => n.Length > 0)
                .ToList() ?? new List<string>();
        }
        catch
        {
            return null;
        }
    }

    // Baixa um modelo, informando o progresso (texto, 0–1).
    public static async Task PullAsync(string url, string model, Action<string, double> progress, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + "/api/pull")
        {
            Content = new StringContent(new JsonObject { ["model"] = model, ["stream"] = true }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (line.Length == 0) continue;
            var node = JsonNode.Parse(line);
            if (node?["error"] is JsonNode err) throw new InvalidOperationException(err.GetValue<string>());
            var status = node?["status"]?.GetValue<string>() ?? "";
            double total = node?["total"]?.GetValue<double>() ?? 0, done = node?["completed"]?.GetValue<double>() ?? 0;
            progress(status, total > 0 ? done / total : -1);
        }
    }

    // Carrega o modelo na placa de vídeo antes de precisar (a primeira carga é a parte lenta).
    public static async Task WarmUpAsync(AppSettings s)
    {
        try
        {
            var body = new JsonObject { ["model"] = s.OllamaModel, ["keep_alive"] = "30m" };
            using var resp = await Http.PostAsync(s.OllamaUrl.TrimEnd('/') + "/api/generate",
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        }
        catch { }
    }

    // Matching "qwen3:8b" com "qwen3:8b" ou "qwen3" com "qwen3:latest".
    public static bool HasModel(IEnumerable<string> installed, string model) =>
        installed.Any(m => m.Equals(model, StringComparison.OrdinalIgnoreCase)
                           || (!model.Contains(':') && m.Equals(model + ":latest", StringComparison.OrdinalIgnoreCase)));
}
