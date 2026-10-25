using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Island;

// WhatsApp para Windows (app novo, WinUI + página web por dentro). Só a interface UIA3 enxerga o conteúdo dele.
// Regra de ouro: só digita a mensagem depois de CONFERIR que o campo diz "Digite uma mensagem para <contato>".
// O app expõe a mesma árvore repetida dezenas de vezes (varrer tudo leva ~4 s), então só usamos buscas que
// param no primeiro resultado e as linhas da lista de conversas.
public static class WhatsApp
{
    // O nome do campo de mensagem diz para quem ela vai.
    static readonly Regex ComposerName = new(@"^(?:Digite uma mensagem para|Type a message to|Escribe un mensaje (?:a|para)|Escribir un mensaje (?:a|para)) (.+)$", RegexOptions.IgnoreCase);

    public sealed record Result(bool Ok, string Text);

    sealed class Session : IDisposable
    {
        public readonly UIA3Automation Uia = new();
        public readonly AutomationElement Root;
        public readonly IntPtr Window;
        public Session(IntPtr window)
        {
            Window = window;
            Root = Uia.FromHandle(window);
        }
        public void Dispose() => Uia.Dispose();

        ConditionBase EditNamed(params string[] parts)
        {
            var name = Uia.PropertyLibrary.Element.Name;
            var any = parts.Select(p => (ConditionBase)new PropertyCondition(name, p, PropertyConditionFlags.MatchSubstring | PropertyConditionFlags.IgnoreCase)).ToArray();
            return Uia.ConditionFactory.ByControlType(ControlType.Edit).And(any.Length == 1 ? any[0] : new OrCondition(any));
        }

        public (AutomationElement? Element, string Chat) Composer()
        {
            var e = Root.FindFirstDescendant(EditNamed("mensagem para", "message to", "mensaje a", "mensaje para"));
            var m = ComposerName.Match(e == null ? "" : SafeName(e));
            return m.Success ? (e, m.Groups[1].Value.Trim()) : (null, "");
        }

        // Vazia, a busca se chama "Pesquisar ou começar uma nova conversa"; com texto dentro, fica sem nome.
        // Nos dois casos é o primeiro campo de texto da tela (antes do campo de mensagem).
        public AutomationElement? Search()
        {
            var named = Root.FindFirstDescendant(EditNamed("pesquisar", "search", "buscar"));
            if (named != null) return named;
            var first = Root.FindFirstDescendant(Uia.ConditionFactory.ByControlType(ControlType.Edit));
            return first != null && !ComposerName.IsMatch(SafeName(first)) ? first : null;
        }

        public AutomationElement[] ChatRows() =>
            Root.FindFirstDescendant(Uia.ConditionFactory.ByControlType(ControlType.DataGrid))?.FindAllChildren() ?? Array.Empty<AutomationElement>();
    }

