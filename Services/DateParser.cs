using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Island;

// Entende datas e horários escritos/falados em qualquer parte da frase:
// "segunda às 18h", "amanhã 9:30", "dia 30", "30 de outubro", "daqui a 20 minutos", "todo dia às 7h",
// "tomorrow at 6pm", "next monday 9am", "mañana a las 7", "el lunes a las 18:00"…
public static class DateParser
{
    // HasDay: a frase citava um dia (amanhã, segunda, dia 30…). Week: falava da semana ("essa semana").
    public sealed record Result(string Text, DateTime? When, bool Alarm, bool Daily, bool HasDay = false, bool HasTime = false, bool Week = false);

    sealed record Lang(
        string[] Weekdays, string[] Months, string Today, string Tomorrow, string AfterTomorrow,
        string Relative, string Units, string TimeA, string TimeB, string TimeC, string Noon, string Midnight,
        string Morning, string Afternoon, string Alarm, string Daily, string Fillers);

    // Dias da semana começando no domingo (igual ao DayOfWeek).
    static readonly Lang Pt = new(
        Weekdays: new[] { "domingo", "segunda", "terca", "quarta", "quinta", "sexta", "sabado" },
        Months: new[] { "janeiro", "fevereiro", "marco", "abril", "maio", "junho", "julho", "agosto", "setembro", "outubro", "novembro", "dezembro" },
        Today: @"\bhoje\b", Tomorrow: @"\bamanha\b", AfterTomorrow: @"\bdepois de amanha\b",
        Relative: @"\b(?:daqui a|daqui|em|dentro de)\s+(\d+|um|uma|dois|duas|tres|meia)\s+(minutos?|min|horas?|h|dias?)\b",
        Units: "min|h|dia",
        TimeA: @"\b(?:as|a|pelas|para as|pra as|ate as)?\s*(\d{1,2})(?::|h)(\d{2})\b",
        TimeB: @"\b(?:as|pelas|para as|pra as|ate as)\s+(\d{1,2})(?:\s*h(?:oras?)?|\s*horas?)?(?:\s+e\s+(\d{1,2}|meia))?\b",
        TimeC: @"\b(\d{1,2})\s*(?:h|horas)\b",
        Noon: @"\bmeio[ -]dia\b", Midnight: @"\bmeia[ -]noite\b",
        Morning: @"\b(?:da|de) (?:manha|madrugada)\b", Afternoon: @"\b(?:da|de) (?:tarde|noite)\b",
        Alarm: @"\b(?:alarme|despertador)\b", Daily: @"\b(?:todo dia|todos os dias|diariamente|toda manha|toda noite)\b",
        Fillers: @"^(?:que|de|pra|para|a|o|e|eu tenho|tenho|eu vou ter|vou ter)\b\s*");

    static readonly Lang En = new(
        Weekdays: new[] { "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" },
        Months: new[] { "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december" },
        Today: @"\b(?:today|tonight)\b", Tomorrow: @"\btomorrow\b", AfterTomorrow: @"\bthe day after tomorrow\b",
        Relative: @"\b(?:in|within)\s+(\d+|a|an|one|two|three|half an?)\s+(minutes?|mins?|hours?|hrs?|days?)\b",
        Units: "min|h|day",
        TimeA: @"\b(?:at\s+)?(\d{1,2}):(\d{2})\s*(am|pm|a\.m\.|p\.m\.)?\b",
        TimeB: @"\bat\s+(\d{1,2})\s*(am|pm|a\.m\.|p\.m\.|o'?clock)?\b",
        TimeC: @"\b(\d{1,2})\s*(am|pm|a\.m\.|p\.m\.)\b",
        Noon: @"\bnoon\b", Midnight: @"\bmidnight\b",
        Morning: @"\bin the morning\b", Afternoon: @"\b(?:in the (?:afternoon|evening)|at night)\b",
        Alarm: @"\b(?:alarm|wake me up|wake me)\b", Daily: @"\b(?:every day|everyday|daily|every morning|every night)\b",
        Fillers: @"^(?:to|that|about|for|on|at|i have|i've got|i got)\b\s*");

    static readonly Lang Es = new(
        Weekdays: new[] { "domingo", "lunes", "martes", "miercoles", "jueves", "viernes", "sabado" },
        Months: new[] { "enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre" },
        Today: @"\bhoy\b", Tomorrow: @"\bmanana\b(?! de la)", AfterTomorrow: @"\bpasado manana\b",
        Relative: @"\b(?:en|dentro de)\s+(\d+|un|una|dos|tres|media)\s+(minutos?|min|horas?|h|dias?)\b",
        Units: "min|h|dia",
        TimeA: @"\b(?:a las|a la)?\s*(\d{1,2}):(\d{2})\b",
        TimeB: @"\b(?:a las|a la)\s+(\d{1,2})(?:\s*h(?:oras)?)?(?:\s+y\s+(\d{1,2}|media))?\b",
        TimeC: @"\b(\d{1,2})\s*(?:h|horas)\b",
        Noon: @"\bmediodia\b", Midnight: @"\bmedianoche\b",
        Morning: @"\bde la (?:manana|madrugada)\b", Afternoon: @"\bde la (?:tarde|noche)\b",
        Alarm: @"\b(?:alarma|despertador)\b", Daily: @"\b(?:todos los dias|cada dia|diariamente)\b",
        Fillers: @"^(?:que|de|para|a|el|la|y|tengo)\b\s*");

