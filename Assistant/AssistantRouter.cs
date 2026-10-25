using System;
using System.Threading;
using System.Threading.Tasks;

namespace Island;

// Decide quem atende o pedido: primeiro os comandos prontos; se não reconhecer, a IA escolhida.
public sealed class AssistantRouter
{
    readonly IAssistantHost _host;
    readonly CommandRouter _commands;
    readonly ClaudeAssistant _claude;
    readonly OllamaAssistant _local;

    public AssistantRouter(IAssistantHost host)
    {
        _host = host;
        var tools = new AgentTools(host);
        _commands = new CommandRouter(host, tools);
        _claude = new ClaudeAssistant(host, tools);
        _local = new OllamaAssistant(host, tools);
    }

    public void Reset()
    {
        _claude.Reset();
        _local.Reset();
    }

    public async Task<string> AskAsync(string text, Action<string> onNote, CancellationToken ct)
    {
        var provider = _host.Settings.Provider;
        var quick = await _commands.TryHandleAsync(text, canFallback: provider != "none", ct);
        if (quick != null) return quick;

        return provider switch
        {
            "claude" => await _claude.AskAsync(text, onNote, ct),
            "local" => await _local.AskAsync(text, onNote, ct),
            _ => Loc.T("r.notUnderstood"),
        };
    }
}
