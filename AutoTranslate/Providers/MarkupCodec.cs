using System;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoTranslate.Providers
{
    /// <summary>
    /// Converts masked plain text to markup for engines with a markup mode (DeepL XML, Microsoft HTML) and back:
    /// the text is escaped, TextMasking's plain tokens become elements the engine never translates, and on the way
    /// back the elements become plain tokens again and entities are decoded.
    /// </summary>
    internal sealed class MarkupCodec
    {
        private static readonly Regex Entity = new Regex("&(#[0-9]{1,7}|#[xX][0-9A-Fa-f]{1,6}|[A-Za-z][A-Za-z0-9]{1,31});", RegexOptions.CultureInvariant);
        private static readonly Regex LeftoverTag = new Regex("</?[A-Za-z][^<>]*>", RegexOptions.CultureInvariant);

        private readonly Func<string, string> _element;
        private readonly Regex _elementPattern;
        private readonly string _lineBreakElement;
        private readonly Regex _lineBreakPattern;

        /// <param name="element">Markup for a token, given its index letters ("A").</param>
        /// <param name="elementPattern">Finds that markup in the answer; group "id" holds the index letters.</param>
        /// <param name="lineBreakElement">Markup for a line break, or null to keep line breaks as text.</param>
        /// <param name="lineBreakPattern">Finds the line-break markup in the answer.</param>
        public MarkupCodec(Func<string, string> element, Regex elementPattern, string lineBreakElement = null, Regex lineBreakPattern = null)
        {
            _element = element;
            _elementPattern = elementPattern;
            _lineBreakElement = lineBreakElement;
            _lineBreakPattern = lineBreakPattern;
        }

        public string Encode(string masked)
        {
            var builder = new StringBuilder(masked.Length + 64);
            int position = 0;
            foreach (Match match in TextMasking.CanonicalToken.Matches(masked))
            {
                AppendEscaped(builder, masked, position, match.Index);
                builder.Append(_element(match.Groups[1].Value));
                position = match.Index + match.Length;
            }
            AppendEscaped(builder, masked, position, masked.Length);
            return builder.ToString();
        }

        public string Decode(string markup)
        {
            if (markup == null)
                return null;
            string text = _elementPattern.Replace(markup, match => "ZM" + match.Groups["id"].Value.Replace(" ", "").Replace("\u00A0", "").ToUpperInvariant() + "Z");
            if (_lineBreakPattern != null)
                text = _lineBreakPattern.Replace(text, "\n");
            // Any other element is the engine's own (it should not add any): drop it rather than show markup.
            text = LeftoverTag.Replace(text, "");
            return DecodeEntities(text);
        }

        private void AppendEscaped(StringBuilder builder, string text, int from, int to)
        {
            for (int i = from; i < to; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '&': builder.Append("&amp;"); break;
                    case '<': builder.Append("&lt;"); break;
                    case '>': builder.Append("&gt;"); break;
                    case '\r':
                        if (_lineBreakElement != null)
                        {
                            if (i + 1 < to && text[i + 1] == '\n')
                                i++;
                            builder.Append(_lineBreakElement);
                        }
                        else
                        {
                            builder.Append(c);
                        }
                        break;
                    case '\n':
                    case '\u2028':
                    case '\u2029':
                        if (_lineBreakElement != null)
                            builder.Append(_lineBreakElement);
                        else
                            builder.Append(c);
                        break;
                    default: builder.Append(c); break;
                }
            }
        }

        private static string DecodeEntities(string text)
        {
            if (text.IndexOf('&') < 0)
                return text;
            return Entity.Replace(text, match =>
            {
                string name = match.Groups[1].Value;
                if (name[0] == '#')
                {
                    int code;
                    bool ok = name.Length > 1 && (name[1] == 'x' || name[1] == 'X')
                        ? int.TryParse(name.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)
                        : int.TryParse(name.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out code);
                    if (!ok || code < 0 || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF))
                        return match.Value;
                    return char.ConvertFromUtf32(code);
                }
                switch (name)
                {
                    case "amp": return "&";
                    case "lt": return "<";
                    case "gt": return ">";
                    case "quot": return "\"";
                    case "apos": return "'";
                    case "nbsp": return "\u00A0";
                    default:
                        string decoded = WebUtility.HtmlDecode(match.Value);
                        return decoded;
                }
            });
        }
    }
}
