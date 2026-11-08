using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoTranslate.Core;
using Newtonsoft.Json.Linq;

namespace AutoTranslate.Providers
{
    /// <summary>
    /// Google Translate's free "gtx" web endpoint (no key). Several texts go into one request as separate lines:
    /// Google translates line by line and keeps the line structure, so the answer is split back by lines. When the
    /// number of lines differs, the texts of that request are translated one by one instead. Plain text only, so
    /// tokens are TextMasking's plain "ZMAZ" tokens. Rate limiting answers HTTP 429 (sometimes 503); those propagate as
    /// <see cref="TranslationHttpException"/> so the service backs off. Run with concurrency 1.
    /// </summary>
    internal sealed class GoogleFreeProvider : TranslationProviderBase
    {
        private const string DefaultBaseUrl = "https://translate.googleapis.com";
        /// <summary>Longest GET URL; longer requests are sent as POST forms.</summary>
        private const int MaxGetUrlLength = 2000;

        private readonly string _endpoint;

        public GoogleFreeProvider(ProviderOptions options) : base(options)
        {
            string baseUrl = Options.ServiceUrl.Length > 0 ? RequireHttpUrl(Options.ServiceUrl, "Google") : DefaultBaseUrl;
            _endpoint = baseUrl.TrimEnd('/').EndsWith("/translate_a/single", StringComparison.OrdinalIgnoreCase)
                ? baseUrl.TrimEnd('/')
                : Combine(baseUrl, "translate_a/single");
        }

        public override ProviderKind Kind => ProviderKind.GoogleFree;
        public override int MaxTextsPerRequest => 40;
        /// <summary>Google's web limit is 5000 characters per request; a margin is kept for the line separators.</summary>
        public override int MaxCharsPerRequest => 4500;
        protected override string EngineName => "Google";

        protected override int CostOf(string text)
        {
            return text.Length + 1; // the line separator
        }

        protected override async Task<string[]> TranslateBatchAsync(IReadOnlyList<string> texts, string sourceCode, string targetCode, CancellationToken cancellation)
        {
            var results = new string[texts.Count];
            if (texts.Count == 1)
            {
                results[0] = await TranslateOneAsync(texts[0], sourceCode, targetCode, cancellation).ConfigureAwait(false);
                return results;
            }

            // One line per source line; remember how many lines each text has.
            var lineCounts = new int[texts.Count];
            var joined = new StringBuilder();
            for (int i = 0; i < texts.Count; i++)
            {
                string normalized = TextMasking.LineBreak.Replace(texts[i], "\n");
                lineCounts[i] = CountLines(normalized);
                if (i > 0)
                    joined.Append('\n');
                joined.Append(normalized);
            }

            string translation = null;
            try
            {
                string body = await RequestAsync(joined.ToString(), sourceCode, targetCode, cancellation).ConfigureAwait(false);
                translation = ParseTranslation(body);
            }
            catch (InvalidDataException)
            {
                translation = null; // unexpected shape: try one by one
            }

            if (translation != null)
            {
                string[] lines = TextMasking.LineBreak.Split(TrimTrailingLineBreak(translation));
                int expected = 0;
                foreach (int count in lineCounts)
                    expected += count;
                if (lines.Length == expected)
                {
                    int line = 0;
                    for (int i = 0; i < texts.Count; i++)
                    {
                        results[i] = string.Join("\n", lines, line, lineCounts[i]);
                        line += lineCounts[i];
                    }
                    return results;
                }
            }

            // Google merged or split lines: translate each text on its own.
            for (int i = 0; i < texts.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                results[i] = await TranslateOneAsync(texts[i], sourceCode, targetCode, cancellation).ConfigureAwait(false);
            }
            return results;
        }

        private async Task<string> TranslateOneAsync(string text, string sourceCode, string targetCode, CancellationToken cancellation)
        {
            string normalized = TextMasking.LineBreak.Replace(text, "\n");
            string body = await RequestAsync(normalized, sourceCode, targetCode, cancellation).ConfigureAwait(false);
            try
            {
                string translation = ParseTranslation(body);
                return translation == null ? null : TrimTrailingLineBreak(translation);
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        private async Task<string> RequestAsync(string text, string sourceCode, string targetCode, CancellationToken cancellation)
        {
            ThrowIfDisposed();
            string query = "client=gtx&ie=UTF-8&oe=UTF-8&dt=t"
                + "&sl=" + HttpTransport.EscapeData(sourceCode)
                + "&tl=" + HttpTransport.EscapeData(targetCode);
            string getUrl = _endpoint + "?" + query + "&q=" + HttpTransport.EscapeData(text);

            HttpRequestMessage request;
            if (getUrl.Length <= MaxGetUrlLength)
            {
                request = new HttpRequestMessage(HttpMethod.Get, getUrl);
            }
            else
            {
                request = new HttpRequestMessage(HttpMethod.Post, _endpoint + "?" + query)
                {
                    Content = HttpTransport.Form(new[] { new KeyValuePair<string, string>("q", text) }),
                };
            }
            using (request)
            {
                request.Headers.TryAddWithoutValidation("User-Agent", HttpTransport.BrowserUserAgent);
                request.Headers.TryAddWithoutValidation("Accept", "*/*");
                request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                return await HttpTransport.SendAsync(request, Options.TimeoutSeconds, cancellation, EngineName).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Concatenates the translated segments of <c>[[["translated","source",...],...],null,"en",...]</c>.
        /// Segments are sentences; the line breaks are inside their texts.
        /// </summary>
        /// <exception cref="InvalidDataException">Not the expected JSON.</exception>
        internal static string ParseTranslation(string body)
        {
            JToken root;
            try
            {
                root = JToken.Parse(body);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("Google returned a response that is not JSON.", exception);
            }
            var array = root as JArray;
            if (array == null || array.Count == 0)
                throw new InvalidDataException("Google returned an unexpected response.");
            if (array[0].Type == JTokenType.Null)
                return ""; // nothing translatable (e.g. only punctuation)
            var segments = array[0] as JArray;
            if (segments == null)
                throw new InvalidDataException("Google returned an unexpected response.");

            var builder = new StringBuilder();
            foreach (JToken segment in segments)
            {
                var parts = segment as JArray;
                if (parts == null || parts.Count == 0 || parts[0].Type != JTokenType.String)
                    continue; // e.g. transliteration-only entries
                builder.Append((string)parts[0]);
            }
            return builder.ToString();
        }

        private static int CountLines(string normalized)
        {
            int lines = 1;
            foreach (char c in normalized)
            {
                if (c == '\n')
                    lines++;
            }
            return lines;
        }

        private static string TrimTrailingLineBreak(string text)
        {
            // The input never ends with a line break (texts are trimmed), so one at the end was added by Google.
            int end = text.Length;
            while (end > 0 && (text[end - 1] == '\n' || text[end - 1] == '\r'))
                end--;
            return text.Substring(0, end);
        }
    }
}
