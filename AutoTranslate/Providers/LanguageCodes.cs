using System;
using System.Collections.Generic;
using System.Globalization;
using AutoTranslate.Core;

namespace AutoTranslate.Providers
{
    /// <summary>
    /// Language codes used by Auto Translate. The mod's own code is ISO 639-1 lower-case ("ar", "es"), except
    /// Chinese ("zh-Hans", "zh-Hant") and Brazilian Portuguese ("pt-BR"); every engine has its own spelling, see
    /// <see cref="ForProvider"/>. Thread-safe (all data is immutable after the static constructor).
    /// </summary>
    public static class LanguageCodes
    {
        /// <summary>Source language value meaning "let the engine detect it".</summary>
        public const string Auto = "auto";

        private enum Script
        {
            Latin,
            Arabic,
            Hebrew,
            Cyrillic,
            Greek,
            Han,
            Japanese,
            Hangul,
            Thai,
            Devanagari,
            Bengali,
        }

        private sealed class Language
        {
            public string Iso;
            public string NativeName;
            public Script Script;
            public string Google;
            public string DeepL;
            public string Microsoft;
            public string Libre;
        }

        private static readonly Dictionary<string, Language> ByIso = new Dictionary<string, Language>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> SupportedList = new List<string>();
        private static readonly IReadOnlyList<string> SupportedReadOnly;

        static LanguageCodes()
        {
            //   iso       native name             script               Google   DeepL      Microsoft  LibreTranslate
            Add("ar", "العربية", Script.Arabic, "ar", "AR", "ar", "ar");
            Add("en", "English", Script.Latin, "en", "EN-US", "en", "en");
            Add("es", "Español", Script.Latin, "es", "ES", "es", "es");
            Add("fr", "Français", Script.Latin, "fr", "FR", "fr", "fr");
            Add("de", "Deutsch", Script.Latin, "de", "DE", "de", "de");
            Add("it", "Italiano", Script.Latin, "it", "IT", "it", "it");
            Add("pt", "Português (Portugal)", Script.Latin, "pt-PT", "PT-PT", "pt-pt", "pt");
            Add("pt-BR", "Português (Brasil)", Script.Latin, "pt", "PT-BR", "pt", "pt");
            Add("ru", "Русский", Script.Cyrillic, "ru", "RU", "ru", "ru");
            Add("uk", "Українська", Script.Cyrillic, "uk", "UK", "uk", "uk");
            Add("pl", "Polski", Script.Latin, "pl", "PL", "pl", "pl");
            Add("nl", "Nederlands", Script.Latin, "nl", "NL", "nl", "nl");
            Add("tr", "Türkçe", Script.Latin, "tr", "TR", "tr", "tr");
            Add("zh-Hans", "简体中文", Script.Han, "zh-CN", "ZH-HANS", "zh-Hans", "zh");
            Add("zh-Hant", "繁體中文", Script.Han, "zh-TW", "ZH-HANT", "zh-Hant", "zt");
            Add("ja", "日本語", Script.Japanese, "ja", "JA", "ja", "ja");
            Add("ko", "한국어", Script.Hangul, "ko", "KO", "ko", "ko");
            Add("hi", "हिन्दी", Script.Devanagari, "hi", null, "hi", "hi");
            Add("id", "Bahasa Indonesia", Script.Latin, "id", "ID", "id", "id");
            Add("ms", "Bahasa Melayu", Script.Latin, "ms", null, "ms", "ms");
            Add("th", "ไทย", Script.Thai, "th", "TH", "th", "th");
            Add("vi", "Tiếng Việt", Script.Latin, "vi", "VI", "vi", "vi");
            Add("fa", "فارسی", Script.Arabic, "fa", null, "fa", "fa");
            Add("he", "עברית", Script.Hebrew, "iw", "HE", "he", "he");
            Add("ur", "اردو", Script.Arabic, "ur", null, "ur", "ur");
            Add("bn", "বাংলা", Script.Bengali, "bn", null, "bn", "bn");
            Add("sv", "Svenska", Script.Latin, "sv", "SV", "sv", "sv");
            Add("da", "Dansk", Script.Latin, "da", "DA", "da", "da");
            Add("nb", "Norsk bokmål", Script.Latin, "no", "NB", "nb", "nb");
            Add("fi", "Suomi", Script.Latin, "fi", "FI", "fi", "fi");
            Add("cs", "Čeština", Script.Latin, "cs", "CS", "cs", "cs");
            Add("sk", "Slovenčina", Script.Latin, "sk", "SK", "sk", "sk");
            Add("hu", "Magyar", Script.Latin, "hu", "HU", "hu", "hu");
            Add("ro", "Română", Script.Latin, "ro", "RO", "ro", "ro");
            Add("bg", "Български", Script.Cyrillic, "bg", "BG", "bg", "bg");
            Add("el", "Ελληνικά", Script.Greek, "el", "EL", "el", "el");
            Add("hr", "Hrvatski", Script.Latin, "hr", null, "hr", null);
            Add("sr", "Српски", Script.Cyrillic, "sr", null, "sr-Cyrl", "sr");
            Add("sl", "Slovenščina", Script.Latin, "sl", "SL", "sl", "sl");
            Add("lt", "Lietuvių", Script.Latin, "lt", "LT", "lt", "lt");
            Add("lv", "Latviešu", Script.Latin, "lv", "LV", "lv", "lv");
            Add("et", "Eesti", Script.Latin, "et", "ET", "et", "et");
            Add("ca", "Català", Script.Latin, "ca", null, "ca", "ca");
            Add("fil", "Filipino", Script.Latin, "tl", null, "fil", "tl");
            SupportedReadOnly = SupportedList.AsReadOnly();
        }

