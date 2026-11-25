using Autodesk.Revit.ApplicationServices;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RevitFlux.Plugin.UI;

/// <summary>
/// Lightweight runtime localization. Detects Revit UI language on first use
/// and returns translated strings by key. Unknown keys or missing languages
/// fall back to English. Add new strings by extending <see cref="Table"/>.
/// </summary>
internal static class Localization
{
    private static string? _cachedLocale;

    /// <summary>
    /// Two-letter locale code ("en", "it", "fr", "de", "es"). Detected from
    /// Revit's active <see cref="LanguageType"/> when available, with a
    /// <see cref="CultureInfo.CurrentUICulture"/> fallback.
    /// </summary>
    public static string Locale
    {
        get
        {
            if (_cachedLocale != null) return _cachedLocale;
            _cachedLocale = DetectLocale();
            return _cachedLocale;
        }
    }

    /// <summary>Translate a key to the current Revit UI language.</summary>
    public static string T(string key)
    {
        if (Table.TryGetValue(key, out var variants))
        {
            if (variants.TryGetValue(Locale, out var s)) return s;
            if (variants.TryGetValue("en", out var en)) return en;
        }
        return key;
    }

    /// <summary>Translate and format ({0}, {1}, ...). If the translated
    /// string has mismatched placeholders (locale error), returns the raw
    /// template instead of throwing — the support flow must never crash on
    /// a bad translation.</summary>
    public static string T(string key, params object?[] args)
    {
        var fmt = T(key);
        try { return string.Format(fmt, args); }
        catch (FormatException) { return fmt; }
    }

    private static string DetectLocale()
    {
        try
        {
            var app = RevitFluxApp.Instance?.UiApplication?.Application;
            if (app != null)
            {
                return app.Language switch
                {
                    LanguageType.Italian             => "it",
                    LanguageType.French              => "fr",
                    LanguageType.German              => "de",
                    LanguageType.Spanish             => "es",
                    LanguageType.English_USA         => "en",
                    LanguageType.English_GB          => "en",
                    _                                => "en"
                };
            }
        }
        catch { /* fall through */ }

        try
        {
            var iso = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName?.ToLowerInvariant();
            if (iso == "it" || iso == "fr" || iso == "de" || iso == "es") return iso;
        }
        catch { /* ignore */ }

        return "en";
    }

    // Keys: dot-separated namespaces. Values: per-locale string.
    // Missing locale -> fallback to "en". Missing key -> return the key itself.
    private static readonly Dictionary<string, Dictionary<string, string>> Table = new()
    {
        // ── Update checker ──────────────────────────────────────────────
        ["update.available_title"] = new()
        {
            ["en"] = "RevitFlux {0} is available",
            ["it"] = "È disponibile RevitFlux {0}",
        },
        ["update.available_detail"] = new()
        {
            ["en"] = "You are on {0}. Click Download to get the new release.",
            ["it"] = "Versione installata: {0}. Clicca Download per scaricare la nuova release.",
        },
        ["update.download_button"] = new()
        {
            ["en"] = "Download",
            ["it"] = "Scarica",
        },
        ["update.open_browser_failed"] = new()
        {
            ["en"] = "Unable to open the download link: {0}",
            ["it"] = "Impossibile aprire il link di download: {0}",
        },
    };
}
