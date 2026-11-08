using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using AutoTranslate.Core;

namespace AutoTranslate.Providers
{
    /// <summary>
    /// Replaces the parts of a text that a translator must not change (format items, placeholders, rich-text tags,
    /// URLs, paths, key combinations, escapes...) by tokens, and puts them back into the translation. The player's
    /// protected terms (<see cref="ProtectedTerms"/>: names such as "Al Qassim") are masked the same way when given.
    /// <para>
    /// TOKEN FORMAT. A token is "ZM" + one to three letters A–Y + "Z" (ZMAZ, ZMBZ, ... ZMYZ, ZMAAZ, ...), the index of
    /// the protected part in bijective base 25 (Z is never an index letter, so the closing Z is unambiguous).
    /// Why this and not "{0}", "[[0]]", "__0__" or "⟦0⟧": Google's free gtx endpoint has no markup mode, so tokens must
    /// survive as plain text. Neural engines treat an unknown all-capital Latin "word" as a name or acronym and copy it
    /// verbatim into any script (the technique XUnity.AutoTranslator uses). Brackets and underscores are frequently
    /// re-spaced, converted to full-width forms or dropped, and digits may be localized ("٠" in Arabic, "０" in CJK)
    /// or re-formatted, which is why the index is spelled with letters.
    /// The providers that have a markup mode convert these tokens to markup they are guaranteed to keep and back:
    /// DeepL to &lt;x id="A"/&gt; with tag_handling=xml, Microsoft to &lt;span class="notranslate"&gt; with textType=html.
    /// Google and LibreTranslate receive the plain tokens.
    /// </para>
    /// <para>
    /// A space is inserted between a token and an adjacent letter or digit ("&lt;b&gt;Settings" → "ZMAZ Settings")
    /// so the engine sees the word; <see cref="Unmask"/> removes it again. Line breaks are NOT tokens: they stay in the
    /// masked text (engines keep lines), and Unmask requires the translation to have the same number of lines.
    /// Leading and trailing whitespace stays in the masked text too; providers trim it before sending and Unmask
    /// restores it, along with each line's indentation.
    /// </para>
    /// Thread-safe.
    /// </summary>
    public static class TextMasking
    {
        private const string IndexLetters = "ABCDEFGHIJKLMNOPQRSTUVWXY";
        private const int MaxTokens = 25 + 25 * 25 + 25 * 25 * 25;

        /// <summary>A token exactly as <see cref="Mask"/> writes it; group 1 holds the index letters.</summary>
        internal static readonly Regex CanonicalToken = new Regex("ZM([A-Y]{1,3})Z", RegexOptions.CultureInvariant);

        /// <summary>A token as an engine may return it: spaced ("ZM A Z"), lower or mixed case (bounded by non-letters), full-width.</summary>
        private static readonly Regex TolerantToken = new Regex(
            "[ZＺ][ \u00A0]?[MＭ][ \u00A0]?((?:[A-YＡ-Ｙ][ \u00A0]?){1,3}?)[ZＺ]"
            + "|(?<!\\p{L})(?i:[zｚ][ \u00A0]?[mｍ][ \u00A0]?((?:[a-yａ-ｙ][ \u00A0]?){1,3}?)[zｚ])(?!\\p{L})",
            RegexOptions.CultureInvariant);

        /// <summary>One line break of any convention.</summary>
        internal static readonly Regex LineBreak = new Regex("\r\n|\r|\n|\u2028|\u2029", RegexOptions.CultureInvariant);

        private const RegexOptions Opts = RegexOptions.CultureInvariant;
        private const RegexOptions OptsI = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

        private sealed class Rule
        {
            public Regex Regex;
            public bool TrimTrailingPunctuation;

            public Rule(string pattern, RegexOptions options, bool trimTrailingPunctuation = false)
            {
                Regex = new Regex(pattern, options);
                TrimTrailingPunctuation = trimTrailingPunctuation;
            }
        }

        private const string KeyModifier = "(?:Ctrl|Control|Shift|AltGr|Alt|Cmd|Command|Win|Windows|Meta|Super|Option|Opt|Fn)";
        private const string KeyName = "(?:F(?:2[0-4]|1[0-9]|[1-9])|Tab|Enter|Return|Escape|Esc|Space(?:bar)?|Backspace|Delete|Del|Insert|Ins|Home|End|Page ?Up|Page ?Down|PgUp|PgDn|Up|Down|Left|Right|Mouse ?[1-5]|LMB|RMB|MMB|Numpad ?[0-9+*/-]|Num ?[0-9]|[A-Za-z0-9])";
        private const string PathSegment = "[^\\s<>\"'|?*\\\\/:]+";