    static Lang For(string lang) => lang switch { "en" => En, "es" => Es, _ => Pt };

    static int NumberWord(string w) => w switch
    {
        "um" or "uma" or "un" or "a" or "an" or "one" => 1,
        "dois" or "duas" or "dos" or "two" => 2,
        "tres" or "three" => 3,
        _ => int.TryParse(w, out int n) ? n : 1,
    };

    public static Result Parse(string input, string lang, DateTime now)
    {
        var L = For(lang);
        var text = input;
        var n = Fold(text);
        var cut = new bool[n.Length]; // trechos de data/hora, que saem do texto final

        Match? Take(string pattern)
        {
            var m = Regex.Match(n, pattern, RegexOptions.IgnoreCase);
            if (!m.Success || m.Length == 0) return null;
            for (int i = m.Index; i < m.Index + m.Length; i++) cut[i] = true;
            return m;
        }

        bool week = Take(lang switch
        {
            "en" => @"\b(?:this|next) week\b",
            "es" => @"\b(?:esta|la proxima|proxima) semana\b",
            _ => @"\b(?:essa|esta|nessa|nesta|na proxima|proxima) semana\b",
        }) != null;
        bool alarm = Take(L.Alarm) != null;
        bool daily = Take(L.Daily) != null;

        // "daqui a 20 minutos"
        TimeSpan? relative = null;
        if (Take(L.Relative) is { } rel)
        {
            var amountWord = rel.Groups[1].Value;
            double amount = amountWord.StartsWith("mei") || amountWord.StartsWith("half") || amountWord == "media" ? 0.5 : NumberWord(amountWord);
            var unit = rel.Groups[2].Value;
            if (amountWord.StartsWith("mei") || amountWord.StartsWith("half") || amountWord == "media") unit = "h";
            relative = unit.StartsWith("m") ? TimeSpan.FromMinutes(amount)
                : unit.StartsWith("h") ? TimeSpan.FromHours(amount)
                : TimeSpan.FromDays(amount);
        }

        // Dia
        int? dayOffset = null;
        if (Take(L.AfterTomorrow) != null) dayOffset = 2;
        else if (Take(L.Tomorrow) != null) dayOffset = 1;
        else if (Take(L.Today) != null) dayOffset = 0;

        DayOfWeek? weekday = null;
        bool nextWeek = false;
        for (int d = 0; d < 7 && weekday == null; d++)
        {
            var name = L.Weekdays[d];
            var pattern = lang switch
            {
                "en" => $@"\b(?:on |this |next )?{name}\b",
                "es" => $@"\b(?:el |este |el proximo |proximo )?{name}\b",
                _ => $@"\b(?:na |no |nesta |neste |na proxima |no proximo |proxima |proximo |essa |esse )?{name}(?:-feira| feira)?\b",
            };
            if (Take(pattern) is { } wm)
            {
                weekday = (DayOfWeek)d;
                nextWeek = wm.Value.Contains("next") || wm.Value.Contains("proxim");
            }
        }

        // Data: 30/10, 30/10/2026, "30 de outubro", "dia 30", "october 30", "the 30th"
        int? day = null, month = null, year = null;
        var monthsAlt = string.Join("|", L.Months);
        if (Take(@"\b(\d{1,2})/(\d{1,2})(?:/(\d{2,4}))?\b") is { } dm)
        {
            int a = int.Parse(dm.Groups[1].Value), b = int.Parse(dm.Groups[2].Value);
            (day, month) = lang == "en" ? (b, a) : (a, b); // em inglês é mês/dia
            if (dm.Groups[3].Success) year = int.Parse(dm.Groups[3].Value) is int y && y < 100 ? 2000 + y : int.Parse(dm.Groups[3].Value);
        }
        else if (Take($@"\b(?:dia |el dia |the )?(\d{{1,2}})(?:st|nd|rd|th)?\s+(?:de |of )?({monthsAlt})\b") is { } dm2)
        {
            day = int.Parse(dm2.Groups[1].Value);
            month = Array.IndexOf(L.Months, dm2.Groups[2].Value) + 1;
        }
        else if (Take($@"\b({monthsAlt})\s+(\d{{1,2}})(?:st|nd|rd|th)?\b") is { } dm3)
        {
            month = Array.IndexOf(L.Months, dm3.Groups[1].Value) + 1;
            day = int.Parse(dm3.Groups[2].Value);
        }
        else if (Take(@"\b(?:dia|el dia|on the|the)\s+(\d{1,2})(?:st|nd|rd|th)?\b") is { } dm4)
        {
            day = int.Parse(dm4.Groups[1].Value);
        }

        // Hora
        int? hour = null, minute = null;
        string ampm = "";
        if (Take(L.Noon) != null) { hour = 12; minute = 0; }
        else if (Take(L.Midnight) != null) { hour = 0; minute = 0; }
        else if (Take(L.TimeA) is { } ta)
        {
            hour = int.Parse(ta.Groups[1].Value);
            minute = int.Parse(ta.Groups[2].Value);
            if (ta.Groups.Count > 3) ampm = ta.Groups[3].Value;
        }
        else if (Take(L.TimeB) is { } tb)
        {
            hour = int.Parse(tb.Groups[1].Value);
            var g2 = tb.Groups[2].Value;
            if (lang == "en") ampm = g2;
            else minute = g2 is "meia" or "media" ? 30 : int.TryParse(g2, out int mm) ? mm : 0;
        }
        else if (Take(L.TimeC) is { } tc)
        {
            hour = int.Parse(tc.Groups[1].Value);
            if (lang == "en" && tc.Groups.Count > 2) ampm = tc.Groups[2].Value;
        }

        if (hour != null)
        {
            ampm = ampm.Replace(".", "").ToLowerInvariant();
            if (Take(L.Afternoon) != null && hour < 12) hour += 12;
            else Take(L.Morning);
            if (ampm == "pm" && hour < 12) hour += 12;
            if (ampm == "am" && hour == 12) hour = 0;
            if (hour > 23 || minute > 59) hour = minute = null;
        }

        // Monta a data final
        DateTime? when = null;
        if (relative is TimeSpan r)
        {
            when = now + r;
        }
        else if (day != null || dayOffset != null || weekday != null || hour != null || daily)
        {
            var date = now.Date;
            if (day is int dd)
            {
                int mo = month ?? now.Month, yr = year ?? now.Year;
                try
                {
                    date = new DateTime(yr, mo, Math.Min(dd, DateTime.DaysInMonth(yr, mo)));
                    if (date < now.Date && year == null) date = month == null ? date.AddMonths(1) : date.AddYears(1);
                }
                catch { date = now.Date; }
            }
            else if (dayOffset is int off)
            {
                date = now.Date.AddDays(off);
            }
            else if (weekday is DayOfWeek wd)
            {
                int ahead = ((int)wd - (int)now.DayOfWeek + 7) % 7;
                if (nextWeek && ahead == 0) ahead = 7;
                date = now.Date.AddDays(ahead);
            }

            var time = hour != null ? new TimeSpan(hour.Value, minute ?? 0, 0) : TimeSpan.FromHours(9); // só o dia: 9h
            var candidate = date + time;
            // Só o horário (ou "segunda" que já passou hoje): vai para a próxima vez.
            if (candidate <= now)
            {
                if (weekday != null && day == null && dayOffset == null) candidate = candidate.AddDays(7);
                else if (day == null && dayOffset == null) candidate = candidate.AddDays(1);
            }
            when = candidate;
        }

        // O que sobrou é o texto da tarefa
        var sb = new StringBuilder();
        for (int i = 0; i < text.Length; i++) sb.Append(cut[i] ? ' ' : text[i]);
        var rest = Regex.Replace(sb.ToString(), @"\s+", " ");
        rest = Regex.Replace(rest, @"\s+([,.;!?])", "$1");
        rest = rest.Trim(' ', ',', '.', ';', ':', '-');
        for (int i = 0; i < 3; i++)
        {
            var f = Regex.Match(Fold(rest), L.Fillers, RegexOptions.IgnoreCase);
            if (!f.Success || f.Length == 0) break;
            rest = rest[f.Length..].Trim(' ', ',', '.', ';', ':', '-');
        }
        // Preposição que ficou sozinha no fim ("almoço com a Ana ao", "pay rent on").
        for (int i = 0; i < 2; i++)
            rest = Regex.Replace(rest, @"\s+(?:ao|aos|a|as|à|às|no|na|em|de|do|da|para|pra|on|at|in|for|by|el|la|al|en|del)$", "", RegexOptions.IgnoreCase);
        rest = Regex.Replace(rest, @"^[,\s]+|[,\s]+$", "");
        if (rest.Length > 0) rest = char.ToUpper(rest[0]) + rest[1..];

        return new Result(rest, when, alarm, daily, HasDay: day != null || dayOffset != null || weekday != null, HasTime: hour != null, Week: week);
    }

    // "seg., 27/10 18:00" / "amanhã 07:00" / "hoje 15:30"
    public static string Label(DateTime when, DateTime now)
    {
        var c = Loc.Culture;
        if (when.Date == now.Date) return Loc.T("date.today") + " " + when.ToString("HH:mm");
        if (when.Date == now.Date.AddDays(1)) return $"{Loc.T("date.tomorrow")} ({when.ToString("dddd", c).Split('-')[0]}) {when:HH:mm}";
        if (when.Date < now.Date.AddDays(7)) return when.ToString("ddd HH:mm", c);
        return when.ToString("ddd dd/MM HH:mm", c);
    }

    // Remove acentos mantendo o tamanho (para recortar o texto original nas mesmas posições).
    static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(char.ToLowerInvariant(ch.ToString().Normalize(NormalizationForm.FormD)[0]));
        return sb.ToString();
    }
}