        private static void Add(string iso, string nativeName, Script script, string google, string deepL, string microsoft, string libre)
        {
            ByIso[iso] = new Language
            {
                Iso = iso,
                NativeName = nativeName,
                Script = script,
                Google = google,
                DeepL = deepL,
                Microsoft = microsoft,
                Libre = libre,
            };
            SupportedList.Add(iso);
        }

        /// <summary>The languages offered in the options dropdown (Arabic first). Every one has a <see cref="DisplayName"/>.</summary>
        public static IReadOnlyList<string> Supported => SupportedReadOnly;

        /// <summary>
        /// Converts a game locale id ("es-ES", "zh-HANS", "pt-BR", "en-US") to the mod's code ("es", "zh-Hans", "pt-BR", "en").
        /// Unknown ids give the part before the first '-' in lower case; null or empty gives null.
        /// </summary>
        public static string FromLocaleId(string gameLocaleId)
        {
            return Normalize(gameLocaleId);
        }

        /// <summary>Converts Unity's system language; null for Unknown or a language without an ISO code.</summary>
        public static string FromSystemLanguage(UnityEngine.SystemLanguage language)
        {
            // Matched by name so the code is independent of the enum's numeric values and of members that only
            // some Unity versions have (Hindi, the "Hugarian"/"Hungarian" spellings).
            switch (language.ToString())
            {
                case "Afrikaans": return "af";
                case "Arabic": return "ar";
                case "Basque": return "eu";
                case "Belarusian": return "be";
                case "Bulgarian": return "bg";
                case "Catalan": return "ca";
                case "Chinese": return "zh-Hans";
                case "ChineseSimplified": return "zh-Hans";
                case "ChineseTraditional": return "zh-Hant";
                case "Czech": return "cs";
                case "Danish": return "da";
                case "Dutch": return "nl";
                case "English": return "en";
                case "Estonian": return "et";
                case "Faroese": return "fo";
                case "Finnish": return "fi";
                case "French": return "fr";
                case "German": return "de";
                case "Greek": return "el";
                case "Hebrew": return "he";
                case "Hindi": return "hi";
                case "Hugarian": return "hu";
                case "Hungarian": return "hu";
                case "Icelandic": return "is";
                case "Indonesian": return "id";
                case "Italian": return "it";
                case "Japanese": return "ja";
                case "Korean": return "ko";
                case "Latvian": return "lv";
                case "Lithuanian": return "lt";
                case "Norwegian": return "nb";
                case "Polish": return "pl";
                case "Portuguese": return "pt";
                case "Romanian": return "ro";
                case "Russian": return "ru";
                case "SerboCroatian": return "hr";
                case "Slovak": return "sk";
                case "Slovenian": return "sl";
                case "Spanish": return "es";
                case "Swedish": return "sv";
                case "Thai": return "th";
                case "Turkish": return "tr";
                case "Ukrainian": return "uk";
                case "Vietnamese": return "vi";
                default: return null;
            }
        }