        // Every pattern is searched independently; overlapping matches are resolved earliest-first, longest-first.
        private static readonly Rule[] Rules =
        {
            // Text that already looks like one of our tokens: protected so it cannot be confused with one.
            new Rule("ZM[A-Y]{1,3}Z", Opts),
            new Rule("(?<!\\p{L})(?i:zm[a-y]{1,3}z)(?!\\p{L})", Opts),
            // URLs and e-mail addresses.
            new Rule("(?:https?|ftp|file)://[^\\s<>\"'`]+", OptsI, true),
            new Rule("(?<![\\w.])www\\.[^\\s<>\"'`]+", OptsI, true),
            new Rule("(?<![\\w.+-])[\\w.+-]+@[\\w-]+(?:\\.[\\w-]+)+", Opts, true),
            // Windows paths with a drive letter (inner folders may contain spaces), UNC paths, Unix-style absolute paths.
            new Rule("(?<![\\w])[A-Za-z]:[\\\\/](?:" + PathSegment + "(?: " + PathSegment + ")*(?=[\\\\/])[\\\\/])*(?:" + PathSegment + ")?", Opts, true),
            new Rule("\\\\\\\\[\\w.$-]+(?:\\\\[^\\s<>\"'|?*\\\\]+)+", Opts, true),
            new Rule("(?<![\\w/.~])(?:~|\\.{1,2})?/(?:[\\w.@-]+/)+[\\w.@-]*", Opts, true),
            // File names with a known extension.
            new Rule("(?<![\\w.-])[\\w-]+(?:\\.[\\w-]+)*\\.(?:json|xml|txt|cfg|ini|log|csv|tsv|dll|exe|png|jpe?g|gif|bmp|tga|svg|cs|js|ts|tsx|jsx|css|scss|html?|md|ya?ml|toml|zip|7z|rar|cok|cid|prefab|bin|dat|sav|db|lua|py|bat|ps1|sh|unity3d|bundle)(?![\\w])", OptsI, true),
            // Key combinations: Ctrl+Shift+F, Alt + F4, Shift+Mouse 1.
            new Rule("(?<![\\w])" + KeyModifier + "(?:[ ]?\\+[ ]?(?:" + KeyModifier + "|" + KeyName + "))+(?![\\w])", OptsI),
            // Format items and placeholders: {{x}}, {0}, {0:N2}, {0,-8}, {NAME}, ${x}, $1, literal {{ and }}.
            new Rule("\\{\\{[^{}\\r\\n]*\\}\\}", Opts),
            new Rule("\\$?\\{[^{}\\r\\n]{1,80}\\}", Opts),
            new Rule("\\{\\{|\\}\\}", Opts),
            new Rule("(?<![\\w$])\\$[0-9](?![0-9])", Opts),
            // Windows environment variables (%APPDATA%) before printf so "%d%%" is not read as one.
            new Rule("%[A-Za-z_][A-Za-z0-9_]+%", Opts),
            // printf: %s %d %5.2f %1$s %-10s %lld %@ %% ; Qt-style %1.
            new Rule("%(?:[0-9]+\\$)?[-+0#']*(?:[0-9]+|\\*)?(?:\\.(?:[0-9]+|\\*))?(?:hh|h|ll|l|L|q|j|z|t|I64|I32)?[diouxXeEfFgGaAcCsSpn@](?![A-Za-z])", Opts),
            new Rule("%%", Opts),
            new Rule("%[0-9]{1,2}(?![0-9.$])", Opts),
            // Rich text / HTML tags (<b>, </color>, <color=#FF0000>, <br/>, <sprite name="x">, <#ff0>) and entities.
            new Rule("</?[A-Za-z][\\w.:-]*(?:\\s*=\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s>]+))?(?:\\s+[\\w.:-]+(?:\\s*=\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s>]+))?)*\\s*/?>", Opts),
            new Rule("<#[0-9A-Fa-f]{3,8}>", Opts),
            new Rule("&(?:[A-Za-z][A-Za-z0-9]{1,31}|#[0-9]{1,7}|#[xX][0-9A-Fa-f]{1,6});", Opts),
            // Escape sequences written as text: \n \t \r \" \\ \u00E9 \x41.
            new Rule("\\\\(?:[nrtv0\"'\\\\]|u[0-9A-Fa-f]{4}|x[0-9A-Fa-f]{2})", Opts),
            // Version numbers (1.2.3, v1.2), hex colours, identifiers (snake_case, dotted.keys.like.this).
            new Rule("(?<![\\w.])(?:[vV][0-9]+(?:\\.[0-9]+)+|[0-9]+(?:\\.[0-9]+){2,})(?![\\w])", Opts),
            new Rule("(?<![\\w#])#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6})(?![\\w])", Opts),
            new Rule("(?<![\\w.])[A-Za-z][A-Za-z0-9]*_[A-Za-z0-9_]+(?![\\w])", Opts),
            new Rule("(?<![\\w.])[A-Za-z_][A-Za-z0-9_]*(?:\\.[A-Za-z_][A-Za-z0-9_]*){2,}(?![\\w])", Opts),
        };

