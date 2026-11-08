using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Game.SceneFlow;
using Newtonsoft.Json.Linq;

namespace AutoTranslate
{
    /// <summary>
    /// The interface languages: the game's twelve plus Arabic. English is the source text; Arabic lives in the code;
    /// the others are embedded JSON files (i18n/&lt;code&gt;.json, keyed by the English text, shared with the UI).
    /// A text missing from a file shows in English.
    /// </summary>
    internal static class UiLanguages
    {
        public const string English = "en";
        public const string Arabic = "ar";

        /// <summary>Every interface language, in the game's order (Arabic after English).</summary>
        public static readonly string[] Codes =
        {
            "en", "ar", "de", "es", "fr", "it", "ja", "ko", "pl", "pt-BR", "ru", "zh-HANS", "zh-HANT",
        };

        /// <summary>Each language's name in its own language, same order as <see cref="Codes"/>.</summary>
        public static readonly string[] NativeNames =
        {
            "English", "العربية", "Deutsch", "Español", "Français", "Italiano", "日本語", "한국어", "Polski",
            "Português (Brasil)", "Русский", "简体中文", "繁體中文",
        };

        private static readonly Dictionary<string, Dictionary<string, string>> s_Tables = new Dictionary<string, Dictionary<string, string>>();
        private static string s_Windows;

        /// <summary>The interface language for a game locale id or a culture name ("de-DE", "zh-HANS", "pt-BR", "ar-SA"); null when unsupported.</summary>
        public static string FromTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            var parts = tag.Trim().Split('-', '_');
            var language = parts[0].ToLowerInvariant();
            switch (language)
            {
                case "zh":
                    for (int i = 1; i < parts.Length; i++)
                    {
                        switch (parts[i].ToLowerInvariant())
                        {
                            case "hant": case "tw": case "hk": case "mo": case "cht": return "zh-HANT";
                        }
                    }
                    return "zh-HANS";
                case "pt":
                    return "pt-BR";
                default:
                    foreach (var code in Codes)
                        if (code == language) return code;
                    return null;
            }
        }

        /// <summary>The game's language, or English when the game is in a language the mod does not have.</summary>
        public static string Game()
        {
            string locale = null;
            try { locale = GameManager.instance?.localizationManager?.activeLocaleId; }
            catch (Exception) { /* no active dictionary yet */ }
            return FromTag(locale) ?? English;
        }

        /// <summary>Windows' display language, or English when it is one the mod does not have.</summary>
        public static string Windows()
        {
            if (s_Windows != null) return s_Windows;
            string found = null;
            try { found = FromTag(CultureInfo.CurrentUICulture?.Name); }
            catch (Exception) { /* culture unavailable */ }
            if (found == null)
            {
                try { found = FromSystemLanguage(UnityEngine.Application.systemLanguage); }
                catch (Exception) { /* not on the main thread */ }
            }
            s_Windows = found ?? English;
            return s_Windows;
        }

        private static string FromSystemLanguage(UnityEngine.SystemLanguage language)
        {
            switch (language)
            {
                case UnityEngine.SystemLanguage.Arabic: return "ar";
                case UnityEngine.SystemLanguage.German: return "de";
                case UnityEngine.SystemLanguage.Spanish: return "es";
                case UnityEngine.SystemLanguage.French: return "fr";
                case UnityEngine.SystemLanguage.Italian: return "it";
                case UnityEngine.SystemLanguage.Japanese: return "ja";
                case UnityEngine.SystemLanguage.Korean: return "ko";
                case UnityEngine.SystemLanguage.Polish: return "pl";
                case UnityEngine.SystemLanguage.Portuguese: return "pt-BR";
                case UnityEngine.SystemLanguage.Russian: return "ru";
                case UnityEngine.SystemLanguage.ChineseTraditional: return "zh-HANT";
                case UnityEngine.SystemLanguage.Chinese:
                case UnityEngine.SystemLanguage.ChineseSimplified: return "zh-HANS";
                case UnityEngine.SystemLanguage.English: return "en";
                default: return null;
            }
        }

        /// <summary><paramref name="english"/> in <paramref name="code"/> (not Arabic: Arabic texts are in the code); English when missing.</summary>
        public static string Translate(string code, string english)
        {
            if (string.IsNullOrEmpty(english) || string.IsNullOrEmpty(code) || code == English) return english;
            var table = Table(code);
            return table != null && table.TryGetValue(english, out var text) && !string.IsNullOrEmpty(text) ? text : english;
        }

        /// <summary>Replaces {name} placeholders: Format("{n} signs", "n", 3).</summary>
        public static string Format(string template, params object[] pairs)
        {
            if (string.IsNullOrEmpty(template)) return template;
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                template = template.Replace("{" + pairs[i] + "}", Convert.ToString(pairs[i + 1], CultureInfo.InvariantCulture));
            return template;
        }

        private static Dictionary<string, string> Table(string code)
        {
            lock (s_Tables)
            {
                if (s_Tables.TryGetValue(code, out var cached)) return cached;
                Dictionary<string, string> table = null;
                try
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("i18n." + code + ".json"))
                    {
                        if (stream != null)
                        {
                            using (var reader = new StreamReader(stream))
                            {
                                table = new Dictionary<string, string>(StringComparer.Ordinal);
                                foreach (var pair in JObject.Parse(reader.ReadToEnd()))
                                    if (pair.Value != null && pair.Value.Type == JTokenType.String) table[pair.Key] = (string)pair.Value;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Mod.Log.Warn(e, "Reading the " + code + " interface texts failed");
                }
                s_Tables[code] = table;
                return table;
            }
        }
    }
}
