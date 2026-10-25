using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Island;

// Jogos instalados na Steam e na Epic Games, para abrir pelo nome ou pela sigla ("cs2", "gta 5", "r6").
public static class GameLibrary
{
    public sealed record Game(string Name, string Store, string LaunchUri);

    static List<Game>? _cache;
    static DateTime _cacheAt;

    public static List<Game> Installed()
    {
        if (_cache != null && DateTime.Now - _cacheAt < TimeSpan.FromMinutes(5)) return _cache;
        var games = new List<Game>();
        try { games.AddRange(Steam()); } catch { }
        try { games.AddRange(Epic()); } catch { }
        _cache = games;
        _cacheAt = DateTime.Now;
        return games;
    }

    static IEnumerable<Game> Steam()
    {
        var root = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
        if (root == null) yield break;
        var libraries = new List<string> { root };
        var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            libraries.AddRange(Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"").Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));

        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var manifest in Directory.GetFiles(apps, "appmanifest_*.acf"))
            {
                var text = File.ReadAllText(manifest);
                var id = Regex.Match(text, "\"appid\"\\s+\"(\\d+)\"").Groups[1].Value;
                var name = Regex.Match(text, "\"name\"\\s+\"([^\"]+)\"").Groups[1].Value;
                if (id.Length == 0 || name.Length == 0 || name.Contains("Redistributable") || name.StartsWith("Steamworks")) continue;
                yield return new Game(name, "Steam", "steam://rungameid/" + id);
            }
        }
    }

    static IEnumerable<Game> Epic()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.GetFiles(dir, "*.item"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var r = doc.RootElement;
            string Get(string k) => r.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
            var name = Get("DisplayName");
            if (name.Length == 0) continue;
            var uri = $"com.epicgames.launcher://apps/{Get("CatalogNamespace")}%3A{Get("CatalogItemId")}%3A{Get("AppName")}?action=launch&silent=true";
            yield return new Game(name, "Epic Games", uri);
        }
    }

    // Melhor jogo para o que foi dito. strict = só nome exato ou sigla (para não confundir com apps).
    public static Game? Match(string query, bool strict)
    {
        // "gta v" → "gta5": algarismos romanos soltos viram números, igual às siglas.
        var q = Norm(Regex.Replace(Fold(query), @"\b(ii|iii|iv|v|vi)\b", w => w.Value switch { "ii" => "2", "iii" => "3", "iv" => "4", "v" => "5", _ => "6" }));
        if (q.Length < 2) return null;
        if (Aliases.TryGetValue(q, out var alias))
            return Installed().FirstOrDefault(g => Norm(g.Name).Contains(alias));
        Game? best = null;
        int bestScore = int.MaxValue;
        foreach (var g in Installed())
        {
            var name = Norm(g.Name);
            var acronym = Acronym(g.Name);
            int score =
                name == q ? 0 :
                acronym == q || (q.Length >= 3 && acronym.StartsWith(q)) || (q.Any(char.IsDigit) && acronym.StartsWith(q)) ? 1 :
                name.StartsWith(q) && q.Length >= 3 ? 2 :
                name.Contains(q) && q.Length >= 4 ? 3 :
                int.MaxValue;
            if (strict && score > 2) continue;
            if (score < bestScore) { best = g; bestScore = score; }
        }
        return best;
    }

    public static void Launch(Game game) => Process.Start(new ProcessStartInfo(game.LaunchUri) { UseShellExecute = true });

    // "Counter-Strike 2" → "cs2"; "Grand Theft Auto V" → "gta5"; "Tom Clancy's Rainbow Six Siege" → "tcr6s".
    static string Acronym(string name)
    {
        var sb = new StringBuilder();
        foreach (var raw in Regex.Split(Fold(name), @"[^a-z0-9]+").Where(w => w.Length > 0))
        {
            var w = raw switch { "ii" => "2", "iii" => "3", "iv" => "4", "v" => "5", "vi" => "6", "six" => "6", "seis" => "6", _ => raw };
            sb.Append(char.IsDigit(w[0]) ? w : w[0].ToString());
        }
        return sb.ToString();
    }

    // Apelidos da comunidade que não saem das iniciais.
    static readonly Dictionary<string, string> Aliases = new()
    {
        ["r6"] = "rainbowsix", ["rainbow"] = "rainbowsix", ["csgo"] = "counterstrike", ["cs"] = "counterstrike",
        ["gta"] = "grandtheftauto", ["lol"] = "leagueoflegends", ["wow"] = "worldofwarcraft", ["cod"] = "callofduty",
    };

    static string Norm(string s) => Regex.Replace(Fold(s), "[^a-z0-9]", "");

    static string Fold(string s) => new(s.ToLowerInvariant().Normalize(NormalizationForm.FormD)
        .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
}