        /// <summary>Layout of the tokens of one mask, used by <see cref="Unmask"/> to undo the inserted spaces.</summary>
        private sealed class TokenLayout
        {
            public string MaskedText;
            public bool[] PadBefore;
            public bool[] PadAfter;
            public bool[] TightBefore;
            public bool[] TightAfter;
        }

        private static readonly ConditionalWeakTable<MaskedText, TokenLayout> Layouts = new ConditionalWeakTable<MaskedText, TokenLayout>();

        private struct Span
        {
            public int Start;
            public int End;
        }

        /// <summary>
        /// Masks a text. Never returns null. <see cref="MaskedText.IsEmpty"/> is true when no letter remains outside the
        /// tokens (nothing to translate: only tokens, digits, punctuation, symbols or whitespace).
        /// </summary>
        public static MaskedText Mask(string text)
        {
            return Mask(text, null);
        }

        /// <summary>
        /// Masks a text and, in addition, every occurrence of the <paramref name="terms"/> (words or phrases the player
        /// wants kept as they are, e.g. "Al Qassim"): each becomes a token like any other protected part, so the engine
        /// translates the rest and <see cref="Unmask"/> puts the term back exactly as the original text wrote it. A text
        /// made only of terms (and punctuation) is <see cref="MaskedText.IsEmpty"/>. <paramref name="terms"/> may be null.
        /// </summary>
        public static MaskedText Mask(string text, ProtectedTerms terms)
        {
            var result = new MaskedText();
            if (string.IsNullOrEmpty(text))
            {
                result.Text = text ?? "";
                result.IsEmpty = true;
                return result;
            }

            List<Span> spans = FindProtectedSpans(text, terms);
            if (spans.Count > MaxTokens)
            {
                // Absurdly many protected parts: not a UI text worth translating.
                result.Text = text;
                result.IsEmpty = true;
                return result;
            }

            var builder = new StringBuilder(text.Length + spans.Count * 6);
            var layout = new TokenLayout
            {
                PadBefore = new bool[spans.Count],
                PadAfter = new bool[spans.Count],
                TightBefore = new bool[spans.Count],
                TightAfter = new bool[spans.Count],
            };
            bool hasLetters = false;
            int position = 0;
            for (int i = 0; i < spans.Count; i++)
            {
                Span span = spans[i];
                hasLetters |= AppendPlain(builder, text, position, span.Start);
                char before = span.Start > 0 ? text[span.Start - 1] : '\0';
                char after = span.End < text.Length ? text[span.End] : '\0';
                layout.TightBefore[i] = span.Start > 0 && !char.IsWhiteSpace(before);
                layout.TightAfter[i] = span.End < text.Length && !char.IsWhiteSpace(after);
                // A neighbouring token (only possible when separated by whitespace) never needs padding; a letter or digit does.
                layout.PadBefore[i] = span.Start > 0 && char.IsLetterOrDigit(before);
                layout.PadAfter[i] = span.End < text.Length && char.IsLetterOrDigit(after);
                if (layout.PadBefore[i])
                    builder.Append(' ');
                builder.Append(Token(i));
                if (layout.PadAfter[i])
                    builder.Append(' ');
                result.Tokens.Add(text.Substring(span.Start, span.End - span.Start));
                position = span.End;
            }
            hasLetters |= AppendPlain(builder, text, position, text.Length);

            result.Text = builder.ToString();
            result.IsEmpty = !hasLetters;
            layout.MaskedText = result.Text;
            if (spans.Count > 0)
                Layouts.Add(result, layout);
            return result;
        }