        /// <summary>Converts a .NET culture ("ar-SA" → "ar", "zh-CN" → "zh-Hans", "zh-TW" → "zh-Hant", "pt-BR" → "pt-BR"); null for the invariant culture.</summary>
        public static string FromCulture(CultureInfo culture)
        {
            if (culture == null)
                return null;
            string name;
            try
            {
                name = culture.Name;
            }
            catch (Exception)
            {
                return null;
            }
            return Normalize(name);
        }

        /// <summary>The language's name in its own language ("ar" → "العربية", "es" → "Español"); the code itself when unknown.</summary>
        public static string DisplayName(string isoCode)
        {
            if (string.IsNullOrEmpty(isoCode))
                return "";
            Language language;
            if (ByIso.TryGetValue(isoCode, out language))
                return language.NativeName;
            string normalized = Normalize(isoCode);
            if (normalized != null && ByIso.TryGetValue(normalized, out language))
                return language.NativeName;
            try
            {
                var culture = CultureInfo.GetCultureInfo(isoCode);
                if (!string.IsNullOrEmpty(culture.NativeName) && !culture.Equals(CultureInfo.InvariantCulture))
                    return culture.NativeName;
            }
            catch (Exception)
            {
                // Unknown to the runtime (Mono ships a reduced culture table): fall through to the code.
            }
            return isoCode;
        }

        /// <summary>
        /// True when the letters of <paramref name="text"/> are mostly (at least 60 %) in the script of
        /// <paramref name="isoCode"/>, i.e. the text is already written in the target language's script and need not be
        /// translated. Always false for Latin-script targets (Latin letters do not tell the language) and for texts
        /// without letters.
        /// </summary>
        public static bool IsScriptOf(string text, string isoCode)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(isoCode))
                return false;
            Language language;
            if (!ByIso.TryGetValue(isoCode, out language))
            {
                string normalized = Normalize(isoCode);
                if (normalized == null || !ByIso.TryGetValue(normalized, out language))
                    return false;
            }
            if (language.Script == Script.Latin)
                return false;

