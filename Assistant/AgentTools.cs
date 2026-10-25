using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic.Models.Beta.Messages;
using Microsoft.Win32;

namespace Island;

public interface IAssistantHost
{
    AppSettings Settings { get; }
    MediaService Media { get; }
    AudioService Audio { get; }
    SpotifyService Spotify { get; }
    IntPtr OwnWindow { get; }
    IntPtr LastExternalWindow { get; }
    ChatEntry Step(string glyph, string text);
    Task<bool> ConfirmAsync(string title, string detail);
    void StartFocusTimer(int? minutes);
    TodoItem? AddTask(string text, bool alarm = false);
    IReadOnlyList<TodoItem> PendingTasks { get; }
    List<TodoItem> RemoveTasks(string query, bool complete);
    bool AlarmRinging { get; }
    void StopAlarm();
    void SnoozeAlarm(int minutes = 5);
    void SaveData();
}

public sealed record ToolOutput(string Text, bool IsError = false, byte[]? Jpeg = null);

// Tudo que o assistente consegue fazer no seu PC está aqui — nada além disso.
public sealed class AgentTools
{
    static readonly string[] ExecutableExtensions = { ".exe", ".bat", ".cmd", ".ps1", ".vbs", ".msi", ".com", ".scr", ".js", ".jse", ".wsf", ".lnk" };
    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", ".git", "bin", "obj", "dist", "build", ".vs", ".idea", "packages", "target", "__pycache__", ".venv", "venv", "AppData" };

    readonly IAssistantHost _host;
    List<(string Name, string AppId)>? _startApps;
    DateTime _startAppsAt;

    public sealed record ToolSpec(string Name, string Description, Dictionary<string, JsonElement> Properties, string[] Required);

    public List<ToolSpec> Specs { get; } = new();