        /// <summary>
        /// Puts the protected parts back into a translation of <see cref="MaskedText.Text"/>.
        /// Tolerates tokens that the engine re-spaced ("ZM A Z"), re-cased ("Zmaz"), wrote in full-width letters or
        /// moved; removes the spaces <see cref="Mask"/> inserted and spaces the engine put between a token and adjacent
        /// punctuation; restores leading/trailing whitespace, line-break style and line indentation.
        /// </summary>
        /// <returns>
        /// The final text, or NULL when the translation is unusable: <paramref name="translated"/> is null, a token is
        /// missing, duplicated or unknown, the number of lines changed, or nothing but tokens is left of a text that had
        /// words. Null means the caller should keep the original text (and may retry later).
        /// </returns>
        public static string Unmask(MaskedText masked, string translated)
        {
            if (masked == null || translated == null)
                return null;
            string source = masked.Text ?? "";
            int tokenCount = masked.Tokens == null ? 0 : masked.Tokens.Count;

            int leadLength = 0;
            while (leadLength < source.Length && char.IsWhiteSpace(source[leadLength]))
                leadLength++;
            int trailStart = source.Length;
            while (trailStart > leadLength && char.IsWhiteSpace(source[trailStart - 1]))
                trailStart--;
            string lead = source.Substring(0, leadLength);
            string trail = source.Substring(trailStart);
            string sourceCore = source.Substring(leadLength, trailStart - leadLength);

            string core = TrimAll(translated);
            if (core.Length == 0)
                return sourceCore.Length == 0 ? source : null;

            TokenLayout layout = GetLayout(masked, source, tokenCount);

            // 1. Lines: the same count, with the original line-break sequences and indentation.
            MatchCollection sourceBreaks = LineBreak.Matches(sourceCore);
            string[] translatedLines = LineBreak.Split(core);
            if (translatedLines.Length != sourceBreaks.Count + 1)
                return null;
            if (sourceBreaks.Count > 0)
            {
                string[] sourceLines = LineBreak.Split(sourceCore);
                var joined = new StringBuilder(core.Length + 16);
                for (int i = 0; i < translatedLines.Length; i++)
                {
                    string sourceLine = sourceLines[i];
                    string line = TrimHorizontal(translatedLines[i]);
                    if (line.Length > 0)
                    {
                        joined.Append(LeadingHorizontal(sourceLine));
                        joined.Append(line);
                        joined.Append(TrailingHorizontal(sourceLine));
                    }
                    else if (sourceLine.Trim().Length == 0)
                    {
                        joined.Append(sourceLine);
                    }
                    if (i < sourceBreaks.Count)
                        joined.Append(sourceBreaks[i].Value);
                }
                core = joined.ToString();
            }

            // 2. A text that had words must still have something besides the tokens.
            if (!masked.IsEmpty && !HasLettersOutsideTokens(core))
                return null;

            // 3. Tokens.
            var seen = new bool[tokenCount];
            int found = 0;
            var output = new StringBuilder(core.Length + 32);
            int position = 0;
            foreach (Match match in TolerantToken.Matches(core))
            {
                int index = DecodeIndex(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
                if (index < 0 || index >= tokenCount || seen[index])
                    return null;
                seen[index] = true;
                found++;

                output.Append(core, position, match.Index - position);
                // Left side.
                if (layout.PadBefore[index])
                {
                    // Mask inserted this space between the token and a word.
                    TrimHorizontalEnd(output);
                }
                else if (layout.TightBefore[index])
                {
                    // Originally touching punctuation: drop the space the engine added, unless a word now stands there.
                    int length = output.Length;
                    TrimHorizontalEnd(output);
                    if (output.Length != length && output.Length > 0 && char.IsLetterOrDigit(output[output.Length - 1]))
                        output.Append(' ');
                }
                else if (output.Length > 0 && NeedsSpaceBetween(output[output.Length - 1]))
                {
                    // Originally separated by whitespace, but the engine glued a word to the token.
                    output.Append(' ');
                }

                output.Append(masked.Tokens[index]);
                position = match.Index + match.Length;

                // Right side.
                if (layout.PadAfter[index])
                {
                    position = SkipHorizontal(core, position);
                }
                else if (layout.TightAfter[index])
                {
                    int next = SkipHorizontal(core, position);
                    if (next >= core.Length || !char.IsLetterOrDigit(core[next]) || IsTokenStart(core, next))
                        position = next;
                }
                else if (position < core.Length && NeedsSpaceBetween(core[position]) && !IsTokenStart(core, position))
                {
                    output.Append(' ');
                }
            }
            output.Append(core, position, core.Length - position);
            if (found != tokenCount)
                return null;

            string body = output.ToString();
            return lead + body + trail;
        }

        /// <summary>The token for a protected part's index ("ZMAZ" for 0).</summary>
        internal static string Token(int index)
        {
            return "ZM" + IndexToLetters(index) + "Z";
        }

        /// <summary>Index letters → index ("A" → 0, "Y" → 24, "AA" → 25); accepts full-width letters, spaces and any case; -1 when invalid.</summary>
        internal static int DecodeIndex(string letters)
        {
            if (string.IsNullOrEmpty(letters))
                return -1;
            int value = 0;
            int digits = 0;
            foreach (char raw in letters)
            {
                char c = raw;
                if (c == ' ' || c == '\u00A0')
                    continue;
                if (c >= 'Ａ' && c <= 'Ｚ')
                    c = (char)('A' + (c - 'Ａ'));
                else if (c >= 'ａ' && c <= 'ｚ')
                    c = (char)('A' + (c - 'ａ'));
                c = char.ToUpperInvariant(c);
                int digit = IndexLetters.IndexOf(c);
                if (digit < 0)
                    return -1;
                value = value * 25 + digit + 1;
                digits++;
            }
            return digits == 0 || digits > 3 ? -1 : value - 1;
        }

        private static string IndexToLetters(int index)
        {
            var chars = new char[3];
            int length = 0;
            int n = index + 1;
            while (n > 0)
            {
                n--;
                chars[length++] = IndexLetters[n % 25];
                n /= 25;
            }
            Array.Reverse(chars, 0, length);
            return new string(chars, 0, length);
        }

        private static List<Span> FindProtectedSpans(string text, ProtectedTerms terms)
        {
            var candidates = new List<Span>();
            if (terms != null && !terms.IsEmpty)
            {
                var ranges = new List<KeyValuePair<int, int>>();
                terms.AddMatches(text, ranges);
                foreach (var range in ranges)
                    candidates.Add(new Span { Start = range.Key, End = range.Value });
            }
            foreach (Rule rule in Rules)
            {
                foreach (Match match in rule.Regex.Matches(text))
                {
                    int start = match.Index;
                    int end = match.Index + match.Length;
                    if (rule.TrimTrailingPunctuation)
                    {
                        while (end > start && IsTrailingPunctuation(text[end - 1], text, start, end))
                            end--;
                    }
                    if (end > start)
                        candidates.Add(new Span { Start = start, End = end });
                }
            }
            candidates.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));

