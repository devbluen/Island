using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace Island;

// Lê e seleciona as abas do navegador pela acessibilidade do Windows (Chrome, Edge, Brave, Opera, Firefox).
public static class BrowserTabs
{
    public sealed record Tab(AutomationElement Element, string Name, bool Selected, string Class);

    public static List<Tab> List(IntPtr browser)
    {
        var root = AutomationElement.FromHandle(browser);
        var items = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
        var tabs = new List<Tab>();
        foreach (AutomationElement el in items)
        {
            if (InsidePage(el, root)) continue; // abas desenhadas DENTRO de um site não contam
            bool selected = el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p) && ((SelectionItemPattern)p).Current.IsSelected;
            tabs.Add(new Tab(el, el.Current.Name, selected, el.Current.ClassName));
        }
        // Chrome/Edge/Brave/Opera: as abas de verdade são da classe "Tab"; balões (ex.: "Traduzir esta página?
        // inglês | português") também têm "abas" e não podem entrar na conta.
        if (tabs.Any(t => t.Class == "Tab")) tabs = tabs.Where(t => t.Class == "Tab").ToList();
        else tabs = tabs.Where(t => !t.Class.Contains("TabbedPane")).ToList();
        return tabs;
    }

    static bool InsidePage(AutomationElement el, AutomationElement root)
    {
        var walker = TreeWalker.ControlViewWalker;
        for (var parent = walker.GetParent(el); parent != null && parent != root; parent = walker.GetParent(parent))
            if (parent.Current.ControlType == ControlType.Document) return true;
        return false;
    }

    public static void Select(Tab tab)
    {
        if (tab.Element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p))
        {
            ((SelectionItemPattern)p).Select();
            return;
        }
        // Sem o padrão de seleção: clica no meio da aba e devolve o mouse para onde estava.
        var r = tab.Element.Current.BoundingRectangle;
        var (x, y) = WindowTools.CursorPosition();
        WindowTools.Click((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
        WindowTools.MoveMouse(x, y);
    }

    // "apuração de 2026" casa com "Apuração 2026 - g1": todas as palavras importantes do pedido aparecem no título.
    public static bool Matches(string title, string query)
    {
        var t = Fold(title);
        var words = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !Stop.Contains(w)).ToList();
        if (words.Count == 0) return false;
        return words.All(w => t.Contains(w)) || t.Replace(" ", "").Contains(string.Concat(words));
    }

    static readonly HashSet<string> Stop = new()
    {
        "a", "o", "as", "os", "de", "do", "da", "dos", "das", "e", "aba", "abas", "guia", "guias", "site", "pagina",
        "the", "of", "tab", "tabs", "page", "and", "la", "el", "las", "los", "pestana", "pestanas", "y", "del",
    };

    public static string Fold(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
