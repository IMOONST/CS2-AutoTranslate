using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AutoTranslate.Providers
{
    /// <summary>
    /// An HTTP failure of a translation engine. <see cref="Exception.Message"/> starts with "HTTP &lt;code&gt;" (e.g.
    /// "HTTP 429 Too Many Requests: ...") so the service can recognise rate limiting (429), quota (456) and outages
    /// (5xx). Messages never contain the API key.
    /// </summary>
    public sealed class TranslationHttpException : Exception
    {
        public TranslationHttpException(int statusCode, string message) : base(message)
        {
            StatusCode = statusCode;
        }

        /// <summary>The HTTP status code.</summary>
        public int StatusCode { get; }

        /// <summary>429 / 503 / 456: back off before the next request.</summary>
        public bool IsRateLimit => StatusCode == 429 || StatusCode == 503 || StatusCode == 456;
    }

    /// <summary>
    /// The one HttpClient all providers share (HttpClient is thread-safe and meant to be reused; one instance per
    /// request would exhaust sockets). Adds per-request timeouts, gzip, UTF-8 and key-free error messages.
    /// </summary>
    internal static class HttpTransport
    {
        /// <summary>A current desktop browser; Google's free endpoint answers bots without one with 403/429 sooner.</summary>
        public const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        private const int MaxErrorBodyLength = 300;
        private static readonly HttpClient Client;
        private static readonly Regex HtmlTag = new Regex("<[^>]*>", RegexOptions.CultureInvariant);
        private static readonly Regex Spaces = new Regex("\\s+", RegexOptions.CultureInvariant);

        static HttpTransport()
        {
            try
            {
                // Older Mono / .NET Framework defaults may not offer TLS 1.2, which every engine requires.
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch (Exception)
            {
                // The runtime decides the protocols itself (e.g. Unity's unitytls backend): nothing to set.
            }
            try
            {
                ServicePointManager.Expect100Continue = false;
                if (ServicePointManager.DefaultConnectionLimit < 8)
                    ServicePointManager.DefaultConnectionLimit = 8;
            }
            catch (Exception)
            {
                // Not supported by this runtime; defaults are fine.
            }

            var handler = new HttpClientHandler();
            try
            {
                if (handler.SupportsAutomaticDecompression)
                    handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            }
            catch (Exception)
            {
                // Decompression unsupported: responses arrive uncompressed because no Accept-Encoding is sent.
            }
            try
            {
                handler.UseCookies = false;
            }
            catch (Exception)
            {
                // Ignored: cookies are harmless.
            }

            Client = new HttpClient(handler, true)
            {
                // Each request has its own timeout (see SendAsync); the client-wide one must not interfere.
                Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            };
        }

        /// <summary>
        /// Sends a request and returns the body of a successful response as text (UTF-8 unless the server says otherwise).
        /// </summary>
        /// <exception cref="TranslationHttpException">Non-success status code.</exception>
        /// <exception cref="TimeoutException">No complete answer within <paramref name="timeoutSeconds"/>.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellation"/> was cancelled.</exception>
        /// <exception cref="System.IO.IOException">Network failure (message without secrets).</exception>
        public static async Task<string> SendAsync(HttpRequestMessage request, int timeoutSeconds, CancellationToken cancellation, string engineName, params string[] secrets)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            cancellation.ThrowIfCancellationRequested();
            int seconds = timeoutSeconds <= 0 ? 20 : Math.Min(timeoutSeconds, 300);

            if (request.Headers.UserAgent.Count == 0)
                request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            if (request.Headers.Accept.Count == 0)
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.AcceptCharset.Add(new StringWithQualityHeaderValue("utf-8"));

            using (var timeout = new CancellationTokenSource())
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
                try
                {
                    using (HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false))
                    {
                        string body = await ReadBodyAsync(response).ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            int code = (int)response.StatusCode;
                            string reason = string.IsNullOrEmpty(response.ReasonPhrase) ? DefaultReason(code) : response.ReasonPhrase;
                            string detail = Describe(body, secrets);
                            string message = "HTTP " + code + " " + reason + " (" + engineName + ")" + (detail.Length > 0 ? ": " + detail : "");
                            throw new TranslationHttpException(code, Sanitize(message, secrets));
                        }
                        return body;
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellation);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    throw new TimeoutException(engineName + " did not answer within " + seconds + " s.");
                }
                catch (HttpRequestException exception)
                {
                    throw new System.IO.IOException(Sanitize(engineName + " request failed: " + FlattenMessage(exception), secrets));
                }
                catch (WebException exception)
                {
                    throw new System.IO.IOException(Sanitize(engineName + " request failed: " + FlattenMessage(exception), secrets));
                }
            }
        }

        /// <summary>A UTF-8 JSON body.</summary>
        public static HttpContent Json(string json)
        {
            return new StringContent(json, Encoding.UTF8, "application/json");
        }

        /// <summary>An application/x-www-form-urlencoded body (UTF-8, no length limit unlike FormUrlEncodedContent on old runtimes).</summary>
        public static HttpContent Form(IEnumerable<KeyValuePair<string, string>> fields)
        {
            var builder = new StringBuilder();
            foreach (KeyValuePair<string, string> field in fields)
            {
                if (builder.Length > 0)
                    builder.Append('&');
                builder.Append(EscapeData(field.Key)).Append('=').Append(EscapeData(field.Value ?? ""));
            }
            var content = new StringContent(builder.ToString(), Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded") { CharSet = "utf-8" };
            return content;
        }

        /// <summary>Percent-encodes UTF-8 (RFC 3986), in chunks because old runtimes limit Uri.EscapeDataString's input.</summary>
        public static string EscapeData(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            const int chunk = 16000;
            if (value.Length <= chunk)
                return Uri.EscapeDataString(value);
            var builder = new StringBuilder(value.Length * 3);
            int position = 0;
            while (position < value.Length)
            {
                int length = Math.Min(chunk, value.Length - position);
                if (length < value.Length - position && char.IsHighSurrogate(value[position + length - 1]))
                    length--; // never split a surrogate pair
                builder.Append(Uri.EscapeDataString(value.Substring(position, length)));
                position += length;
            }
            return builder.ToString();
        }

        /// <summary>Removes every secret (and its URL-encoded form) from a message.</summary>
        public static string Sanitize(string message, string[] secrets)
        {
            if (string.IsNullOrEmpty(message) || secrets == null)
                return message ?? "";
            foreach (string secret in secrets)
            {
                if (string.IsNullOrEmpty(secret) || secret.Length < 4)
                    continue;
                message = message.Replace(secret, "***");
                string escaped = Uri.EscapeDataString(secret);
                if (escaped != secret)
                    message = message.Replace(escaped, "***");
            }
            return message;
        }

        private static async Task<string> ReadBodyAsync(HttpResponseMessage response)
        {
            if (response.Content == null)
                return "";
            byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes == null || bytes.Length == 0)
                return "";
            Encoding encoding = Encoding.UTF8;
            string charset = response.Content.Headers.ContentType == null ? null : response.Content.Headers.ContentType.CharSet;
            if (!string.IsNullOrEmpty(charset))
            {
                try
                {
                    encoding = Encoding.GetEncoding(charset.Trim('"', ' '));
                }
                catch (Exception)
                {
                    encoding = Encoding.UTF8;
                }
            }
            string text = encoding.GetString(bytes);
            return text.Length > 0 && text[0] == '\uFEFF' ? text.Substring(1) : text;
        }

        /// <summary>A short, single-line excerpt of an error body (HTML stripped; JSON "message"/"error" preferred).</summary>
        private static string Describe(string body, string[] secrets)
        {
            if (string.IsNullOrEmpty(body))
                return "";
            string text = body.Trim();
            try
            {
                if (text.StartsWith("{", StringComparison.Ordinal))
                {
                    var json = Newtonsoft.Json.Linq.JObject.Parse(text);
                    Newtonsoft.Json.Linq.JToken token = json["message"] ?? json["error"];
                    if (token != null && token.Type == Newtonsoft.Json.Linq.JTokenType.Object)
                        token = token["message"] ?? token;
                    if (token != null)
                        text = token.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)token : token.ToString(Newtonsoft.Json.Formatting.None);
                }
            }
            catch (Exception)
            {
                // Not JSON: use the raw body.
            }
            if (text.IndexOf('<') >= 0)
            {
                Match title = Regex.Match(text, "<title>([^<]*)</title>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                text = title.Success ? title.Groups[1].Value : HtmlTag.Replace(text, " ");
                text = WebUtility.HtmlDecode(text);
            }
            text = Spaces.Replace(text, " ").Trim();
            if (text.Length > MaxErrorBodyLength)
                text = text.Substring(0, MaxErrorBodyLength) + "…";
            return Sanitize(text, secrets);
        }

        private static string FlattenMessage(Exception exception)
        {
            var builder = new StringBuilder();
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                if (string.IsNullOrEmpty(current.Message))
                    continue;
                if (builder.Length > 0)
                    builder.Append(" → ");
                builder.Append(current.Message);
                if (builder.Length > 600)
                    break;
            }
            return builder.ToString();
        }

        private static string DefaultReason(int code)
        {
            switch (code)
            {
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 413: return "Payload Too Large";
                case 414: return "URI Too Long";
                case 429: return "Too Many Requests";
                case 456: return "Quota Exceeded";
                case 500: return "Internal Server Error";
                case 502: return "Bad Gateway";
                case 503: return "Service Unavailable";
                case 504: return "Gateway Timeout";
                default: return "Error";
            }
        }
    }
}