            var spans = new List<Span>();
            int lastEnd = 0;
            foreach (Span candidate in candidates)
            {
                if (candidate.Start < lastEnd)
                    continue;
                if (spans.Count > 0 && spans[spans.Count - 1].End == candidate.Start)
                {
                    // Directly adjacent protected parts ("%d%%", "</b><br>") become one token.
                    Span previous = spans[spans.Count - 1];
                    previous.End = candidate.End;
                    spans[spans.Count - 1] = previous;
                }
                else
                {
                    spans.Add(candidate);
                }
                lastEnd = candidate.End;
            }
            return spans;
        }

        private static bool IsTrailingPunctuation(char c, string text, int start, int end)
        {
            switch (c)
            {
                case '.':
                case ',':
                case ';':
                case ':':
                case '!':
                case '?':
                case '\'':
                case '"':
                    return true;
                case ')':
                    return !HasUnclosed(text, start, end - 1, '(', ')');
                case ']':
                    return !HasUnclosed(text, start, end - 1, '[', ']');
                default:
                    return false;
            }
        }

        private static bool HasUnclosed(string text, int start, int end, char open, char close)
        {
            int depth = 0;
            for (int i = start; i < end; i++)
            {
                if (text[i] == open)
                    depth++;
                else if (text[i] == close && depth > 0)
                    depth--;
            }
            return depth > 0;
        }