    // Formato do Claude.
    public List<BetaToolUnion> AnthropicDefinitions() => Specs
        .Select(t => (BetaToolUnion)new BetaTool
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = new() { Properties = t.Properties, Required = t.Required },
        })
        .ToList();

    // Formato do Ollama (IA local). Modelos locais não enxergam imagens, então sem captura de tela.
    public List<object> OllamaDefinitions() => Specs
        .Where(t => t.Name != "look_at_screen")
        .Select(t => (object)new
        {
            type = "function",
            function = new
            {
                name = t.Name,
                description = t.Description,
                parameters = new { type = "object", properties = t.Properties, required = t.Required },
            },
        })
        .ToList();

    public AgentTools(IAssistantHost host)
    {
        _host = host;

        Add("get_system_status",
            "Mostra o que está acontecendo no PC agora: data/hora, janela que o usuário estava usando, música tocando, volume, bateria, CPU e memória.",
            new { });
        Add("list_windows",
            "Lista as janelas abertas (título e programa).",
            new { });
        Add("find_projects",
            "Procura pastas de projetos pelo nome nas pastas de projetos do usuário. Retorna caminho e tipo (Node, .NET, Python...). Use antes de abrir/compilar um projeto; nunca invente caminhos.",
            new { query = Prop("string", "Parte do nome do projeto, ex.: \"island\"") },
            "query");
        Add("list_directory",
            "Lista arquivos e pastas de um diretório.",
            new { path = Prop("string", "Caminho absoluto") },
            "path");
        Add("read_text_file",
            "Lê um arquivo de texto (até ~20 mil caracteres). Útil para package.json, README, .csproj, logs.",
            new { path = Prop("string", "Caminho absoluto") },
            "path");
        Add("open_path",
            "Abre um arquivo ou pasta. Sem 'program', usa o programa padrão do Windows. Com 'program' (ex.: \"code\", \"devenv\", \"explorer\", \"notepad\", \"idea64\", \"rider64\"), abre com ele.",
            new
            {
                path = Prop("string", "Caminho absoluto do arquivo ou pasta"),
                program = Prop("string", "Opcional: programa para abrir"),
            },
            "path");
        Add("launch_app",
            "Abre um aplicativo ou JOGO instalado pelo nome (Menu Iniciar, Steam e Epic Games; entende siglas como \"cs2\", \"gta 5\", \"r6\"). Se já estiver aberto, traz para a frente. Ex.: \"Spotify\", \"Discord\", \"cs2\".",
            new { name = Prop("string", "Nome do aplicativo") },
            "name");
        Add("run_command",
            "Executa um comando do PowerShell (ex.: \"npm run build\", \"dotnet build\", \"git status\") e devolve a saída. O usuário precisa aprovar. Use visible=true para abrir num terminal visível (servidores de desenvolvimento, comandos interativos ou longos).",
            new
            {
                command = Prop("string", "Comando PowerShell"),
                working_directory = Prop("string", "Pasta onde executar (caminho absoluto)"),
                visible = Prop("boolean", "Abrir em janela de terminal visível em vez de capturar a saída"),
            },
            "command");
        Add("window_action",
            "Age sobre uma janela aberta encontrada pelo título ou nome do programa.",
            new
            {
                window = Prop("string", "Trecho do título ou nome do programa, ex.: \"spotify\""),
                action = new { type = "string", @enum = new[] { "focus", "minimize", "maximize", "restore", "close" } },
            },
            "window", "action");
        Add("media_control",
            "Controla a música/vídeo que está tocando.",
            new { action = new { type = "string", @enum = new[] { "play_pause", "next", "previous" } } },
            "action");
        Add("play_spotify",
            "Toca no Spotify do usuário uma música, artista, álbum ou playlist (inclusive as playlists dele, pelo nome). Para as músicas curtidas/favoritas/salvas do usuário use kind=\"liked\" (query vazia). A busca do Spotify tolera erros de pronúncia; passe o nome como o usuário falou (pode incluir o artista).",
            new
            {
                query = Prop("string", "O que tocar, ex.: \"evidências chitãozinho e xororó\""),
                kind = new { type = "string", @enum = new[] { "track", "artist", "album", "playlist", "liked" } },
            },
            "query");
        Add("set_volume",
            "Muda o volume do sistema (0 a 100) e/ou liga/desliga o mudo.",
            new
            {
                level = Prop("integer", "Volume de 0 a 100"),
                mute = Prop("boolean", "true = mudo, false = com som"),
            });
        Add("start_focus_timer",
            "Inicia o timer de foco (Pomodoro) da ilha.",
            new { minutes = Prop("integer", "Duração em minutos (opcional)") });
        Add("add_task",
            "Cria uma tarefa, lembrete ou alarme. A data/hora vai no próprio texto, em linguagem natural (ex.: \"Reunião segunda às 18h\", \"Pagar boleto dia 30\", \"Tirar o bolo daqui a 20 minutos\", \"todo dia às 7h\"). Use alarm=true para alarme que toca até o usuário parar.",
            new
            {
                text = Prop("string", "Texto com a data/hora"),
                alarm = Prop("boolean", "true para alarme (toca até parar)"),
            },
            "text");
        Add("list_tasks",
            "Lista as tarefas, lembretes e alarmes pendentes (agenda do usuário). Use SEMPRE que perguntarem o que têm para fazer, a agenda, compromissos ou lembretes. Opcional: date em linguagem natural (\"segunda\", \"amanhã\", \"dia 30\", \"essa semana\") para filtrar.",
            new { date = Prop("string", "Dia para filtrar (opcional)") });
        Add("remove_task",
            "Apaga (ou marca como feita) tarefas/lembretes/alarmes cujo texto contém 'query'. query vazio = todos os pendentes.",
            new
            {
                query = Prop("string", "Parte do texto da tarefa"),
                complete = Prop("boolean", "true = marcar como feita em vez de apagar"),
            });
        Add("set_app_volume",
            "Muda o volume de UM aplicativo só (como o Mixer de Volume do Windows), ex.: Spotify, Discord, Chrome, um jogo. Use level (0-100) para valor exato ou change (ex.: -20) para aumentar/diminuir.",
            new
            {
                app = Prop("string", "Nome do app/processo, ex.: \"spotify\", \"discord\", \"navegador\""),
                level = Prop("integer", "Volume de 0 a 100"),
                change = Prop("integer", "Quanto somar/subtrair (ex.: -20)"),
                mute = Prop("boolean", "true = mudo, false = com som"),
            },
            "app");
        Add("quick_open",
            "Abre algo DENTRO de um app usando a busca rápida dele: servidor/canal/conversa no Discord ou Slack, arquivo no VS Code/Cursor, conversa no WhatsApp, página no Notion. Abre o app se estiver fechado.",
            new
            {
                app = new { type = "string", @enum = new[] { "discord", "slack", "vscode", "cursor", "whatsapp", "teams", "notion" } },
                query = Prop("string", "O que procurar, ex.: nome do servidor ou canal"),
            },
            "app", "query");
        Add("send_whatsapp",
            "Envia uma mensagem no WhatsApp para um contato ou grupo, pelo nome salvo. Abre a conversa, CONFERE o destinatário e só então envia (se não achar a pessoa, não envia nada). Use SEMPRE esta ferramenta para mandar mensagens no WhatsApp; nunca type_text/press_keys.",
            new
            {
                contact = Prop("string", "Nome do contato ou grupo como está salvo, ex.: \"Vinicius Gomes\""),
                message = Prop("string", "Texto exato da mensagem"),
            },
            "contact", "message");
        Add("remember",
            "Guarda na memória (só no PC do usuário) um fato duradouro que ele contou e que vale lembrar depois: sites, contas, nomes e apelidos de pessoas, preferências, apps, projetos, rotinas. Escreva curto, de preferência no formato \"meu X é Y\" (ex.: \"meu site de anti cheat é xxx.com\").",
            new { fact = Prop("string", "O fato, ex.: \"meu editor preferido é o Cursor\"") },
            "fact");
        Add("forget",
            "Apaga da memória os fatos que falam de algo (quando o usuário pedir para esquecer).",
            new { query = Prop("string", "Do que esquecer, ex.: \"site de anti cheat\"") },
            "query");
        Add("app_action",
            "Abre uma PARTE de um programa ou do Windows: biblioteca/loja/amigos/downloads/configurações da Steam; loja/biblioteca da Epic; configurações/DMs do Discord; curtidas do Spotify; histórico/downloads/aba anônima/configurações do navegador; terminal/extensões do VS Code; telas das Configurações do Windows (bluetooth, wifi, som, tela, atualizações, papel de parede…); gerenciador de tarefas, painel de controle, pastas (downloads, documentos, lixeira); ações do PC (bloquear, print, modo escuro/claro, esvaziar lixeira, suspender, desligar, reiniciar); sites conhecidos (youtube, gmail, netflix…). Diga app e action em linguagem natural.",
            new
            {
                app = Prop("string", "Programa: steam, epic, discord, spotify, navegador, vscode, explorador, windows, web, ilha"),
                action = Prop("string", "O que abrir/fazer, ex.: \"biblioteca\", \"loja\", \"configurações\", \"bluetooth\", \"downloads\", \"modo escuro\""),
            },
            "action");
        Add("browser_tab",
            "Abas/guias do navegador (Chrome, Edge, Firefox, Opera, Brave): list = ver todas as abas abertas; switch = ir para uma; close = fechar uma; close_matching = fechar todas que combinam com query; close_others = fechar TODAS menos as de 'keep' (ex.: keep=\"instagram, apuração 2026\"). Sempre devolve as abas que ficaram abertas: confira antes de responder ao usuário.",
            new
            {
                action = new { type = "string", @enum = new[] { "list", "switch", "close", "close_matching", "close_others" } },
                query = Prop("string", "Parte do título da aba, ex.: \"youtube\""),
                index = Prop("integer", "Número da aba (1 = primeira, 9 = última)"),
                keep = Prop("string", "close_others: abas a manter, separadas por vírgula"),
            },
            "action");
        Add("read_window",
            "Lê os itens visíveis de uma janela (botões, listas, links, textos) para entender o que dá para fazer nela.",
            new { window = Prop("string", "Nome do programa ou trecho do título") },
            "window");
        Add("click_element",
            "Clica num item de uma janela pelo nome (use read_window antes para ver os nomes).",
            new
            {
                window = Prop("string", "Nome do programa ou trecho do título"),
                name = Prop("string", "Nome do botão/item"),
            },
            "window", "name");
        Add("scroll",
            "Rola a página/janela que o usuário está usando (ou a indicada): direction = down, up, top (início) ou bottom (fim).",
            new
            {
                window = Prop("string", "Programa (opcional: usa a janela que o usuário estava usando)"),
                direction = new { type = "string", @enum = new[] { "down", "up", "top", "bottom" } },
                pages = Prop("integer", "Quantas telas rolar (padrão 1)"),
            },
            "direction");
        Add("mouse",
            "Controla o mouse: move para uma posição da tela (position: center, top_left, top_right, bottom_left, bottom_right, top, bottom, left, right), para coordenadas x/y em pixels, ou anda dx/dy pixels; e/ou clica (click, double_click, right_click, middle_click) onde ficar.",
            new
            {
                action = new { type = "string", @enum = new[] { "move", "click", "double_click", "right_click", "middle_click" } },
                position = new { type = "string", @enum = new[] { "center", "top_left", "top_right", "bottom_left", "bottom_right", "top", "bottom", "left", "right" } },
                x = Prop("integer", "Coordenada X em pixels (opcional)"),
                y = Prop("integer", "Coordenada Y em pixels (opcional)"),
                dx = Prop("integer", "Andar na horizontal (+ direita, - esquerda)"),
                dy = Prop("integer", "Andar na vertical (+ baixo, - cima)"),
                screen = Prop("integer", "Monitor (1, 2…). Padrão: onde o mouse está"),
            },
            "action");
        Add("press_keys",
            "Aperta teclas/atalhos no teclado do usuário (na janela indicada ou na que ele está usando), ex.: \"ctrl+c\", \"enter\", \"ctrl+shift+p\", \"alt+f4\", \"win+d\", \"f5\", \"up\", \"pagedown\". Para várias em sequência, chame de novo.",
            new
            {
                window = Prop("string", "Programa que deve receber as teclas (opcional: usa o que está em foco)"),
                keys = Prop("string", "Combinação de teclas"),
            },
            "keys");
        Add("type_text",
            "Digita qualquer texto no teclado do usuário (com acentos), na janela indicada ou na que ele está usando; opcionalmente aperta Enter no fim.",
            new
            {
                window = Prop("string", "Programa que deve receber o texto (opcional)"),
                text = Prop("string", "Texto a digitar"),
                press_enter = Prop("boolean", "Apertar Enter depois"),
            },
            "text");
        Add("web_search",
            "Pesquisa na internet (resultados da web + notícias recentes) e devolve títulos, trechos e links. Use para QUALQUER coisa atual ou que você não saiba: notícias, eleições, placares, preços, clima, cotações, lançamentos, horários, fatos.",
            new { query = Prop("string", "O que pesquisar") },
            "query");
        Add("fetch_page",
            "Lê o texto de uma página da internet (use depois de web_search para ver os detalhes de um resultado).",
            new { url = Prop("string", "URL http(s)") },
            "url");
        Add("open_url",
            "Abre um site no navegador padrão. Para pesquisar algo, use https://www.google.com/search?q=...",
            new { url = Prop("string", "URL http(s)") },
            "url");
        Add("look_at_screen",
            "Tira uma captura da tela principal para você ver o que está nela. Use só quando o usuário pedir algo sobre o que aparece na tela.",
            new { });
    }

    static object Prop(string type, string description) => new { type, description };

    void Add(string name, string description, object properties, params string[] required)
    {
        var props = JsonSerializer.SerializeToElement(properties).EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone());
        Specs.Add(new ToolSpec(name, description, props, required));
    }

    // ---------------- Execução ----------------

    public async Task<ToolOutput> ExecuteAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        var output = await ExecuteCoreAsync(name, input, ct);
        // Diário local do que o assistente fez (só o nome da ferramenta e se deu certo; sem o conteúdo).
        Log.Info($"ferramenta {name}: {(output.IsError ? "ERRO" : "ok")}");
        return output;
    }

    async Task<ToolOutput> ExecuteCoreAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        try
        {
            return name switch
            {
                "get_system_status" => new(SystemStatus()),
                "list_windows" => new(ListWindows()),
                "find_projects" => new(await Task.Run(() => FindProjects(Str(input, "query")), ct)),
                "list_directory" => new(ListDirectory(Str(input, "path"))),
                "read_text_file" => new(await ReadTextFileAsync(Str(input, "path"))),
                "open_path" => await OpenPathAsync(Str(input, "path"), Str(input, "program")),
                "launch_app" => await LaunchAppAsync(Str(input, "name")),
                "run_command" => await RunCommandAsync(Str(input, "command"), Str(input, "working_directory"), Bool(input, "visible") == true, ct),
                "window_action" => await WindowActionAsync(Str(input, "window"), Str(input, "action")),
                "media_control" => MediaControl(Str(input, "action")),
                "play_spotify" => await PlaySpotifyAsync(Str(input, "query"), Str(input, "kind"), ct),
                "set_volume" => SetVolume(Int(input, "level"), Bool(input, "mute")),
                "start_focus_timer" => StartTimer(Int(input, "minutes")),
                "add_task" => AddTask(Str(input, "text"), Bool(input, "alarm") == true),
                "list_tasks" => new(ListTasks(Str(input, "date"))),
                "remove_task" => RemoveTask(Str(input, "query"), Bool(input, "complete") == true),
                "set_app_volume" => SetAppVolume(Str(input, "app"), Int(input, "level"), Int(input, "change"), Bool(input, "mute")),
                "quick_open" => await QuickOpenAsync(Str(input, "app"), Str(input, "query"), ct),
                "browser_tab" => await BrowserTabAsync(Str(input, "action"), Str(input, "query"), Int(input, "index"), ct, Str(input, "keep")),
                "send_whatsapp" => await SendWhatsAppAsync(Str(input, "contact"), Str(input, "message"), ct),
                "remember" => Remember(Str(input, "fact")),
                "forget" => Forget(Str(input, "query")),
                "app_action" => await AppActionAsync(Str(input, "app"), Str(input, "action"), ct),
                "read_window" => await ReadWindowAsync(Str(input, "window")),
                "click_element" => await ClickElementAsync(Str(input, "window"), Str(input, "name")),
                "scroll" => await ScrollAsync(Str(input, "window"), Str(input, "direction"), Int(input, "pages") ?? 1, ct),
                "mouse" => Mouse(Str(input, "action"), Str(input, "position"), Int(input, "x"), Int(input, "y"), Int(input, "dx"), Int(input, "dy"), Int(input, "screen")),
                "press_keys" => await PressKeysAsync(Str(input, "window"), Str(input, "keys"), ct),
                "type_text" => await TypeTextAsync(Str(input, "window"), Str(input, "text"), Bool(input, "press_enter") == true, ct),
                "open_url" => OpenUrl(Str(input, "url")),
                "web_search" => await WebSearchAsync(Str(input, "query"), ct),
                "fetch_page" => await FetchPageAsync(Str(input, "url"), ct),
                "look_at_screen" => await LookAtScreenAsync(),
                _ => new("Ferramenta desconhecida: " + name, true),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            return new(Loc.T("copy.error") + ": " + e.Message, true);
        }
    }

    static string Str(IReadOnlyDictionary<string, JsonElement> d, string k) =>
        d.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static int? Int(IReadOnlyDictionary<string, JsonElement> d, string k) =>
        d.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;

    static bool? Bool(IReadOnlyDictionary<string, JsonElement> d, string k) =>
        d.TryGetValue(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    static string Expand(string path) => Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));

    static string Normalize(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in d)
            if (char.IsLetterOrDigit(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    // ---------------- Estado do sistema ----------------

    string SystemStatus()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Data/hora: {DateTime.Now:dddd, dd/MM/yyyy HH:mm}");
        if (WindowTools.Describe(_host.LastExternalWindow) is { } w)
            sb.AppendLine($"Janela que o usuário estava usando: \"{w.Title}\" ({w.Process})");

        var m = _host.Media;
        sb.AppendLine(m.HasMedia
            ? $"Mídia: {m.Title} — {m.Artist} ({m.AppName}), {(m.IsPlaying ? "tocando" : "pausada")}"
            : "Mídia: nada tocando");

        var a = _host.Audio;
        if (a.Volume >= 0) sb.AppendLine($"Volume: {Math.Round(a.Volume * 100)}%{(a.Muted ? " (mudo)" : "")}, saída: {a.DeviceName ?? "?"}");

        var (hasBattery, pct, charging) = SystemWatcher.Battery();
        if (hasBattery) sb.AppendLine($"Bateria: {pct}%{(charging ? " (carregando)" : "")}");

        var mem = SystemStats.Memory();
        sb.AppendLine($"CPU: {SystemStats.Cpu()}% | Memória: {mem.Percent}% ({mem.UsedGb:0.0} de {mem.TotalGb:0.0} GB)");
        return sb.ToString();
    }

    string ListWindows()
    {
        var windows = WindowTools.List(_host.OwnWindow);
        if (windows.Count == 0) return "Nenhuma janela aberta.";
        return string.Join("\n", windows.Take(40).Select(w => $"- \"{w.Title}\" ({w.Process}){(w.Minimized ? " [minimizada]" : "")}"));
    }

    // ---------------- Arquivos e projetos ----------------

    string FindProjects(string query)
    {
        if (Normalize(query).Length == 0) return "Informe parte do nome do projeto.";
        var roots = _host.Settings.ProjectFolderList().Where(Directory.Exists).ToList();
        if (roots.Count == 0)
            return "Nenhuma pasta de projetos configurada. Peça ao usuário para adicionar em Configurações › Assistente.";

        var found = SearchProjects(query);
        if (found.Count == 0) return $"Nenhuma pasta com \"{query}\" em: {string.Join(", ", roots)}";
        return string.Join("\n", found.Take(12).Select(f => $"- {f.Path} — {DescribeProject(f.Path)}"));
    }

    // Pastas cujo nome bate com a busca: 0 = igual, 1 = começa com, 2 = contém. Melhores primeiro.
    public List<(string Path, int Score)> SearchProjects(string query)
    {
        var q = Normalize(query);
        var roots = _host.Settings.ProjectFolderList().Where(Directory.Exists).ToList();
        var found = new List<(string Path, int Score)>();
        if (q.Length == 0) return found;
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        var queue = new Queue<(string Dir, int Depth)>(roots.Select(r => (r, 0)));
        int visited = 0;

        while (queue.Count > 0 && visited < 20000)
        {
            var (dir, depth) = queue.Dequeue();
            visited++;
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir, "*", opts); } catch { continue; }

            foreach (var sub in subs)
            {
                var name = Path.GetFileName(sub);
                if (SkipDirs.Contains(name) || name.StartsWith('.')) continue;
                var n = Normalize(name);
                if (n == q) found.Add((sub, 0));
                else if (n.StartsWith(q)) found.Add((sub, 1));
                else if (n.Contains(q)) found.Add((sub, 2));
                if (depth < 3) queue.Enqueue((sub, depth + 1));
            }
        }

        // Prefere pastas que parecem projeto de verdade (têm package.json, .csproj…).
        return found
            .OrderBy(f => f.Score)
            .ThenBy(f => DescribeProject(f.Path) == "pasta" ? 1 : 0)
            .ThenBy(f => f.Path.Length)
            .ToList();
    }

    static string PackageManager(string dir) =>
        File.Exists(Path.Combine(dir, "pnpm-lock.yaml")) ? "pnpm"
        : File.Exists(Path.Combine(dir, "yarn.lock")) ? "yarn"
        : File.Exists(Path.Combine(dir, "bun.lockb")) || File.Exists(Path.Combine(dir, "bun.lock")) ? "bun"
        : "npm";

    static HashSet<string> NpmScripts(string dir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "package.json")));
            if (doc.RootElement.TryGetProperty("scripts", out var s) && s.ValueKind == JsonValueKind.Object)
                return s.EnumerateObject().Select(p => p.Name).ToHashSet();
        }
        catch { }
        return new();
    }

    static bool HasAny(string dir, params string[] patterns) =>
        patterns.Any(p => Directory.EnumerateFiles(dir, p).Any());

    // Comando para compilar o projeto, pelo tipo detectado (null se não souber).
    public static string? BuildCommandFor(string dir)
    {
        if (File.Exists(Path.Combine(dir, "package.json")))
            return NpmScripts(dir).Contains("build") ? $"{PackageManager(dir)} run build" : null;
        if (HasAny(dir, "*.sln", "*.slnx", "*.csproj", "*.fsproj")) return "dotnet build";
        if (File.Exists(Path.Combine(dir, "Cargo.toml"))) return "cargo build";
        if (File.Exists(Path.Combine(dir, "go.mod"))) return "go build ./...";
        if (File.Exists(Path.Combine(dir, "pom.xml"))) return "mvn -q package";
        if (File.Exists(Path.Combine(dir, "gradlew.bat"))) return ".\\gradlew.bat build";
        return null;
    }

    // Comando para rodar o projeto em desenvolvimento (null se não souber).
    public static string? RunCommandFor(string dir)
    {
        if (File.Exists(Path.Combine(dir, "package.json")))
        {
            var scripts = NpmScripts(dir);
            var pm = PackageManager(dir);
            return scripts.Contains("dev") ? $"{pm} run dev" : scripts.Contains("start") ? $"{pm} run start" : null;
        }
        if (HasAny(dir, "*.csproj", "*.fsproj")) return "dotnet run";
        if (File.Exists(Path.Combine(dir, "Cargo.toml"))) return "cargo run";
        if (File.Exists(Path.Combine(dir, "go.mod"))) return "go run .";
        return null;
    }

    static string DescribeProject(string dir)
    {
        var kinds = new List<string>();
        try
        {
            var files = Directory.GetFiles(dir).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (files.Contains("package.json"))
            {
                var scripts = "";
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "package.json")));
                    if (doc.RootElement.TryGetProperty("scripts", out var s) && s.ValueKind == JsonValueKind.Object)
                        scripts = " scripts: " + string.Join(", ", s.EnumerateObject().Select(p => p.Name).Take(10));
                }
                catch { }
                kinds.Add("Node.js" + scripts);
            }
            var sln = files.FirstOrDefault(f => f!.EndsWith(".sln", StringComparison.OrdinalIgnoreCase));
            var csproj = files.FirstOrDefault(f => f!.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
            if (sln != null || csproj != null) kinds.Add($".NET ({sln ?? csproj})");
            if (files.Contains("pyproject.toml") || files.Contains("requirements.txt")) kinds.Add("Python");
            if (files.Contains("pom.xml") || files.Contains("build.gradle") || files.Contains("build.gradle.kts")) kinds.Add("Java/Kotlin");
            if (files.Contains("Cargo.toml")) kinds.Add("Rust");
            if (files.Contains("go.mod")) kinds.Add("Go");
            if (files.Contains("composer.json")) kinds.Add("PHP");
            if (Directory.Exists(Path.Combine(dir, ".git"))) kinds.Add("git");
        }
        catch { }
        return kinds.Count == 0 ? "pasta" : string.Join(", ", kinds);
    }

    static string ListDirectory(string path)
    {
        path = Expand(path);
        if (!Directory.Exists(path)) return "Pasta não encontrada: " + path;
        var opts = new EnumerationOptions { IgnoreInaccessible = true };
        var dirs = Directory.EnumerateDirectories(path, "*", opts).Select(d => "[pasta] " + Path.GetFileName(d));
        var files = Directory.EnumerateFiles(path, "*", opts).Select(f => $"{Path.GetFileName(f)} ({new FileInfo(f).Length / 1024.0:0.#} KB)");
        var all = dirs.Concat(files).Take(200).ToList();
        return all.Count == 0 ? "(vazia)" : string.Join("\n", all);
    }

    static async Task<string> ReadTextFileAsync(string path)
    {
        path = Expand(path);
        var info = new FileInfo(path);
        if (!info.Exists) return "Arquivo não encontrado: " + path;
        if (info.Length > 2_000_000) return "Arquivo grande demais para ler (" + info.Length / 1024 + " KB).";
        var text = await File.ReadAllTextAsync(path);
        if (text.AsSpan(0, Math.Min(text.Length, 8000)).Contains('\0')) return "Parece ser um arquivo binário.";
        return text.Length > 20000 ? text[..20000] + "\n…(cortado)" : text;
    }

    // ---------------- Abrir coisas ----------------

    async Task<ToolOutput> OpenPathAsync(string path, string program)
    {
        path = Expand(path);
        bool isUrl = path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (!isUrl && !File.Exists(path) && !Directory.Exists(path)) return new("Caminho não existe: " + path, true);

        if (string.IsNullOrWhiteSpace(program))
        {
            if (ExecutableExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())
                && _host.Settings.ConfirmCommands && !await _host.ConfirmAsync("Executar este arquivo?", path))
                return new("O usuário não permitiu.");
            _host.Step("", "Abrindo " + Path.GetFileName(path.TrimEnd('\\')));
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return new("Aberto: " + path);
        }

        var exe = ResolveProgram(program);
        if (exe == null) return new($"Programa \"{program}\" não encontrado neste PC.", true);
        _host.Step("", $"Abrindo {Path.GetFileName(path.TrimEnd('\\'))} com {Path.GetFileNameWithoutExtension(exe)}");
        StartProgram(exe, "\"" + path + "\"", Path.GetDirectoryName(path));
        return new($"Aberto {path} com {exe}");
    }

    static void StartProgram(string exe, string args, string? workingDir)
    {
        var ext = Path.GetExtension(exe).ToLowerInvariant();
        var psi = ext is ".cmd" or ".bat"
            ? new ProcessStartInfo("cmd.exe", $"/c \"\"{exe}\" {args}\"") { CreateNoWindow = true, UseShellExecute = false }
            : new ProcessStartInfo(exe, args) { UseShellExecute = true };
        if (workingDir != null && Directory.Exists(workingDir)) psi.WorkingDirectory = workingDir;
        Process.Start(psi);
    }

    // Acha o executável de um programa: caminho direto, apelidos, "App Paths" do registro ou PATH.
    static string? ResolveProgram(string program)
    {
        program = Expand(program);
        if (File.Exists(program)) return program;

        var key = Normalize(program);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string? alias = key switch
        {
            "vscode" or "vscodium" or "code" or "visualstudiocode" => FirstExisting(
                Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe")) ?? "code",
            "visualstudio" or "devenv" or "vs" => FindVisualStudio() ?? "devenv",
            "explorer" or "exploradordearquivos" => "explorer.exe",
            "terminal" or "windowsterminal" or "wt" => "wt.exe",
            "notepad" or "blocodenotas" => "notepad.exe",
            "cursor" => FirstExisting(Path.Combine(local, "Programs", "cursor", "Cursor.exe")) ?? "cursor",
            _ => null,
        };
        if (alias != null && File.Exists(alias)) return alias;
        var name = alias ?? program;

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            var exeName = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
            using var k = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exeName);
            if (k?.GetValue(null) is string p && File.Exists(p.Trim('"'))) return p.Trim('"');
        }

        var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';');
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts.Prepend(""))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), name + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }
        return null;
    }

    static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    static string? FindVisualStudio()
    {
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return null;
        try
        {
            var p = Process.Start(new ProcessStartInfo(vswhere, "-latest -property productPath")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            var path = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    async Task<ToolOutput> LaunchAppAsync(string name)
    {
        // Já está aberto? Traz a janela existente para a frente em vez de abrir outra.
        if (WindowTools.FindByProcess(name, _host.OwnWindow) is { } open)
        {
            WindowTools.Focus(open.Handle);
            _host.Step("", open.Process);
            return new("Já estava aberto; trouxe para a frente: " + open.Process);
        }
        name = System.Text.RegularExpressions.Regex.Replace(name.Trim(), @"\s+(?:pela|pelo|na|no|da|do|on|in|from|en|por|de) (?:steam|epic(?: games)?)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var q = Normalize(name);
        if (q.Length == 0) return new("Informe o nome do app.", true);

        // Jogo da Steam/Epic pelo nome ou sigla ("cs2", "gta 5")
        if (GameLibrary.Match(name, strict: true) is { } game)
        {
            _host.Step("", $"{game.Store}: {game.Name}");
            GameLibrary.Launch(game);
            return new($"Aberto: {game.Name} ({game.Store})");
        }

        var apps = await GetStartAppsAsync();
        var match = apps
            .Select(a => (a, n: Normalize(a.Name)))
            .Select(x => (x.a, score: x.n == q ? 0 : x.n.StartsWith(q) ? 1 : x.n.Contains(q) ? 2 : 9))
            .Where(x => x.score < 9)
            .OrderBy(x => x.score).ThenBy(x => x.a.Name.Length)
            .Select(x => x.a)
            .FirstOrDefault();

        if (match.AppId != null)
        {
            _host.Step("", "Abrindo " + match.Name);
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + match.AppId) { UseShellExecute = true });
            return new("Aberto: " + match.Name);
        }

        if (GameLibrary.Match(name, strict: false) is { } looseGame)
        {
            _host.Step("", $"{looseGame.Store}: {looseGame.Name}");
            GameLibrary.Launch(looseGame);
            return new($"Aberto: {looseGame.Name} ({looseGame.Store})");
        }

        if (ResolveProgram(name) is { } exe)
        {
            _host.Step("", "Abrindo " + Path.GetFileNameWithoutExtension(exe));
            StartProgram(exe, "", null);
            return new("Aberto: " + exe);
        }
        return new($"Não encontrei nenhum app chamado \"{name}\".", true);
    }

    // Lista de apps do Menu Iniciar (inclui apps da Microsoft Store). Guardada por 10 min.
    async Task<List<(string Name, string AppId)>> GetStartAppsAsync()
    {
        if (_startApps != null && DateTime.Now - _startAppsAt < TimeSpan.FromMinutes(10)) return _startApps;
        var list = new List<(string, string)>();
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-StartApps | ConvertTo-Json -Compress\"")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            var json = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new() { doc.RootElement };
            foreach (var e in items)
                if (e.TryGetProperty("Name", out var n) && e.TryGetProperty("AppID", out var id))
                    list.Add((n.GetString() ?? "", id.GetString() ?? ""));
        }
        catch { }
        _startApps = list;
        _startAppsAt = DateTime.Now;
        return list;
    }

    // ---------------- Comandos ----------------

    async Task<ToolOutput> RunCommandAsync(string command, string workingDirectory, bool visible, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command)) return new("Comando vazio.", true);
        var dir = string.IsNullOrWhiteSpace(workingDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Expand(workingDirectory);
        if (!Directory.Exists(dir)) return new("Pasta não existe: " + dir, true);

        if (_host.Settings.ConfirmCommands && !await _host.ConfirmAsync(visible ? "Abrir terminal com este comando?" : "Executar este comando?", $"{command}\n📁 {dir}"))
            return new("O usuário negou a execução deste comando.");

        if (visible)
        {
            _host.Step("", "Terminal: " + command);
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            Process.Start(new ProcessStartInfo("powershell.exe", "-NoExit -EncodedCommand " + encoded)
                { WorkingDirectory = dir, UseShellExecute = true });
            return new("Abri um terminal executando o comando.");
        }

        var step = _host.Step("", command);
        var script = "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'; " + command;
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var lines = new LinkedList<string>();
        void OnLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (lines)
            {
                lines.AddLast(line);
                if (lines.Count > 400) lines.RemoveFirst();
            }
            step.Detail = line.Length > 160 ? line[..160] + "…" : line;
        }

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            await p.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            if (ct.IsCancellationRequested) throw;
            return new("O comando passou de 10 minutos e foi encerrado.", true);
        }

        p.WaitForExit(); // garante que toda a saída foi lida
        string output;
        lock (lines) output = string.Join("\n", lines);
        if (output.Length > 8000) output = "…" + output[^8000..];
        step.Detail = p.ExitCode == 0 ? "✓ concluído" : $"✗ código {p.ExitCode}";
        return new($"Código de saída: {p.ExitCode}\n{output}", p.ExitCode != 0);
    }

    // ---------------- Janelas, mídia, volume ----------------

    async Task<ToolOutput> WindowActionAsync(string query, string action)
    {
        var q = Normalize(query);
        var w = WindowTools.List(_host.OwnWindow)
            .FirstOrDefault(x => Normalize(x.Title).Contains(q) || Normalize(x.Process).Contains(q));
        if (w == null) return new($"Nenhuma janela com \"{query}\".", true);

        switch (action)
        {
            case "focus": WindowTools.Focus(w.Handle); break;
            case "minimize": WindowTools.Minimize(w.Handle); break;
            case "maximize": WindowTools.Maximize(w.Handle); WindowTools.Focus(w.Handle); break;
            case "restore": WindowTools.Restore(w.Handle); break;
            case "close":
                if (_host.Settings.ConfirmCommands && !await _host.ConfirmAsync("Fechar esta janela?", $"\"{w.Title}\" ({w.Process})"))
                    return new("O usuário não permitiu fechar.");
                WindowTools.Close(w.Handle);
                break;
            default: return new("Ação inválida: " + action, true);
        }
        _host.Step("", $"{action} › {w.Process}");
        return new($"Feito ({action}) em \"{w.Title}\".");
    }

    ToolOutput MediaControl(string action)
    {
        if (!_host.Media.HasMedia) return new("Nenhuma mídia aberta no momento.", true);
        switch (action)
        {
            case "play_pause": _host.Media.PlayPause(); break;
            case "next": _host.Media.Next(); break;
            case "previous": _host.Media.Previous(); break;
            default: return new("Ação inválida.", true);
        }
        return new("Ok.");
    }

    async Task<ToolOutput> PlaySpotifyAsync(string query, string kind, CancellationToken ct)
    {
        if (kind == "liked" && !_host.Spotify.IsConnected)
        {
            Process.Start(new ProcessStartInfo("spotify:collection:tracks") { UseShellExecute = true });
            return new(Loc.T("sp.likedOpened"));
        }
        if (kind == "liked")
        {
            _host.Step("", "Spotify: ♥");
            return new(await _host.Spotify.PlayAsync("", "liked", ct));
        }
        if (string.IsNullOrWhiteSpace(query) && !_host.Spotify.IsConnected) { WindowTools.PressKeys("playpause"); return new(Loc.T("sp.resumed")); }
        if (!_host.Spotify.IsConnected)
        {
            // Sem conexão com a conta: pelo menos abre a busca no app.
            Process.Start(new ProcessStartInfo("spotify:search:" + Uri.EscapeDataString(query)) { UseShellExecute = true });
            return new(Loc.T("sp.openedSearch"));
        }
        var label = kind switch { "artist" => "artista", "album" => "álbum", "playlist" => "playlist", _ => "música" };
        _host.Step("", $"Spotify ({label}): {query}");
        return new(await _host.Spotify.PlayAsync(query, kind, ct));
    }

    ToolOutput SetVolume(int? level, bool? mute)
    {
        if (level is int l) _host.Audio.SetVolume(l / 100f);
        if (mute is bool m) _host.Audio.SetMute(m);
        return new("Ok.");
    }

    ToolOutput StartTimer(int? minutes)
    {
        _host.StartFocusTimer(minutes);
        return new("Timer iniciado.");
    }

    ToolOutput AddTask(string text, bool alarm)
    {
        if (string.IsNullOrWhiteSpace(text)) return new("Texto vazio.", true);
        var item = _host.AddTask(text, alarm);
        if (item == null) return new("Não consegui criar.", true);
        var when = item.Due is DateTime d ? d.ToString("dddd, dd/MM/yyyy HH:mm") : "sem data";
        return new($"{(item.Alarm ? "Alarme" : "Tarefa")} criada: \"{item.Text}\" — {when}{(item.Daily ? " (todo dia)" : "")}.");
    }

    public string ListTasks(string date = "")
    {
        var now = DateTime.Now;
        IEnumerable<TodoItem> tasks = _host.PendingTasks;
        var filter = "";
        if (!string.IsNullOrWhiteSpace(date))
        {
            var asked = DateParser.Parse(date, Loc.Lang, now);
            if (asked.Week) { tasks = tasks.Where(t => t.Due >= now.Date && t.Due < now.Date.AddDays(7)); filter = " (próximos 7 dias)"; }
            else if (asked.When is DateTime d) { tasks = tasks.Where(t => t.Due?.Date == d.Date); filter = $" (dia {d:dddd, dd/MM})"; }
        }
        var list = tasks.ToList();
        if (list.Count == 0) return $"Nada pendente{filter}. Hoje é {now:dddd, dd/MM}.";
        return $"Hoje é {now:dddd, dd/MM HH:mm}. Pendentes{filter}:\n" + string.Join("\n", list.Select(t =>
            $"- {(t.Alarm ? "[alarme] " : "")}{t.Text}" +
            (t.Due is DateTime d ? $" — {d:dddd, dd/MM} às {d:HH:mm}{(d.Date == now.Date ? " (hoje)" : d.Date == now.Date.AddDays(1) ? " (amanhã)" : "")}" : " — sem data") +
            (t.Daily ? " (todo dia)" : "")));
    }
    ToolOutput RemoveTask(string query, bool complete)
    {
        var removed = _host.RemoveTasks(query, complete);
        if (removed.Count == 0) return new($"Nenhuma tarefa com \"{query}\".", true);
        return new($"{(complete ? "Concluídas" : "Apagadas")}: {string.Join(", ", removed.Select(t => t.Text))}.");
    }

    ToolOutput SetAppVolume(string app, int? level, int? change, bool? mute)
    {
        if (string.IsNullOrWhiteSpace(app)) return new("Diga qual app.", true);
        var changed = _host.Audio.SetAppVolume(app, level / 100f, change / 100f, mute);
        if (changed.Count == 0)
        {
            var playing = string.Join(", ", _host.Audio.ListAppSessions().Select(s => s.Name).Distinct());
            return new($"\"{app}\" não está tocando som agora. Apps com som: {(playing.Length > 0 ? playing : "nenhum")}.", true);
        }
        return new(string.Join(", ", changed.Select(s => $"{s.Name}: {Math.Round(s.Volume * 100)}%{(s.Muted ? " (mudo)" : "")}")));
    }

    // ---------------- Usar programas por dentro ----------------

    // Atalho de "busca rápida" de cada app, nome do processo e nome no Menu Iniciar.
    static readonly Dictionary<string, (string Process, string StartName, string Keys)> QuickOpenRecipes = new()
    {
        ["discord"] = ("discord", "Discord", "ctrl+k"),
        ["slack"] = ("slack", "Slack", "ctrl+k"),
        ["vscode"] = ("code", "Visual Studio Code", "ctrl+p"),
        ["cursor"] = ("cursor", "Cursor", "ctrl+p"),
        ["whatsapp"] = ("whatsapp", "WhatsApp", "ctrl+f"),
        ["teams"] = ("teams", "Microsoft Teams", "ctrl+e"),
        ["notion"] = ("notion", "Notion", "ctrl+p"),
    };

    public static string? QuickOpenApp(string spoken)
    {
        var k = Normalize(spoken);
        if (k is "vscode" or "visualstudiocode" or "code") return "vscode";
        return QuickOpenRecipes.ContainsKey(k) ? k : null;
    }

    async Task<ToolOutput> QuickOpenAsync(string app, string query, CancellationToken ct)
    {
        var key = QuickOpenApp(app);
        if (key == null) return new($"Não sei abrir coisas dentro de \"{app}\".", true);
        if (string.IsNullOrWhiteSpace(query)) return new("Diga o que abrir.", true);
        var (process, startName, keys) = QuickOpenRecipes[key];

        var window = await EnsureAppWindowAsync(process, startName, ct);
        if (window == null) return new($"Não consegui abrir o {startName}.", true);

        if (key == "whatsapp")
        {
            // WhatsApp: abre a conversa conferindo o nome (o Enter "às cegas" caía na conversa errada).
            _host.Step("\uE8BD", $"WhatsApp: {query}");
            WindowTools.Focus(window.Handle);
            await Task.Delay(300, ct);
            var opened = await Task.Run(() => WhatsApp.OpenAsync(window.Handle, query, ct), ct);
            return new(opened.Text, !opened.Ok);
        }

        _host.Step("", $"{startName}: {query}");
        WindowTools.Focus(window.Handle);
        await Task.Delay(350, ct);
        WindowTools.PressKeys(keys);
        await Task.Delay(450, ct);
        await WindowTools.TypeTextAsync(query, ct);
        await Task.Delay(900, ct); // espera a lista de resultados aparecer
        WindowTools.PressKeys("enter");
        return new($"Abri \"{query}\" no {startName} (pela busca rápida, {keys}).");
    }

    public async Task<ToolOutput> AppActionAsync(string app, string action, CancellationToken ct)
    {
        var a = AppCatalog.Match(app + " " + action) ?? AppCatalog.Match(action);
        if (a == null) return new($"Não conheço \"{action}\" em \"{app}\". Opções:\n{AppCatalog.Describe(app)}", true);
        return await RunCatalogActionAsync(a, ct);
    }

    public async Task<ToolOutput> RunCatalogActionAsync(AppCatalog.Action a, CancellationToken ct)
    {
        _host.Step("", a.Label);
        switch (a.Kind)
        {
            case "uri":
            case "url":
                Process.Start(new ProcessStartInfo(a.Value) { UseShellExecute = true });
                break;

            case "cmd":
                var space = a.Value.IndexOf(' ');
                var exe = space < 0 ? a.Value : a.Value[..space];
                var args = space < 0 ? "" : a.Value[(space + 1)..];
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
                break;

            case "keys":
                var target = WindowTools.Describe(_host.LastExternalWindow);
                if (a.App == "browser" && target != null && WindowTools.IsBrowser(target.Process)) { } // já é o navegador em uso
                else if (AppCatalog.AppProcess.TryGetValue(a.App, out var proc))
                {
                    target = await EnsureAppWindowAsync(proc.Process, proc.StartName, ct);
                    if (target == null) return new($"Não consegui abrir o {proc.StartName}.", true);
                }
                else if (a.App != "current") target = null; // atalhos do Windows (win+d…) não precisam de janela
                if (target != null)
                {
                    WindowTools.Focus(target.Handle);
                    await Task.Delay(300, ct);
                }
                foreach (var combo in a.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    WindowTools.PressKeys(combo);
                    await Task.Delay(150, ct);
                }
                break;

            case "tab":
                var browser = await EnsureAppWindowAsync("navegador", "Google Chrome", ct);
                if (browser == null) return new("Não achei um navegador aberto.", true);
                var address = browser.Process.Equals("firefox", StringComparison.OrdinalIgnoreCase) ? FirefoxPage(a.Value) : a.Value;
                WindowTools.Focus(browser.Handle);
                await Task.Delay(300, ct);
                WindowTools.PressKeys("ctrl+t");
                await Task.Delay(350, ct);
                await WindowTools.TypeTextAsync(address, ct);
                await Task.Delay(100, ct);
                WindowTools.PressKeys("enter");
                break;

            case "special":
                var error = await SpecialAsync(a.Value, a.Label);
                if (error != null) return new(error, true);
                break;
        }
        return new($"Feito: {a.Label}.");
    }

    // Abas pela acessibilidade do Windows: vê a lista real, seleciona a certa e confere o resultado.
    public async Task<ToolOutput> BrowserTabAsync(string action, string query, int? index, CancellationToken ct, string keep = "")
    {
        var last = WindowTools.Describe(_host.LastExternalWindow);
        var browser = last != null && WindowTools.IsBrowser(last.Process) ? last : WindowTools.Find("navegador", _host.OwnWindow);
        if (browser == null) return new("Nenhum navegador aberto.", true);
        WindowTools.Focus(browser.Handle);
        await Task.Delay(250, ct);

        Task<List<BrowserTabs.Tab>> Read() => Task.Run(() =>
        {
            try { return BrowserTabs.List(browser.Handle); }
            catch { return new List<BrowserTabs.Tab>(); }
        }, ct);
        static string Names(List<BrowserTabs.Tab> list) =>
            string.Join("\n", list.Select((t, i) => $"{i + 1}. {t.Name}{(t.Selected ? " (ativa)" : "")}"));

        var tabs = await Read();
        if (tabs.Count == 0) return await BrowserTabByKeysAsync(browser, action, query, index, ct); // navegador sem acessibilidade

        async Task CloseAsync(BrowserTabs.Tab tab)
        {
            BrowserTabs.Select(tab);
            await Task.Delay(150, ct);
            WindowTools.PressKeys("ctrl+w");
            await Task.Delay(250, ct);
        }

        switch (action)
        {
            case "list":
                return new($"Abas abertas no {browser.Process} ({tabs.Count}):\n{Names(tabs)}");

            case "close_others":
            {
                var keeps = (keep.Length > 0 ? keep : query)
                    .Split(new[] { ",", ";", " e ", " and ", " y " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(k => k.Length > 1).ToList();
                if (keeps.Count == 0) return new("Diga quais abas manter.", true);
                bool Kept(BrowserTabs.Tab t) => keeps.Any(k => BrowserTabs.Matches(t.Name, k));
                // Segurança: se nada combina com o que é para manter, não fecha nada (senão fecharia tudo).
                if (!tabs.Any(Kept))
                    return new($"Nenhuma aba combina com \"{string.Join(", ", keeps)}\"; não fechei nada. Abas abertas:\n{Names(tabs)}", true);
                int closed = 0;
                for (int guard = 0; guard < 150; guard++)
                {
                    var victim = tabs.FirstOrDefault(t => !Kept(t));
                    if (victim == null) break;
                    await CloseAsync(victim);
                    closed++;
                    tabs = await Read();
                }
                _host.Step("\uE737", $"✕ {closed}");
                return new($"Fechei {closed} aba(s). Continuam abertas ({tabs.Count}):\n{Names(tabs)}");
            }

            case "close_matching":
            {
                if (query.Length == 0) return new("Diga quais abas fechar.", true);
                int closed = 0;
                for (int guard = 0; guard < 150; guard++)
                {
                    var victim = tabs.FirstOrDefault(t => BrowserTabs.Matches(t.Name, query));
                    if (victim == null || tabs.Count == 1 && closed > 0) break;
                    await CloseAsync(victim);
                    closed++;
                    tabs = await Read();
                }
                if (closed == 0) return new($"Nenhuma aba com \"{query}\". Abas abertas:\n{Names(tabs)}", true);
                _host.Step("\uE737", $"✕ {closed}");
                return new($"Fechei {closed} aba(s) com \"{query}\". Continuam abertas ({tabs.Count}):\n{Names(tabs)}");
            }

            default:
            {
                var target = index is int i ? (i >= 9 || i > tabs.Count ? tabs.Last() : tabs[Math.Max(1, i) - 1])
                    : query.Length > 0 ? tabs.FirstOrDefault(t => BrowserTabs.Matches(t.Name, query))
                    : tabs.FirstOrDefault(t => t.Selected) ?? tabs.Last();
                if (target == null) return new($"Não achei a aba \"{query}\". Abas abertas:\n{Names(tabs)}", true);
                if (action == "close")
                {
                    await CloseAsync(target);
                    tabs = await Read();
                    _host.Step("\uE737", "✕ " + target.Name);
                    return new($"Fechei \"{target.Name}\". Continuam abertas ({tabs.Count}):\n{Names(tabs)}");
                }
                BrowserTabs.Select(target);
                _host.Step("\uE737", target.Name);
                return new($"Aba \"{target.Name}\".");
            }
        }
    }

    // Plano B (navegador que não mostra as abas para a acessibilidade): o título da janela é o da aba ativa,
    // então anda com Ctrl+Tab até achar a aba pedida.
    async Task<ToolOutput> BrowserTabByKeysAsync(WindowInfo browser, string action, string query, int? index, CancellationToken ct)
    {
        if (action is "list" or "close_others" or "close_matching")
            return new("Este navegador não mostra a lista de abas; peça uma aba por vez.", true);

        string Title() => WindowTools.Describe(browser.Handle)?.Title ?? "";
        string Fold(string t) => AppCatalog.Norm(t).Replace(" ", "");
        var q = Fold(query);
        if (index is int i)
        {
            WindowTools.PressKeys("ctrl+" + Math.Clamp(i, 1, 9));
            await Task.Delay(250, ct);
        }
        else if (q.Length > 0 && !Fold(Title()).Contains(q))
        {
            var first = Title();
            bool found = false;
            for (int k = 0; k < 40 && !found; k++)
            {
                WindowTools.PressKeys("ctrl+tab");
                await Task.Delay(180, ct);
                var t = Title();
                if (Fold(t).Contains(q)) found = true;
                else if (t == first) break; // deu a volta
            }
            if (!found) return new($"Não achei nenhuma aba com \"{query}\".", true);
        }
        var title = Title();
        var name = title.Split(" - ")[0].Trim();
        if (action == "close")
        {
            WindowTools.PressKeys("ctrl+w");
            _host.Step("", "✕ " + name);
            return new($"Fechei a aba \"{name}\".");
        }
        _host.Step("", name);
        return new($"Aba \"{name}\".");
    }

    static string FirefoxPage(string chrome) => chrome switch
    {
        "chrome://settings" => "about:preferences",
        "chrome://extensions" => "about:addons",
        _ when chrome.Contains("password") => "about:logins",
        _ => chrome,
    };

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);
    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    async Task<string?> SpecialAsync(string what, string label)
    {
        // Desligar/reiniciar/sair seguem a opção "pedir confirmação" das configurações.
        if (what is "shutdown" or "restart" or "logoff" && _host.Settings.ConfirmCommands
            && !await _host.ConfirmAsync(label, label + "?"))
            return "Cancelado pelo usuário.";
        switch (what)
        {
            case "lock": LockWorkStation(); break;
            case "dark":
            case "light":
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    int light = what == "light" ? 1 : 0;
                    key.SetValue("AppsUseLightTheme", light, Microsoft.Win32.RegistryValueKind.DWord);
                    key.SetValue("SystemUsesLightTheme", light, Microsoft.Win32.RegistryValueKind.DWord);
                }
                SendMessageTimeout((IntPtr)0xFFFF, 0x001A /*WM_SETTINGCHANGE*/, IntPtr.Zero, "ImmersiveColorSet", 2, 1000, out _);
                break;
            case "emptybin": SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4); break; // sem perguntar, sem barra, sem som
            case "sleep": System.Windows.Forms.Application.SetSuspendState(System.Windows.Forms.PowerState.Suspend, false, false); break;
            // 30 s de folga: "shutdown /a" cancela.
            case "shutdown": Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 30") { CreateNoWindow = true, UseShellExecute = false }); break;
            case "restart": Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 30") { CreateNoWindow = true, UseShellExecute = false }); break;
            case "logoff": Process.Start(new ProcessStartInfo("shutdown.exe", "/l") { CreateNoWindow = true, UseShellExecute = false }); break;
            case "islandsettings":
                if (_host is MainWindow mw) mw.Dispatcher.Invoke(() => mw.OpenSettings());
                else return "As configurações só abrem pela ilha.";
                break;
            default: return "Ação desconhecida: " + what;
        }
        return null;
    }

    public ToolOutput Remember(string fact)
    {
        if (string.IsNullOrWhiteSpace(fact)) return new("Diga o que guardar.", true);
        var saved = UserMemory.Remember(_host.Settings.Memories, fact);
        _host.SaveData();
        _host.Step("\uE8F1", saved);
        return new($"Guardado na memória: \"{saved}\".");
    }

    public ToolOutput Forget(string query)
    {
        var removed = UserMemory.Forget(_host.Settings.Memories, query);
        if (removed.Count == 0) return new($"Não havia nada sobre \"{query}\" na memória.", true);
        _host.SaveData();
        return new("Apagado da memória: " + string.Join(" | ", removed));
    }

    async Task<ToolOutput> SendWhatsAppAsync(string contact, string message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(contact) || string.IsNullOrWhiteSpace(message)) return new("Diga o contato e a mensagem.", true);
        if (_host.Settings.ConfirmCommands && !await _host.ConfirmAsync("WhatsApp", $"{contact}: \"{message}\""))
            return new("Cancelado pelo usuário.", true);
        var window = await EnsureAppWindowAsync("whatsapp", "WhatsApp", ct);
        if (window == null) return new("Não consegui abrir o WhatsApp.", true);
        _host.Step("\uE8BD", $"WhatsApp → {contact}");
        WindowTools.Focus(window.Handle);
        await Task.Delay(300, ct);
        var sent = await Task.Run(() => WhatsApp.SendAsync(window.Handle, contact, message, ct), ct);
        return new(sent.Text, !sent.Ok);
    }

    // Acha a janela do app; se estiver fechado, abre e espera aparecer.
    async Task<WindowInfo?> EnsureAppWindowAsync(string process, string startName, CancellationToken ct)
    {
        var w = WindowTools.Find(process, _host.OwnWindow);
        if (w != null) return w;
        await LaunchAppAsync(startName);
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(700, ct);
            w = WindowTools.Find(process, _host.OwnWindow);
            if (w != null)
            {
                await Task.Delay(2500, ct); // apps grandes ainda estão carregando
                return w;
            }
        }
        return null;
    }

    WindowInfo? TargetWindow(string window) =>
        string.IsNullOrWhiteSpace(window)
            ? WindowTools.Describe(_host.LastExternalWindow)
            : WindowTools.Find(window, _host.OwnWindow);

    async Task<ToolOutput> ReadWindowAsync(string window)
    {
        var w = TargetWindow(window);
        if (w == null) return new($"Nenhuma janela com \"{window}\".", true);
        _host.Step("", $"Lendo {w.Process}");
        var text = await Task.Run(() => UiReader.Read(w.Handle));
        return new($"Janela \"{w.Title}\" ({w.Process}):\n{text}");
    }

    async Task<ToolOutput> ClickElementAsync(string window, string name)
    {
        var w = TargetWindow(window);
        if (w == null) return new($"Nenhuma janela com \"{window}\".", true);
        WindowTools.Focus(w.Handle);
        var clicked = await Task.Run(() => UiReader.Click(w.Handle, name));
        if (clicked == null) return new($"Não achei \"{name}\" na janela {w.Process}. Use read_window para ver os nomes.", true);
        _host.Step("", $"Clique: {clicked}");
        return new($"Cliquei em \"{clicked}\".");
    }

    public async Task<ToolOutput> ScrollAsync(string window, string direction, int pages, CancellationToken ct)
    {
        var w = TargetWindow(window);
        if (w == null) return new("Não achei a janela para rolar.", true);
        WindowTools.Focus(w.Handle);
        await Task.Delay(200, ct);
        var key = direction switch { "up" => "pageup", "top" => "home", "bottom" => "end", _ => "pagedown" };
        int times = key is "home" or "end" ? 1 : Math.Clamp(pages, 1, 10);
        for (int i = 0; i < times; i++)
        {
            WindowTools.PressKeys(key);
            await Task.Delay(120, ct);
        }
        _host.Step("", $"{w.Process}: {direction}");
        return new($"Rolei {direction} em \"{w.Title}\".");
    }

    public ToolOutput Mouse(string action, string position, int? x, int? y, int? dx, int? dy, int? screen)
    {
        var area = WindowTools.ScreenBounds(screen);
        var (cx, cy) = WindowTools.CursorPosition();
        int m = 24; // longe da borda, para não cair fora da tela nem disparar cantos ativos
        (int X, int Y)? target = position switch
        {
            "center" => (area.Left + area.Width / 2, area.Top + area.Height / 2),
            "top_left" => (area.Left + m, area.Top + m),
            "top_right" => (area.Right - m, area.Top + m),
            "bottom_left" => (area.Left + m, area.Bottom - m),
            "bottom_right" => (area.Right - m, area.Bottom - m),
            "top" => (area.Left + area.Width / 2, area.Top + m),
            "bottom" => (area.Left + area.Width / 2, area.Bottom - m),
            "left" => (area.Left + m, area.Top + area.Height / 2),
            "right" => (area.Right - m, area.Top + area.Height / 2),
            _ => null,
        };
        if (x != null || y != null) target = (x ?? cx, y ?? cy);
        if (dx != null || dy != null) target = ((target?.X ?? cx) + (dx ?? 0), (target?.Y ?? cy) + (dy ?? 0));
        if (target is { } t)
        {
            WindowTools.MoveMouse(t.X, t.Y);
            (cx, cy) = t;
        }
        switch (action)
        {
            case "click": WindowTools.MouseClick(); break;
            case "double_click": WindowTools.MouseClick("left", 2); break;
            case "right_click": WindowTools.MouseClick("right"); break;
            case "middle_click": WindowTools.MouseClick("middle"); break;
        }
        _host.Step("\uE962", $"Mouse {action} ({cx}, {cy})");
        return new($"Mouse em ({cx}, {cy}){(action is "move" ? "" : ", " + action)}.");
    }

    // Pedido digitado na própria ilha: as teclas vão para a janela que você estava usando, não para a ilha.
    async Task FocusUserWindowAsync(CancellationToken ct)
    {
        if (Native.GetForegroundWindow() != _host.OwnWindow || _host.LastExternalWindow == IntPtr.Zero) return;
        WindowTools.Focus(_host.LastExternalWindow);
        await Task.Delay(250, ct);
    }

    async Task<ToolOutput> PressKeysAsync(string window, string keys, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(window))
        {
            var w = TargetWindow(window);
            if (w == null) return new($"Nenhuma janela com \"{window}\".", true);
            WindowTools.Focus(w.Handle);
            await Task.Delay(250, ct);
        }
        else await FocusUserWindowAsync(ct);
        if (!WindowTools.PressKeys(keys)) return new($"Não entendi as teclas \"{keys}\".", true);
        _host.Step("", keys);
        return new($"Apertei {keys}.");
    }

    async Task<ToolOutput> TypeTextAsync(string window, string text, bool enter, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(window))
        {
            var w = TargetWindow(window);
            if (w == null) return new($"Nenhuma janela com \"{window}\".", true);
            WindowTools.Focus(w.Handle);
            await Task.Delay(250, ct);
        }
        else await FocusUserWindowAsync(ct);
        await WindowTools.TypeTextAsync(text, ct);
        if (enter)
        {
            await Task.Delay(150, ct);
            WindowTools.PressKeys("enter");
        }
        _host.Step("", text.Length > 40 ? text[..40] + "…" : text);
        return new("Digitado.");
    }

    async Task<ToolOutput> WebSearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return new("Diga o que pesquisar.", true);
        _host.Step("", query);
        return new(await WebSearch.SearchAsync(query, ct));
    }

    async Task<ToolOutput> FetchPageAsync(string url, CancellationToken ct)
    {
        _host.Step("", Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url);
        return new(await WebSearch.FetchAsync(url, ct));
    }

    ToolOutput OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return new("Só abro links http/https.", true);
        _host.Step("", uri.Host);
        Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        return new("Aberto no navegador.");
    }

    async Task<ToolOutput> LookAtScreenAsync()
    {
        if (_host.Settings.ConfirmCommands && !await _host.ConfirmAsync("Enviar uma captura da tela ao Claude?", "Para ele ver o que está na sua tela principal."))
            return new("O usuário não permitiu a captura de tela.");
        _host.Step("", "Olhando a tela");
        var jpeg = await Task.Run(() => WindowTools.CapturePrimaryScreenJpeg());
        return new("Captura da tela principal:", false, jpeg);
    }
}
