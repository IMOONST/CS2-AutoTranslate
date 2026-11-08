using System;

namespace AutoTranslate.Core
{
    /// <summary>Which texts are worth translating, and the whitespace handling shared by the cache and lookups.</summary>
    internal static class TextRules
    {
        /// <summary>Longest text sent to an engine.</summary>
        public const int MaxTextLength = 2000;

        private enum Script
        {
            None,
            Arabic,
            Hebrew,
            Cyrillic,
            Greek,
            Han,
            Kana,
            Hangul,
            Thai,
            Devanagari,
            Bengali,
            Tamil,
            Telugu,
            Kannada,
            Malayalam,
            Gujarati,
            Gurmukhi,
            Georgian,
            Armenian,
            Khmer,
            Lao,
            Myanmar,
            Sinhala,
            Ethiopic,
        }

        /// <summary>True when the text has whitespace at either end (so lookups use the trimmed form).</summary>
        public static bool HasOuterWhitespace(string text)
        {
            return text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[text.Length - 1]));
        }

        /// <summary>Puts the leading and trailing whitespace of <paramref name="original"/> around <paramref name="translatedCore"/>.</summary>
        public static string Rewrap(string original, string translatedCore)
        {
            int start = 0;
            while (start < original.Length && char.IsWhiteSpace(original[start])) start++;
            int end = original.Length;
            while (end > start && char.IsWhiteSpace(original[end - 1])) end--;
            if (start == 0 && end == original.Length) return translatedCore;
            return original.Substring(0, start) + translatedCore + original.Substring(end);
        }

        /// <summary>True when the text has at least one letter (anything else is left as it is).</summary>
        public static bool HasLetters(string text)
        {
            for (int i = 0; i < text.Length; i++)
                if (char.IsLetter(text[i])) return true;
            return false;
        }

        /// <summary>
        /// True when the letters of the text are already largely (30 % or more) in the script of the target language, e.g.
        /// a text with Arabic letters when the target is Arabic. Always false for Latin-script targets, whose language
        /// the letters do not reveal.
        /// </summary>
        public static bool IsInTargetScript(string text, string targetLanguage)
        {
            var script = ScriptOf(targetLanguage);
            if (script == Script.None) return false;
            int letters = 0, inScript = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (!char.IsLetter(c)) continue;
                letters++;
                if (InScript(c, script) || (script == Script.Kana && InScript(c, Script.Han))) inScript++;
            }
            if (letters == 0) return false;
            if (script == Script.Kana)
            {
                // Japanese is recognised by its kana; kanji alone may just as well be Chinese.
                bool hasKana = false;
                for (int i = 0; i < text.Length && !hasKana; i++) hasKana = InScript(text[i], Script.Kana);
                if (!hasKana) return false;
            }
            if (script == Script.Han)
            {
                // Kana means Japanese, which is worth translating into Chinese.
                for (int i = 0; i < text.Length; i++)
                    if (InScript(text[i], Script.Kana)) return false;
            }
            return inScript * 10 >= letters * 3;
        }

        private static Script ScriptOf(string language)
        {
            if (string.IsNullOrEmpty(language)) return Script.None;
            int dash = language.IndexOf('-');
            var primary = (dash < 0 ? language : language.Substring(0, dash)).ToLowerInvariant();
            switch (primary)
            {
                case "ar": case "fa": case "ur": case "ps": case "sd": case "ug": case "ckb": return Script.Arabic;
                case "he": case "yi": case "iw": return Script.Hebrew;
                case "ru": case "uk": case "be": case "bg": case "sr": case "mk": case "kk": case "ky": case "mn": case "tg": case "tt": case "ba": return Script.Cyrillic;
                case "el": return Script.Greek;
                case "zh": return Script.Han;
                case "ja": return Script.Kana;
                case "ko": return Script.Hangul;
                case "th": return Script.Thai;
                case "hi": case "mr": case "ne": case "sa": return Script.Devanagari;
                case "bn": case "as": return Script.Bengali;
                case "ta": return Script.Tamil;
                case "te": return Script.Telugu;
                case "kn": return Script.Kannada;
                case "ml": return Script.Malayalam;
                case "gu": return Script.Gujarati;
                case "pa": return Script.Gurmukhi;
                case "ka": return Script.Georgian;
                case "hy": return Script.Armenian;
                case "km": return Script.Khmer;
                case "lo": return Script.Lao;
                case "my": return Script.Myanmar;
                case "si": return Script.Sinhala;
                case "am": case "ti": return Script.Ethiopic;
                default: return Script.None;
            }
        }

        private static bool InScript(char c, Script script)
        {
            switch (script)
            {
                case Script.Arabic: return (c >= '؀' && c <= 'ۿ') || (c >= 'ݐ' && c <= 'ݿ') || (c >= 'ࢠ' && c <= 'ࣿ') || (c >= 'ﭐ' && c <= '﷿') || (c >= 'ﹰ' && c <= '﻿');
                case Script.Hebrew: return (c >= '֐' && c <= '׿') || (c >= 'יִ' && c <= 'ﭏ');
                case Script.Cyrillic: return (c >= 'Ѐ' && c <= 'ԯ') || (c >= 'ⷠ' && c <= 'ⷿ') || (c >= 'Ꙁ' && c <= 'ꚟ');
                case Script.Greek: return (c >= 'Ͱ' && c <= 'Ͽ') || (c >= 'ἀ' && c <= '῿');
                case Script.Han: return (c >= '一' && c <= '鿿') || (c >= '㐀' && c <= '䶿') || (c >= '豈' && c <= '﫿');
                case Script.Kana: return (c >= '぀' && c <= 'ヿ') || (c >= 'ㇰ' && c <= 'ㇿ') || (c >= 'ｦ' && c <= 'ﾟ');
                case Script.Hangul: return (c >= '가' && c <= '힯') || (c >= 'ᄀ' && c <= 'ᇿ') || (c >= '㄰' && c <= '㆏');
                case Script.Thai: return c >= '฀' && c <= '๿';
                case Script.Devanagari: return c >= 'ऀ' && c <= 'ॿ';
                case Script.Bengali: return c >= 'ঀ' && c <= '৿';
                case Script.Gurmukhi: return c >= '਀' && c <= '੿';
                case Script.Gujarati: return c >= '઀' && c <= '૿';
                case Script.Tamil: return c >= '஀' && c <= '௿';
                case Script.Telugu: return c >= 'ఀ' && c <= '౿';
                case Script.Kannada: return c >= 'ಀ' && c <= '೿';
                case Script.Malayalam: return c >= 'ഀ' && c <= 'ൿ';
                case Script.Sinhala: return c >= '඀' && c <= '෿';
                case Script.Lao: return c >= '຀' && c <= '໿';
                case Script.Myanmar: return c >= 'က' && c <= '႟';
                case Script.Georgian: return (c >= 'Ⴀ' && c <= 'ჿ') || (c >= 'Ა' && c <= 'Ჿ');
                case Script.Ethiopic: return c >= 'ሀ' && c <= '᎟';
                case Script.Khmer: return c >= 'ក' && c <= '៿';
                case Script.Armenian: return c >= '԰' && c <= '֏';
                default: return false;
            }
        }

        /// <summary>Same language, ignoring region ("ar" = "ar-SA", "pt" ≈ "pt-BR"); Chinese scripts stay distinct.</summary>
        public static bool SameLanguage(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            var pa = Primary(a);
            var pb = Primary(b);
            if (!string.Equals(pa, pb, StringComparison.OrdinalIgnoreCase)) return false;
            if (pa == "zh")
            {
                // "zh" alone counts as Simplified.
                bool ta = a.IndexOf("hant", StringComparison.OrdinalIgnoreCase) >= 0;
                bool tb = b.IndexOf("hant", StringComparison.OrdinalIgnoreCase) >= 0;
                return ta == tb;
            }
            return true;
        }

        /// <summary>True for English ("en", "en-US", "en-GB").</summary>
        public static bool IsEnglish(string language) => !string.IsNullOrEmpty(language) && Primary(language) == "en";

        private static readonly char[] kSeparators = { '-', '_' };

        private static string Primary(string language)
        {
            int dash = language.IndexOfAny(kSeparators);
            return (dash < 0 ? language : language.Substring(0, dash)).ToLowerInvariant();
        }
    }
}
