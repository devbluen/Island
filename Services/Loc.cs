using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;

namespace Island;

// Idiomas da interface, da voz e das respostas.
// No código: Loc.T("chave") / Loc.F("chave", args). Nas telas: {DynamicResource L.chave}.
public static class Loc
{
    public static readonly (string Code, string Name)[] Languages = { ("pt", "Português"), ("en", "English"), ("es", "Español") };

    public static string Lang { get; private set; } = "pt";

    public static event Action? Changed;

    public static CultureInfo Culture => Lang switch
    {
        "en" => CultureInfo.GetCultureInfo("en-US"),
        "es" => CultureInfo.GetCultureInfo("es-ES"),
        _ => CultureInfo.GetCultureInfo("pt-BR"),
    };

    // Idioma do Windows, para a primeira vez que o app abre.
    public static string SystemLanguage() => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
    {
        "pt" => "pt",
        "es" => "es",
        _ => "en",
    };

    public static void Apply(string lang)
    {
        Lang = lang is "en" or "es" ? lang : "pt";
        var res = Application.Current?.Resources;
        if (res != null)
            foreach (var key in Strings.Table.Keys)
                res["L." + key] = T(key);
        Changed?.Invoke();
    }

    public static string T(string key)
    {
        if (!Strings.Table.TryGetValue(key, out var v)) return key;
        return Lang switch { "en" => v.En, "es" => v.Es, _ => v.Pt };
    }

    public static string F(string key, params object?[] args) => string.Format(Culture, T(key), args);
}
