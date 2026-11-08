using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutoTranslate.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoTranslate.Providers
{
    /// <summary>
    /// Microsoft Translator / Azure AI Translator, Text API v3. Up to 100 texts and 50 000 characters per request.
    /// Sent with textType=html: tokens become &lt;span class="notranslate"&gt;ZMAZ&lt;/span&gt; (documented as never
    /// translated) and line breaks &lt;br/&gt; (HTML would collapse raw line breaks).
    /// </summary>
    internal sealed class MicrosoftProvider : TranslationProviderBase
    {
        private const string DefaultBaseUrl = "https://api.cognitive.microsofttranslator.com";

        internal static readonly MarkupCodec Codec = new MarkupCodec(
            letters => "<span class=\"notranslate\">ZM" + letters + "Z</span>",
            new Regex("<\\s*span\\b[^>]*notranslate[^>]*>\\s*[ZＺz][ \u00A0]?[MＭm][ \u00A0]?(?<id>(?:[A-Ya-y][ \u00A0]?){1,3}?)[ZＺz]\\s*<\\s*/\\s*span\\s*>", RegexOptions.CultureInvariant),
            "<br/>",
            new Regex("<\\s*br\\s*/?\\s*>(?:\\s*<\\s*/\\s*br\\s*>)?", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase));

        private readonly string _endpoint;

        public MicrosoftProvider(ProviderOptions options) : base(options)
        {
            if (Options.ApiKey.Length == 0)
                throw new InvalidOperationException("Microsoft Translator needs a key (Azure portal → Translator resource → Keys and Endpoint).");
            string baseUrl = Options.ServiceUrl.Length > 0 ? RequireHttpUrl(Options.ServiceUrl, "Microsoft Translator").TrimEnd('/') : DefaultBaseUrl;
            int query = baseUrl.IndexOf('?');
            string path = query < 0 ? baseUrl : baseUrl.Substring(0, query);
            _endpoint = path.EndsWith("/translate", StringComparison.OrdinalIgnoreCase) ? baseUrl : Combine(path, "translate") + (query < 0 ? "" : baseUrl.Substring(query));
        }

        public override ProviderKind Kind => ProviderKind.Microsoft;
        public override int MaxTextsPerRequest => 100;
        /// <summary>The API's limit counts the encoded (HTML) text: see <see cref="CostOf"/>.</summary>
        public override int MaxCharsPerRequest => 50000;
        protected override string EngineName => "Microsoft Translator";

        protected override int CostOf(string text)
        {
            return Codec.Encode(text).Length;
        }

        protected override async Task<string[]> TranslateBatchAsync(IReadOnlyList<string> texts, string sourceCode, string targetCode, CancellationToken cancellation)
        {
            ThrowIfDisposed();
            var body = new JArray();
            foreach (string text in texts)
                body.Add(new JObject { ["Text"] = Codec.Encode(text) });

            string url = _endpoint + (_endpoint.IndexOf('?') < 0 ? "?" : "&")
                + "api-version=3.0&textType=html&to=" + HttpTransport.EscapeData(targetCode)
                + (sourceCode != LanguageCodes.Auto ? "&from=" + HttpTransport.EscapeData(sourceCode) : "");

            string response;
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", Options.ApiKey);
                if (Options.Region.Length > 0)
                    request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Region", Options.Region);
                request.Headers.TryAddWithoutValidation("X-ClientTraceId", Guid.NewGuid().ToString());
                request.Headers.TryAddWithoutValidation("User-Agent", "AutoTranslate-CS2/1.0");
                request.Content = HttpTransport.Json(body.ToString(Formatting.None));
                try
                {
                    response = await HttpTransport.SendAsync(request, Options.TimeoutSeconds, cancellation, EngineName, Options.ApiKey).ConfigureAwait(false);
                }
                catch (TranslationHttpException exception) when (exception.StatusCode == 401)
                {
                    throw new TranslationHttpException(401, exception.Message
                        + (Options.Region.Length == 0 ? " — check the key; a regional or multi-service resource also needs its region." : " — check the key and the region."));
                }
            }

            return ParseResults(response, texts.Count, EngineName);
        }

        /// <summary>The decoded translations of a Translator v3 response, one per input (null where missing).</summary>
        internal static string[] ParseResults(string response, int count, string engineName)
        {
            JArray items;
            try
            {
                items = JToken.Parse(response) as JArray;
            }
            catch (Exception exception)
            {
                throw new InvalidDataException(engineName + " returned a response that is not JSON.", exception);
            }
            if (items == null || items.Count != count)
                throw new InvalidDataException(engineName + " returned " + (items == null ? 0 : items.Count) + " results for " + count + " texts.");

            var results = new string[count];
            for (int i = 0; i < count; i++)
            {
                var translations = items[i]["translations"] as JArray;
                JToken text = translations != null && translations.Count > 0 ? translations[0]["text"] : null;
                results[i] = text != null && text.Type == JTokenType.String ? Codec.Decode((string)text) : null;
            }
            return results;
        }
    }
}
