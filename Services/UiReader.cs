using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace Island;

// Lê e clica em itens de outros programas pela acessibilidade do Windows (UI Automation),
// a mesma tecnologia que leitores de tela usam. Funciona na maioria dos apps (Discord, Chrome, VS Code…).
public static class UiReader
{
    static readonly ControlType[] Interesting =
    {
        ControlType.Button, ControlType.ListItem, ControlType.TreeItem, ControlType.Hyperlink, ControlType.MenuItem,
        ControlType.TabItem, ControlType.Edit, ControlType.CheckBox, ControlType.ComboBox, ControlType.Text, ControlType.Document,
    };

    static string Kind(ControlType t) =>
        t == ControlType.Button ? "botão" : t == ControlType.ListItem ? "item" : t == ControlType.TreeItem ? "item" :
        t == ControlType.Hyperlink ? "link" : t == ControlType.MenuItem ? "menu" : t == ControlType.TabItem ? "aba" :
        t == ControlType.Edit ? "campo" : t == ControlType.CheckBox ? "caixa" : t == ControlType.ComboBox ? "lista" : "texto";

    // Lista o que dá para ver/clicar na janela (limitado, para não ficar enorme).
    public static string Read(IntPtr hwnd, int max = 150)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var seen = new HashSet<string>();
        var sb = new StringBuilder();
        int count = 0;
        Walk(root, 0, el =>
        {
            var name = SafeName(el);
            if (name.Length == 0 || name.Length > 120) return;
            var type = el.Current.ControlType;
            if (!Interesting.Contains(type)) return;
            var line = $"[{Kind(type)}] {name}";
            if (!seen.Add(line)) return;
            sb.AppendLine(line);
            count++;
        }, () => count >= max);
        return count == 0 ? "(não consegui ler os itens desta janela)" : sb.ToString();
    }

    // Clica no item cujo nome mais parece com o texto pedido.
    public static string? Click(IntPtr hwnd, string target)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var q = Norm(target);
        AutomationElement? best = null;
        int bestScore = int.MaxValue;
        Walk(root, 0, el =>
        {
            var name = Norm(SafeName(el));
            if (name.Length == 0) return;
            int score = name == q ? 0 : name.StartsWith(q) ? 1 : name.Contains(q) ? 2 : int.MaxValue;
            if (score == int.MaxValue) return;
            var type = el.Current.ControlType;
            if (type == ControlType.Text) score += 1; // prefere botões/itens a textos soltos
            if (score < bestScore) { best = el; bestScore = score; }
        }, () => bestScore == 0);

        if (best == null) return null;
        var label = SafeName(best);
        try
        {
            if (best.TryGetCurrentPattern(InvokePattern.Pattern, out var inv)) { ((InvokePattern)inv).Invoke(); return label; }
            if (best.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sel)) { ((SelectionItemPattern)sel).Select(); return label; }
            if (best.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ec)) { ((ExpandCollapsePattern)ec).Expand(); return label; }
        }
        catch { }

        // Sem "clique programático": clica no meio do item com o mouse.
        var r = best.Current.BoundingRectangle;
        if (r.IsEmpty || double.IsInfinity(r.Width)) return null;
        WindowTools.Focus(hwnd);
        WindowTools.Click((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
        return label;
    }

    static void Walk(AutomationElement root, int depth, Action<AutomationElement> visit, Func<bool> stop)
    {
        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<(AutomationElement El, int Depth)>();
        queue.Enqueue((root, 0));
        int visited = 0;
        while (queue.Count > 0 && visited < 4000 && !stop())
        {
            var (el, d) = queue.Dequeue();
            visited++;
            try { visit(el); } catch { }
            if (d >= 30) continue;
            try
            {
                var child = walker.GetFirstChild(el);
                while (child != null)
                {
                    queue.Enqueue((child, d + 1));
                    child = walker.GetNextSibling(child);
                }
            }
            catch { }
        }
    }

    static string SafeName(AutomationElement el)
    {
        try { return (el.Current.Name ?? "").Trim(); } catch { return ""; }
    }

    static string Norm(string s) => new string(s.ToLowerInvariant().Normalize(NormalizationForm.FormD)
        .Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();
}
