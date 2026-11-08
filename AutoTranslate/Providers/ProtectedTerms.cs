using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoTranslate.Providers
{
    /// <summary>
    /// Words and phrases that must come back from the translation engine exactly as written ("Al Qassim",
    /// "Sign Builder"). <see cref="TextMasking.Mask(string, ProtectedTerms)"/> replaces each occurrence by a token, so the
    /// engine translates the rest of the text and the term is restored verbatim (with the casing of the original text).
    /// <para>
    /// Matching: case-insensitive, whole words (a term that starts or ends with a letter or digit does not match inside
    /// a longer word: "Sign" does not match "Signal"), any run of spaces or tabs inside a term matches any run of spaces
    /// or tabs in the text, and at one position the longest term wins ("Sign Builder Pro" before "Sign Builder").
    /// </para>
    /// Immutable and thread-safe; the regular expression is built once, when the list is created, so a list should be
    /// created only when the terms change.
    /// </summary>
    public sealed class ProtectedTerms
    {
        /// <summary>Longest accepted term (longer entries are sentences, which belong in the "texts" list).</summary>
        public const int MaxTermLength = 200;

        /// <summary>Most terms used (the rest are ignored, with a note in <see cref="Ignored"/>).</summary>
        public const int MaxTerms = 2000;

        public static readonly ProtectedTerms Empty = new ProtectedTerms(new List<string>(), 0);

        private static readonly StringComparer kComparer = StringComparer.OrdinalIgnoreCase;
        private const string WordChar = "[\\p{L}\\p{N}\\p{Mn}\\p{Mc}_]";

        private readonly List<string> m_Terms;
        private readonly HashSet<string> m_Set;
        private readonly Regex m_Regex;

        private ProtectedTerms(List<string> terms, int ignored)
        {
            m_Terms = terms;
            m_Set = new HashSet<string>(terms, kComparer);
            Ignored = ignored;
            m_Regex = terms.Count == 0 ? null : new Regex(BuildPattern(terms), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        /// <summary>The terms in use, normalized (trimmed, inner whitespace collapsed), longest first, without case-insensitive duplicates.</summary>
        public IReadOnlyList<string> Terms => m_Terms;

        public int Count => m_Terms.Count;

        public bool IsEmpty => m_Terms.Count == 0;

        /// <summary>Entries that were skipped (empty, without a letter or digit, too long, or over <see cref="MaxTerms"/>).</summary>
        public int Ignored { get; }

        /// <summary>Builds a list from raw entries (null and invalid entries are skipped).</summary>
        public static ProtectedTerms Create(IEnumerable<string> terms)
        {
            if (terms == null) return Empty;
            var seen = new HashSet<string>(kComparer);
            var list = new List<string>();
            int ignored = 0;
            foreach (var raw in terms)
            {
                var term = Normalize(raw);
                if (term == null)
                {
                    if (!string.IsNullOrWhiteSpace(raw)) ignored++;
                    continue;
                }
                if (!seen.Add(term)) continue;
                if (list.Count >= MaxTerms)
                {
                    ignored++;
                    continue;
                }
                list.Add(term);
            }
            if (list.Count == 0 && ignored == 0) return Empty;
            // Longest first: the alternation then prefers the longest term at a position. Ties in a stable, readable order.
            list.Sort((a, b) => a.Length != b.Length ? b.Length.CompareTo(a.Length) : string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
            return new ProtectedTerms(list, ignored);
        }

        /// <summary>True when the text contains at least one of the terms.</summary>
        public bool Matches(string text)
        {
            return m_Regex != null && !string.IsNullOrEmpty(text) && m_Regex.IsMatch(text);
        }

        /// <summary>True when the text, trimmed, is exactly one of the terms (ignoring case).</summary>
        public bool IsTerm(string text)
        {
            if (m_Regex == null || string.IsNullOrEmpty(text)) return false;
            // Most texts are already in normal form: a plain set lookup, no allocation.
            if (IsNormalized(text)) return text.Length <= MaxTermLength && m_Set.Contains(text);
            var normalized = Normalize(text);
            return normalized != null && m_Set.Contains(normalized);
        }

        /// <summary>No outer whitespace, no control character, and only single plain spaces inside.</summary>
        private static bool IsNormalized(string text)
        {
            if (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[text.Length - 1])) return false;
            for (int i = 1; i < text.Length - 1; i++)
            {
                char c = text[i];
                if (char.IsControl(c)) return false;
                if (char.IsWhiteSpace(c) && (c != ' ' || char.IsWhiteSpace(text[i + 1]))) return false;
            }
            return !char.IsControl(text[0]) && !char.IsControl(text[text.Length - 1]);
        }

        /// <summary>True when both lists hold the same terms (ignoring case and order).</summary>
        public bool SameTerms(ProtectedTerms other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other == null) return IsEmpty;
            return m_Set.SetEquals(other.m_Set);
        }

        /// <summary>The terms of this list that are not in <paramref name="other"/> and the reverse (ignoring case).</summary>
        public List<string> Difference(IEnumerable<string> other)
        {
            var otherSet = new HashSet<string>(kComparer);
            if (other != null)
            {
                foreach (var raw in other)
                {
                    var term = Normalize(raw);
                    if (term != null) otherSet.Add(term);
                }
            }
            var result = new List<string>();
            foreach (var term in m_Terms)
                if (!otherSet.Contains(term)) result.Add(term);
            foreach (var term in otherSet)
                if (!m_Set.Contains(term)) result.Add(term);
            return result;
        }

        /// <summary>Adds the (start, end) ranges of every occurrence in <paramref name="text"/>.</summary>
        internal void AddMatches(string text, List<KeyValuePair<int, int>> ranges)
        {
            if (m_Regex == null || string.IsNullOrEmpty(text)) return;
            for (var match = m_Regex.Match(text); match.Success; match = match.NextMatch())
            {
                if (match.Length > 0) ranges.Add(new KeyValuePair<int, int>(match.Index, match.Index + match.Length));
            }
        }

        /// <summary>Trimmed, inner whitespace runs collapsed to one space; null when not a usable term.</summary>
        internal static string Normalize(string raw)
        {
            if (raw == null) return null;
            var builder = new StringBuilder(raw.Length);
            bool pendingSpace = false;
            bool hasWordChar = false;
            foreach (char c in raw)
            {
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = builder.Length > 0;
                    continue;
                }
                if (char.IsControl(c)) continue;
                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }
                builder.Append(c);
                if (char.IsLetterOrDigit(c)) hasWordChar = true;
            }
            if (builder.Length == 0 || !hasWordChar || builder.Length > MaxTermLength) return null;
            return builder.ToString();
        }

        private static string BuildPattern(List<string> terms)
        {
            var pattern = new StringBuilder(terms.Count * 24);
            for (int i = 0; i < terms.Count; i++)
            {
                var term = terms[i];
                if (i > 0) pattern.Append('|');
                pattern.Append("(?:");
                if (IsWordChar(term[0])) pattern.Append("(?<!").Append(WordChar).Append(')');
                var words = term.Split(' ');
                for (int w = 0; w < words.Length; w++)
                {
                    if (w > 0) pattern.Append("[ \\t\\u00A0\\u202F]+");
                    pattern.Append(Regex.Escape(words[w]));
                }
                if (IsWordChar(term[term.Length - 1])) pattern.Append("(?!").Append(WordChar).Append(')');
                pattern.Append(')');
            }
            return pattern.ToString();
        }

        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark
                || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.SpacingCombiningMark;
        }
    }
}
