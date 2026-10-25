using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Island;

// Memória do assistente: fatos que você contou ("meu site de anti cheat é xxx.com"), guardados só neste PC
// (Settings.Memories, um por linha, editável em Configurações › Assistente).
// Fatos no formato "meu X é Y" também servem de atalho: "abre meu site de anti cheat" vira "abre xxx.com".
public static class UserMemory
{
    // "meu site de anti cheat é esse aqui xxx.com", "minha playlist de treino se chama Foco", "my editor is Cursor"
    static readonly Regex Statement = new(
        @"^(?:o |a |os |as |the |el |la )?(?:meu|minha|meus|minhas|my|mi|mis) (.{2,60}?) (?:é|eh|são|sao|se chama|chama|fica em|is|are|es|son|se llama|está en)(?: (?:esse aqui|essa aqui|este aqui|esse|essa|este|esta|isso|this one|this|o|a|el|la))?:? (.+)$",
        RegexOptions.IgnoreCase);

    public static (string Key, string Value)? Parse(string fact)
    {
        var m = Statement.Match(fact.Trim().TrimEnd('.', '!', ';'));
        if (!m.Success) return null;
        var value = m.Groups[2].Value.Trim().TrimEnd('.', '!', ',', ';');
        var key = Fold(m.Groups[1].Value);
        return value.Length == 0 || key.Length < 2 ? null : (key, value);
    }

    // Guarda (ou atualiza, se já existe um "meu X é …" com o mesmo X). Devolve o texto guardado.
    public static string Remember(List<string> memories, string fact)
    {
        fact = fact.Trim().TrimEnd('.', '!', ';');
        if (fact.Length > 0) fact = char.ToUpper(fact[0]) + fact[1..];
        if (Parse(fact) is { } kv)
            memories.RemoveAll(m => Parse(m)?.Key == kv.Key);
        memories.RemoveAll(m => Fold(m) == Fold(fact));
        memories.Add(fact);
        while (memories.Count > 200) memories.RemoveAt(0);
        return fact;
    }

    // Apaga os fatos que falam de "query". Devolve o que foi apagado.
    public static List<string> Forget(List<string> memories, string query)
    {
        var q = Regex.Replace(Fold(query), @"^(?:que |o |a |os |as |do |da |de |the |that |el |la |lo de )*(?:meu |minha |meus |minhas |my |mi |mis )?", "").Trim();
        if (q.Length < 2) return new();
        var removed = memories.Where(m => Fold(m).Contains(q)).ToList();
        memories.RemoveAll(removed.Contains);
        return removed;
    }

    // "abre meu site de anti cheat" → "abre xxx.com" (só para fatos no formato "meu X é Y").
    public static string Resolve(string text, IEnumerable<string> memories)
    {
        foreach (var kv in memories.Select(Parse).Where(p => p != null).Select(p => p!.Value).OrderByDescending(p => p.Key.Length))
        {
            // Mesmo tamanho do texto original (Fold não muda o comprimento), então os índices batem.
            var folded = CommandRouter.Fold(text.ToLowerInvariant());
            var key = Regex.Escape(kv.Key).Replace(@"\ ", @"\s+");
            var m = Regex.Match(folded, @"(?:\b(?:o|a|os|as|the|el|la) )?\b(?:meu|minha|meus|minhas|my|mi|mis) " + key + @"\b");
            if (!m.Success) continue;
            // "minha playlist de treino" → "playlist Foco Total" (mantém o tipo, senão o Spotify buscaria uma música).
            var head = kv.Key.Split(' ')[0];
            var value = Regex.Replace(kv.Value, @"^(?:esse aqui|essa aqui|este aqui|esse|essa|este|esta|isso|this one|this) ", "", RegexOptions.IgnoreCase);
            var replacement = TypeWords.Contains(head) ? head + " " + value : value;
            text = text[..m.Index] + replacement + text[(m.Index + m.Length)..];
        }
        return text;
    }

    static readonly HashSet<string> TypeWords = new() { "playlist", "album", "artista", "banda", "cantor", "cantora", "musica", "song", "artist", "band", "cancion", "pasta", "folder", "carpeta", "projeto", "project", "proyecto" };

    public static string Fold(string s) =>
        Regex.Replace(CommandRouter.Fold(s.ToLowerInvariant()), @"[^\p{L}\p{N}.:/@_-]+", " ").Trim();
}