            int letters = 0;
            int inScript = 0;
            for (int i = 0; i < text.Length; i++)
            {
                int codePoint;
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    codePoint = char.ConvertToUtf32(text[i], text[i + 1]);
                    i++;
                }
                else
                {
                    codePoint = text[i];
                }
                if (!IsLetterCodePoint(codePoint, text, i))
                    continue;
                letters++;
                if (InScript(codePoint, language.Script))
                    inScript++;
            }
            return letters > 0 && inScript * 10 >= letters * 6;
        }

        /// <summary>
        /// The engine's spelling of a mod language code, or null when the engine does not offer that language.
        /// The result is the TARGET form (DeepL "EN-US", "PT-BR"); "auto" is returned unchanged for every engine.
        /// Codes that are not in <see cref="Supported"/> pass through for Google, Microsoft and LibreTranslate (they
        /// offer many more languages) and give null for DeepL.
        /// </summary>
        public static string ForProvider(ProviderKind kind, string isoCode)
        {
            if (string.IsNullOrEmpty(isoCode))
                return null;
            if (string.Equals(isoCode, Auto, StringComparison.OrdinalIgnoreCase))
                return Auto;
            Language language;
            if (!ByIso.TryGetValue(isoCode, out language))
            {
                string normalized = Normalize(isoCode);
                if (normalized != null && ByIso.TryGetValue(normalized, out language))
                {
                    // e.g. "zh-CN" or "pt_BR" spelled differently by the caller.
                }
                else
                {
                    switch (kind)
                    {
                        case ProviderKind.DeepL:
                            return null;
                        default:
                            return normalized ?? isoCode.ToLowerInvariant();
                    }
                }
            }
            switch (kind)
            {
                case ProviderKind.GoogleFree: return language.Google;
                case ProviderKind.DeepL: return language.DeepL;
                case ProviderKind.Microsoft: return language.Microsoft;
                // The free chain is made of Google endpoints.
                case ProviderKind.FreeAuto: return language.Google;
                case ProviderKind.LibreTranslate: return language.Libre;
                default: return null;
            }
        }

        /// <summary>True when <see cref="ForProvider"/> gives a code for that engine.</summary>
        public static bool IsSupportedBy(ProviderKind kind, string isoCode)
        {
            return ForProvider(kind, isoCode) != null;
        }

        /// <summary>The language without region or script ("pt-BR" → "pt", "zh-Hans" → "zh").</summary>
        internal static string BaseLanguage(string isoCode)
        {
            if (string.IsNullOrEmpty(isoCode))
                return isoCode;
            int dash = isoCode.IndexOfAny(new[] { '-', '_' });
            return (dash < 0 ? isoCode : isoCode.Substring(0, dash)).ToLowerInvariant();
        }

        /// <summary>Normalizes any BCP-47-like tag ("zh_TW", "ZH-HANS", "pt-br", "en-US", "iw") to the mod's code.</summary>
        internal static string Normalize(string tag)
        {
            if (string.IsNullOrEmpty(tag))
                return null;
            string[] parts = tag.Trim().Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return null;
            string language = parts[0].ToLowerInvariant();
            switch (language)
            {
                case "zh":
                case "zt":
                {
                    if (language == "zt")
                        return "zh-Hant";
                    for (int i = 1; i < parts.Length; i++)
                    {
                        switch (parts[i].ToLowerInvariant())
                        {
                            case "hant":
                            case "tw":
                            case "hk":
                            case "mo":
                            case "cht":
                                return "zh-Hant";
                            case "hans":
                            case "cn":
                            case "sg":
                            case "chs":
                                return "zh-Hans";
                        }
                    }
                    return "zh-Hans";
                }
                case "pt":
                    for (int i = 1; i < parts.Length; i++)
                    {
                        if (string.Equals(parts[i], "br", StringComparison.OrdinalIgnoreCase))
                            return "pt-BR";
                    }
                    return "pt";
                case "iw": return "he";
                case "in": return "id";
                case "ji": return "yi";
                case "no": return "nb";
                case "tl": return "fil";
                default: return language;
            }
        }

        private static bool IsLetterCodePoint(int codePoint, string text, int index)
        {
            if (codePoint > 0xFFFF)
                return CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(codePoint), 0) <= UnicodeCategory.OtherLetter;
            char c = (char)codePoint;
            if (char.IsLetter(c))
                return true;
            // Combining marks of Indic and Thai scripts are part of their words.
            UnicodeCategory category = char.GetUnicodeCategory(c);
            return (category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.SpacingCombiningMark) && codePoint >= 0x0900;
        }

        private static bool InScript(int c, Script script)
        {
            switch (script)
            {
                case Script.Arabic:
                    return (c >= 0x0600 && c <= 0x06FF) || (c >= 0x0750 && c <= 0x077F) || (c >= 0x08A0 && c <= 0x08FF)
                        || (c >= 0xFB50 && c <= 0xFDFF) || (c >= 0xFE70 && c <= 0xFEFF);
                case Script.Hebrew:
                    return (c >= 0x0590 && c <= 0x05FF) || (c >= 0xFB1D && c <= 0xFB4F);
                case Script.Cyrillic:
                    return (c >= 0x0400 && c <= 0x052F) || (c >= 0x1C80 && c <= 0x1C8F) || (c >= 0x2DE0 && c <= 0x2DFF) || (c >= 0xA640 && c <= 0xA69F);
                case Script.Greek:
                    return (c >= 0x0370 && c <= 0x03FF) || (c >= 0x1F00 && c <= 0x1FFF);
                case Script.Han:
                    return IsHan(c);
                case Script.Japanese:
                    return IsHan(c) || (c >= 0x3040 && c <= 0x30FF) || (c >= 0x31F0 && c <= 0x31FF) || (c >= 0xFF66 && c <= 0xFF9F);
                case Script.Hangul:
                    return (c >= 0xAC00 && c <= 0xD7AF) || (c >= 0x1100 && c <= 0x11FF) || (c >= 0x3130 && c <= 0x318F) || (c >= 0xA960 && c <= 0xA97F) || (c >= 0xD7B0 && c <= 0xD7FF);
                case Script.Thai:
                    return c >= 0x0E00 && c <= 0x0E7F;
                case Script.Devanagari:
                    return (c >= 0x0900 && c <= 0x097F) || (c >= 0xA8E0 && c <= 0xA8FF);
                case Script.Bengali:
                    return c >= 0x0980 && c <= 0x09FF;
                default:
                    return false;
            }
        }

        private static bool IsHan(int c)
        {
            return (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0xF900 && c <= 0xFAFF)
                || (c >= 0x20000 && c <= 0x2FA1F) || c == 0x3005 || c == 0x3007;
        }
    }
}
