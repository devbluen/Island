using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace Island;

// Conversa com o Claude: manda o pedido, executa as ferramentas que ele escolher e repete até ele responder.
public sealed class ClaudeAssistant : IBrain
{
    const int MaxTurns = 25;

    readonly IAssistantHost _host;
    readonly AgentTools _tools;
    readonly List<BetaMessageParam> _history = new();
    DateTime _lastUse = DateTime.MinValue;

    public ClaudeAssistant(IAssistantHost host, AgentTools tools)
    {
        _host = host;
        _tools = tools;
    }

    public void Reset() => _history.Clear();

    // Devolve a resposta final em texto. Textos intermediários vão para onNote.
    public async Task<string> AskAsync(string userText, Action<string> onNote, CancellationToken ct)
    {
        var s = _host.Settings;
        var key = Secret.ApiKey(s);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Configure sua chave da API em Configurações › Assistente.");

        // Conversa "esfria" depois de 15 min sem uso.
        if (DateTime.Now - _lastUse > TimeSpan.FromMinutes(15)) _history.Clear();
        _lastUse = DateTime.Now;

        var client = new AnthropicClient { ApiKey = key };
        _history.Add(new BetaUserMessageParam { Content = userText });

        try
        {
            for (int turn = 0; turn < MaxTurns; turn++)
            {
                ct.ThrowIfCancellationRequested();

                var effort = new BetaOutputConfig { Effort = s.Effort switch { "low" => Effort.Low, "high" => Effort.High, _ => Effort.Medium } };
                var p = s.Model.Contains("haiku")
                    ? new MessageCreateParams
                    {
                        Model = s.Model,
                        MaxTokens = 16000,
                        System = SystemPrompt(s),
                        Tools = _tools.AnthropicDefinitions(),
                        Messages = _history.ToList(),
                        OutputConfig = effort,
                    }
                    : new MessageCreateParams
                    {
                        Model = s.Model,
                        MaxTokens = 16000,
                        System = SystemPrompt(s),
                        Tools = _tools.AnthropicDefinitions(),
                        Messages = _history.ToList(),
                        OutputConfig = effort,
                        // Se um filtro de segurança recusar por engano, o próprio servidor tenta de novo com outro modelo.
                        Betas = ["server-side-fallback-2026-07-01"],
                        Fallbacks = new Default(),
                    };

                BetaMessage resp = await client.Beta.Messages.Create(p, ct);

                if (resp.StopReason == "refusal")
                {
                    _history.Clear();
                    return "Não posso ajudar com esse pedido.";
                }

                var assistantContent = new List<BetaContentBlockParam>();
                var toolUses = new List<BetaToolUseBlock>();
                var texts = new List<string>();

                foreach (var block in resp.Content)
                {
                    if (block.TryPickText(out var text))
                    {
                        assistantContent.Add(new BetaTextBlockParam { Text = text.Text });
                        texts.Add(text.Text);
                    }
                    else if (block.TryPickThinking(out var thinking))
                    {
                        assistantContent.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                    }
                    else if (block.TryPickRedactedThinking(out var redacted))
                    {
                        assistantContent.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
                    }
                    else if (block.TryPickToolUse(out var toolUse))
                    {
                        assistantContent.Add(new BetaToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });
                        toolUses.Add(toolUse);
                    }
                }

                var say = string.Join("\n", texts).Trim();
                if (assistantContent.Count > 0)
                    _history.Add(new BetaAssistantMessageParam { Content = assistantContent });

                if (toolUses.Count == 0) return say.Length > 0 ? say : "Pronto.";
                if (say.Length > 0) onNote(say);

                var results = new List<BetaContentBlockParam>();
                foreach (var tu in toolUses)
                {
                    var output = await _tools.ExecuteAsync(tu.Name, tu.Input, ct);
                    results.Add(ToResult(tu.ID, output));
                }
                _history.Add(new BetaUserMessageParam { Content = results });
            }
            return "Parei aqui: foram passos demais para um pedido só.";
        }
        catch
        {
            // Um histórico pela metade (ex.: ferramenta sem resultado) quebraria o próximo pedido.
            _history.Clear();
            throw;
        }
    }

    static BetaToolResultBlockParam ToResult(string id, ToolOutput output)
    {
        if (output.Jpeg is { } jpeg)
        {
            return new BetaToolResultBlockParam
            {
                ToolUseID = id,
                Content = new List<Block>
                {
                    new BetaTextBlockParam { Text = output.Text },
                    new BetaImageBlockParam { Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(jpeg), MediaType = MediaType.ImageJpeg } },
                },
            };
        }
        return new BetaToolResultBlockParam { ToolUseID = id, Content = output.Text, IsError = output.IsError };
    }

    static string SystemPrompt(AppSettings s)
    {
        var folders = s.ProjectFolderList();
        var prompt = $"""
            Você é o assistente da Island, uma "ilha" flutuante no topo da tela do Windows do usuário. Você age no computador dele através das ferramentas disponíveis.

            Como responder:
            - SEMPRE no idioma: {Loc.T("prompt.language")}. Curto (uma ou duas frases), sem markdown: a resposta aparece numa pílula pequena e pode ser lida em voz alta.
            - Os pedidos chegam muitas vezes por voz, então podem ter erros de transcrição ("ilha" pode ser o projeto "Island", por exemplo). Interprete com bom senso.
            - Depois de agir, diga o resultado de forma concreta (ex.: "Compilou sem erros." ou o erro principal em poucas palavras).

            Como agir:
            - Para projetos, use find_projects para achar o caminho real antes de abrir ou compilar; não invente caminhos. Se houver mais de um candidato, escolha o mais provável e diga qual usou.
            - Para compilar, testar ou rodar, veja os arquivos do projeto (package.json, .csproj, etc.) e use o comando adequado no diretório do projeto. Servidores de desenvolvimento e comandos que não terminam sozinhos devem ir num terminal visível (visible=true).
            - Para entender o que está acontecendo no PC, use get_system_status e list_windows; use look_at_screen só quando o pedido for sobre o conteúdo da tela.
            - Volume de UM app (Spotify, Discord, um jogo): set_app_volume. Volume geral do PC: set_volume.
            - Você TEM acesso à internet: para notícias, resultados de eleições/jogos, preços, clima, cotações ou qualquer fato atual, use web_search (e fetch_page para detalhes) e responda com os dados encontrados, citando a fonte. Nunca diga que não tem acesso a informações em tempo real.
            - Abas do navegador: browser_tab. Para "fecha tudo menos X e Y" use action=close_others com keep="X, Y" (uma chamada só). Use action=list para ver as abas. Responda com base na lista que a ferramenta devolve; nunca diga que fechou o que ela não fechou.
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
            - O app pede a aprovação do usuário antes de comandos; se ele negar, não tente contornar e só avise.

            Contexto:
            - Usuário do Windows: {Environment.UserName}
            - Editor preferido: {(string.IsNullOrWhiteSpace(s.PreferredEditor) ? "não definido" : s.PreferredEditor)}
            - Pastas de projetos: {(folders.Length == 0 ? "nenhuma configurada" : string.Join("; ", folders))}
            """;
        if (!string.IsNullOrWhiteSpace(s.ExtraInstructions))
            prompt += "\n\nSobre o usuário (escrito por ele):\n" + s.ExtraInstructions.Trim();
        if (s.Memories.Count > 0)
            prompt += "\n\nO que você já aprendeu sobre o usuário (memória):\n- " + string.Join("\n- ", s.Memories);
        return prompt;
    }
}