    // Abre a conversa com o contato e confere. Devolve o campo de mensagem (ou o motivo de não ter dado certo).
    static async Task<(AutomationElement? Composer, string Chat, string Error)> OpenChatAsync(Session s, string contact, CancellationToken ct)
    {
        var want = BrowserTabs.Fold(contact);

        // Trava 1: o WhatsApp TEM que estar na frente antes de qualquer clique ou tecla. Sem isso, cliques e
        // teclas iriam para a janela que estiver por cima (ex.: o seu editor).
        if (!await BringToFrontAsync(s.Window, ct)) return (null, "", "Não consegui trazer o WhatsApp para a frente.");

        // Já está na conversa certa?
        var (composer, chat) = s.Composer();
        if (composer != null && SameContact(chat, want)) return (composer, chat, "");

        // Minimizado ou atrás de outra janela, o WhatsApp não desenha a tela (e a acessibilidade fica vazia).
        AutomationElement? search = null;
        for (int i = 0; i < 12 && search == null; i++)
        {
            if (i % 4 == 0) WindowTools.Focus(s.Window);
            await Task.Delay(i == 0 ? 0 : 250, ct);
            search = s.Search();
        }
        if (search == null) return (null, "", "Não achei a busca do WhatsApp.");
        if (!await ClickAsync(s.Window, search, ct)) return (null, "", "O WhatsApp saiu da frente. Nada foi feito.");
        if (!Front(s.Window)) return (null, "", "O WhatsApp saiu da frente. Nada foi feito.");
        WindowTools.PressKeys("ctrl+a");
        await WindowTools.TypeTextAsync(contact, ct);

        // Resultados chegam aos poucos: espera aparecer uma linha cujo nome COMEÇA com o contato.
        // (Grupos onde alguém só citou o nome também aparecem, mas começam com o nome do grupo.)
        AutomationElement? best = null;
        for (int i = 0; i < 25 && best == null; i++)
        {
            await Task.Delay(160, ct);
            best = s.ChatRows()
                .Where(e => RowName(e) is { Length: > 0 } n && BrowserTabs.Fold(n).StartsWith(want))
                .OrderBy(e => RowName(e).Length)
                .FirstOrDefault();
        }
        if (best == null)
        {
            ClearSearch(s.Window, search);
            return (null, "", $"Não achei \"{contact}\" nas conversas do WhatsApp.");
        }

        // Abre clicando no nome (à direita da foto: a foto abre o status).
        if (!await ClickAsync(s.Window, best, ct, 0.6)) return (null, "", "O WhatsApp saiu da frente. Nada foi feito.");

        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(150, ct);
            (composer, chat) = s.Composer();
            if (composer != null && SameContact(chat, want)) return (composer, chat, "");
        }
        ClearSearch(s.Window, search);
        return (null, chat, chat.Length > 0
            ? $"A conversa aberta é \"{chat}\", não \"{contact}\"."
            : $"Não consegui abrir a conversa com \"{contact}\".");
    }

    public static async Task<Result> OpenAsync(IntPtr window, string contact, CancellationToken ct)
    {
        using var s = new Session(window);
        var (composer, chat, error) = await OpenChatAsync(s, contact, ct);
        return composer == null ? new(false, error) : new(true, $"Conversa com \"{chat}\" aberta no WhatsApp.");
    }

    // dryRun: faz tudo, confere que o texto entrou no campo certo e apaga em vez de enviar (para testes).
    public static async Task<Result> SendAsync(IntPtr window, string contact, string message, CancellationToken ct, bool dryRun = false)
    {
        using var s = new Session(window);
        var want = BrowserTabs.Fold(contact);
        var (composer, chat, error) = await OpenChatAsync(s, contact, ct);
        if (composer == null) return new(false, error + " Nada foi enviado.");

        // Digita e PROVA que o texto entrou no campo de mensagem (o foco da acessibilidade não põe o cursor lá;
        // por isso clica de verdade no campo). Duas tentativas.
        bool typed = false;
        for (int attempt = 0; attempt < 2 && !typed; attempt++)
        {
            (composer, var chatNow) = s.Composer();
            if (composer == null || !SameContact(chatNow, want)) return new(false, "A conversa mudou antes de enviar. Nada foi enviado.");
            if (!await ClickAsync(s.Window, composer, ct)) return new(false, "O WhatsApp saiu da frente. Nada foi enviado.");
            if (attempt > 0 && !await ClearComposerAsync(s.Window, ct)) return new(false, "O WhatsApp saiu da frente. Nada foi enviado.");
            if (!Front(s.Window)) return new(false, "O WhatsApp saiu da frente. Nada foi enviado.");
            await WindowTools.TypeTextAsync(message, ct);
            await Task.Delay(250, ct);
            typed = ComposerHas(s, message);
        }
        if (!typed)
        {
            await ClearComposerAsync(s.Window, ct);
            return new(false, $"Abri a conversa com \"{chat}\", mas não consegui digitar no campo de mensagem. Nada foi enviado.");
        }

        if (dryRun)
        {
            await ClearComposerAsync(s.Window, ct);
            return new(true, $"[teste] O texto entrou no campo da conversa com \"{chat}\" e foi apagado sem enviar.");
        }

        if (!Front(s.Window)) return new(false, "O WhatsApp saiu da frente antes do Enter. A mensagem ficou digitada, sem enviar.");
        WindowTools.PressKeys("enter");
        // Enviada = o campo esvaziou.
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(200, ct);
            if (!ComposerHas(s, message)) return new(true, $"Enviado para \"{chat}\" no WhatsApp: \"{message}\"");
        }
        return new(false, $"Digitei a mensagem na conversa com \"{chat}\", mas ela não saiu (continua no campo). Confira o WhatsApp.");
    }

    static bool Front(IntPtr window) => WindowTools.IsForeground(window);

    static async Task<bool> BringToFrontAsync(IntPtr window, CancellationToken ct)
    {
        for (int i = 0; i < 4; i++)
        {
            if (Front(window)) return true;
            WindowTools.Focus(window);
            await Task.Delay(250, ct);
        }
        return Front(window);
    }

    // Trava 2: só clica se o WhatsApp está na frente E o ponto do clique pertence a ele.
    // Páginas web ignoram clique "teleportado": move, deixa o app ver o mouse chegar, e só então clica.
    static async Task<bool> ClickAsync(IntPtr window, AutomationElement e, CancellationToken ct, double xFraction = -1)
    {
        var r = e.BoundingRectangle;
        int x = xFraction > 0 ? r.Left + (int)(r.Width * xFraction) : r.Left + Math.Min(r.Width / 2, 200);
        int y = r.Top + r.Height / 2;
        if (!Front(window) || WindowTools.WindowAt(x, y) != window) return false;
        var (mx, my) = WindowTools.CursorPosition();
        WindowTools.MoveMouse(x, y);
        await Task.Delay(120, ct);
        if (!Front(window) || WindowTools.WindowAt(x, y) != window)
        {
            WindowTools.MoveMouse(mx, my);
            return false;
        }
        WindowTools.MouseClick();
        await Task.Delay(150, ct);
        WindowTools.MoveMouse(mx, my);
        return true;
    }

    static async Task<bool> ClearComposerAsync(IntPtr window, CancellationToken ct)
    {
        if (!Front(window)) return false;
        WindowTools.PressKeys("ctrl+a");
        await Task.Delay(60, ct);
        if (!Front(window)) return false;
        WindowTools.PressKeys("backspace");
        await Task.Delay(120, ct);
        return true;
    }

    // O texto do campo de mensagem contém o que digitamos?
    static bool ComposerHas(Session s, string message)
    {
        var (composer, _) = s.Composer();
        if (composer == null) return false;
        string text = "";
        try { text = composer.Patterns.Text.PatternOrDefault?.DocumentRange.GetText(4000) ?? ""; } catch { }
        if (text.Trim().Length == 0)
            try { text = composer.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault ?? ""; } catch { }
        var have = BrowserTabs.Fold(text);
        var want = BrowserTabs.Fold(message);
        return want.Length > 0 && have.Contains(want.Length > 40 ? want[..40] : want);
    }

    // "Vinicius Gomes" × conversa "Vinicius Gomes 🎮": o nome salvo começa com o que você disse (ou o contrário).
    static bool SameContact(string chat, string want)
    {
        var c = BrowserTabs.Fold(chat);
        return c.Length > 0 && want.Length > 0 && (c.StartsWith(want) || want.StartsWith(c));
    }

    // Linha da lista: "Mostrar status Vinicius Gomes 10:02 …" → "Vinicius Gomes 10:02 …" (o botão do status vem antes).
    static string RowName(AutomationElement e) =>
        Regex.Replace(SafeName(e), @"^(?:Mostrar status|View status|Ver estado|Ver status)\s+", "", RegexOptions.IgnoreCase);

    static string SafeName(AutomationElement e)
    {
        try { return e.Name ?? ""; }
        catch { return ""; }
    }

    static void ClearSearch(IntPtr window, AutomationElement search)
    {
        if (!Front(window)) return;
        try
        {
            search.Focus();
            WindowTools.PressKeys("ctrl+a");
            WindowTools.PressKeys("backspace");
            WindowTools.PressKeys("esc");
        }
        catch { }
    }
}