        /// <summary>Appends text[from..to) and tells whether it contains a letter.</summary>
        private static bool AppendPlain(StringBuilder builder, string text, int from, int to)
        {
            bool letters = false;
            for (int i = from; i < to; i++)
            {
                if (!letters && char.IsLetter(text[i]))
                    letters = true;
            }
            builder.Append(text, from, to - from);
            return letters;
        }

        private static TokenLayout GetLayout(MaskedText masked, string source, int tokenCount)
        {
            TokenLayout layout;
            if (Layouts.TryGetValue(masked, out layout) && layout.MaskedText == source && layout.PadBefore.Length == tokenCount)
                return layout;

            // The MaskedText was not made by Mask in this process (e.g. rebuilt from a cache): derive what the text shows.
            layout = new TokenLayout
            {
                MaskedText = source,
                PadBefore = new bool[tokenCount],
                PadAfter = new bool[tokenCount],
                TightBefore = new bool[tokenCount],
                TightAfter = new bool[tokenCount],
            };
            foreach (Match match in CanonicalToken.Matches(source))
            {
                int index = DecodeIndex(match.Groups[1].Value);
                if (index < 0 || index >= tokenCount)
                    continue;
                int end = match.Index + match.Length;
                layout.TightBefore[index] = match.Index > 0 && !char.IsWhiteSpace(source[match.Index - 1]);
                layout.TightAfter[index] = end < source.Length && !char.IsWhiteSpace(source[end]);
            }
            return layout;
        }


        private static bool IsTokenStart(string text, int index)
        {
            Match match = TolerantToken.Match(text, index);
            return match.Success && match.Index == index;
        }

        private static bool HasLettersOutsideTokens(string translated)
        {
            string rest = TolerantToken.Replace(translated, " ");
            foreach (char c in rest)
            {
                if (char.IsLetter(c))
                    return true;
            }
            return false;
        }


        /// <summary>
        /// When the original had a space between a token and a word but the engine glued them, a space is put back,
        /// except in scripts written without spaces (Chinese, Japanese, Thai, Lao, Khmer, Myanmar).
        /// </summary>
        private static bool NeedsSpaceBetween(char neighbour)
        {
            if (!char.IsLetterOrDigit(neighbour))
                return false;
            int c = neighbour;
            bool spaceless = (c >= 0x2E80 && c <= 0x9FFF) || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFF00 && c <= 0xFFEF)
                || (c >= 0x0E00 && c <= 0x0EFF) || (c >= 0x1000 && c <= 0x109F) || (c >= 0x1780 && c <= 0x17FF)
                || char.IsSurrogate(neighbour);
            return !spaceless;
        }

        private static bool IsHorizontalSpace(char c)
        {
            return c == ' ' || c == '\t' || c == '\u00A0' || c == '\u3000' || c == '\u200B' || c == '\u202F' || c == '\u2009';
        }

        private static void TrimHorizontalEnd(StringBuilder builder)
        {
            int length = builder.Length;
            while (length > 0 && IsHorizontalSpace(builder[length - 1]))
                length--;
            builder.Length = length;
        }

        private static int SkipHorizontal(string text, int position)
        {
            while (position < text.Length && IsHorizontalSpace(text[position]))
                position++;
            return position;
        }

        private static string TrimHorizontal(string line)
        {
            int start = 0;
            int end = line.Length;
            while (start < end && IsHorizontalSpace(line[start]))
                start++;
            while (end > start && IsHorizontalSpace(line[end - 1]))
                end--;
            return line.Substring(start, end - start);
        }

        private static string LeadingHorizontal(string line)
        {
            int i = 0;
            while (i < line.Length && IsHorizontalSpace(line[i]))
                i++;
            return line.Substring(0, i);
        }

        private static string TrailingHorizontal(string line)
        {
            int i = line.Length;
            while (i > 0 && IsHorizontalSpace(line[i - 1]))
                i--;
            return line.Substring(i);
        }

        /// <summary>Trims whitespace plus the invisible marks some engines add at the ends (BOM, zero-width space, bidi marks).</summary>
        internal static string TrimAll(string text)
        {
            int start = 0;
            int end = text.Length;
            while (start < end && IsTrimmable(text[start]))
                start++;
            while (end > start && IsTrimmable(text[end - 1]))
                end--;
            return text.Substring(start, end - start);
        }

        private static bool IsTrimmable(char c)
        {
            return char.IsWhiteSpace(c) || c == '\uFEFF' || c == '\u200B' || c == '\u200E' || c == '\u200F';
        }
    }
}
