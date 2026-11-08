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
    /// DeepL API v2 (free keys end with ":fx" and use api-free.deepl.com). Up to 50 texts per request. Tokens are sent
    /// as empty XML elements &lt;x id="A"/&gt; with tag_handling=xml, which DeepL keeps exactly and places correctly in
    /// the translated sentence; line breaks stay as text and split sentences (split_sentences=1).
    /// </summary>
    internal sealed class DeepLProvider : TranslationProviderBase
    {
        private const string FreeUrl = "https://api-free.deepl.com/v2/translate";
        private const string ProUrl = "https://api.deepl.com/v2/translate";

        private static readonly MarkupCodec Codec = new MarkupCodec(
            letters => "<x id=\"" + letters + "\"/>",
            new Regex("<\\s*x\\s+id\\s*=\\s*[\"']?(?<id>[A-Ya-y]{1,3})[\"']?\\s*(?:/\\s*>|>\\s*<\\s*/\\s*x\\s*>)", RegexOptions.CultureInvariant));

        private readonly string _endpoint;

        public DeepLProvider(ProviderOptions options) : base(options)
        {
            if (Options.ApiKey.Length == 0)
                throw new InvalidOperationException("DeepL needs an API key (Account → API keys on deepl.com).");
            if (Options.ServiceUrl.Length > 0)
            {
                string url = RequireHttpUrl(Options.ServiceUrl, "DeepL").TrimEnd('/');
                if (url.EndsWith("/translate", StringComparison.OrdinalIgnoreCase))
                    _endpoint = url;
                else if (url.EndsWith("/v2", StringComparison.OrdinalIgnoreCase))
                    _endpoint = url + "/translate";
                else
                    _endpoint = url + "/v2/translate";
            }
            else
            {
                _endpoint = Options.ApiKey.EndsWith(":fx", StringComparison.OrdinalIgnoreCase) ? FreeUrl : ProUrl;
            }
        }

        public override ProviderKind Kind => ProviderKind.DeepL;
        public override int MaxTextsPerRequest => 50;
        /// <summary>DeepL accepts 128 KiB per request body; the encoded texts stay far below that.</summary>
        public override int MaxCharsPerRequest => 30000;
        protected override string EngineName => "DeepL";

        protected override int CostOf(string text)
        {
            return Codec.Encode(text).Length;
        }

        protected override async Task<string[]> TranslateBatchAsync(IReadOnlyList<string> texts, string sourceCode, string targetCode, CancellationToken cancellation)
        {
            ThrowIfDisposed();
            var encoded = new JArray();
            foreach (string text in texts)
                encoded.Add(Codec.Encode(text));

            var body = new JObject
            {
                ["text"] = encoded,
                ["target_lang"] = targetCode,
                ["tag_handling"] = "xml",
                ["split_sentences"] = "1",
                ["outline_detection"] = false,
            };
            if (sourceCode != LanguageCodes.Auto)
                body["source_lang"] = SourceCode(sourceCode);

            string response;
            using (var request = new HttpRequestMessage(HttpMethod.Post, _endpoint))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + Options.ApiKey);
                request.Headers.TryAddWithoutValidation("User-Agent", "AutoTranslate-CS2/1.0");
                request.Content = HttpTransport.Json(body.ToString(Formatting.None));
                try
                {
                    response = await HttpTransport.SendAsync(request, Options.TimeoutSeconds, cancellation, EngineName, Options.ApiKey).ConfigureAwait(false);
                }
                catch (TranslationHttpException exception) when (exception.StatusCode == 403 || exception.StatusCode == 401)
                {
                    throw new TranslationHttpException(exception.StatusCode, exception.Message + " — the DeepL key was rejected (a free key must end with \":fx\").");
                }
                catch (TranslationHttpException exception) when (exception.StatusCode == 456)
                {
                    throw new TranslationHttpException(456, exception.Message + " — the DeepL character quota of this billing period is used up.");
                }
            }

            JArray translations;
            try
            {
                translations = JObject.Parse(response)["translations"] as JArray;
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("DeepL returned a response that is not JSON.", exception);
            }
            if (translations == null || translations.Count != texts.Count)
                throw new InvalidDataException("DeepL returned " + (translations == null ? 0 : translations.Count) + " translations for " + texts.Count + " texts.");

            var results = new string[texts.Count];
            for (int i = 0; i < texts.Count; i++)
            {
                JToken text = translations[i]["text"];
                results[i] = text != null && text.Type == JTokenType.String ? Codec.Decode((string)text) : null;
            }
            return results;
        }

        /// <summary>DeepL source languages have no variants: "EN-US" → "EN", "PT-BR" → "PT", "ZH-HANS" → "ZH".</summary>
        private static string SourceCode(string targetStyleCode)
        {
            int dash = targetStyleCode.IndexOf('-');
            return (dash < 0 ? targetStyleCode : targetStyleCode.Substring(0, dash)).ToUpperInvariant();
        }
    }
}
