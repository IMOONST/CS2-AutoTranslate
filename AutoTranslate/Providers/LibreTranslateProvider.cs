using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutoTranslate.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoTranslate.Providers
{
    /// <summary>
    /// LibreTranslate (self-hosted or a public instance): POST {url}/translate with format "text" and TextMasking's
    /// plain tokens (its HTML mode translates every text node separately, which breaks sentences apart). Recent
    /// versions accept an array in "q"; when a server rejects arrays the provider remembers that and sends one text
    /// per request from then on.
    /// </summary>
    internal sealed class LibreTranslateProvider : TranslationProviderBase
    {
        private readonly string _endpoint;
        private volatile bool _arraysUnsupported;

        public LibreTranslateProvider(ProviderOptions options) : base(options)
        {
            if (Options.ServiceUrl.Length == 0)
                throw new InvalidOperationException("LibreTranslate needs the server URL (for example http://localhost:5000).");
            string url = RequireHttpUrl(Options.ServiceUrl, "LibreTranslate").TrimEnd('/');
            _endpoint = url.EndsWith("/translate", StringComparison.OrdinalIgnoreCase) ? url : url + "/translate";
        }

        public override ProviderKind Kind => ProviderKind.LibreTranslate;
        public override int MaxTextsPerRequest => 25;
        /// <summary>Public instances commonly limit a request to a few thousand characters (char_limit).</summary>
        public override int MaxCharsPerRequest => 4000;
        protected override string EngineName => "LibreTranslate";

        protected override async Task<string[]> TranslateBatchAsync(IReadOnlyList<string> texts, string sourceCode, string targetCode, CancellationToken cancellation)
        {
            var results = new string[texts.Count];
            if (texts.Count > 1 && !_arraysUnsupported)
            {
                var q = new JArray();
                foreach (string text in texts)
                    q.Add(text);
                string response = null;
                try
                {
                    response = await PostAsync(q, sourceCode, targetCode, cancellation).ConfigureAwait(false);
                }
                catch (TranslationHttpException exception) when (exception.StatusCode == 400 || exception.StatusCode == 500 || exception.StatusCode == 422)
                {
                    // Old servers answer an array with 400/500 ("Invalid request: missing q parameter" or a crash).
                    _arraysUnsupported = true;
                }

                if (response != null)
                {
                    JToken translated = ParseTranslatedText(response);
                    var array = translated as JArray;
                    if (array != null && array.Count == texts.Count)
                    {
                        for (int i = 0; i < texts.Count; i++)
                            results[i] = array[i].Type == JTokenType.String ? (string)array[i] : null;
                        return results;
                    }
                    // A string (array joined) or a wrong count: this server does not really support arrays.
                    _arraysUnsupported = true;
                }
            }

            for (int i = 0; i < texts.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                string response = await PostAsync(new JValue(texts[i]), sourceCode, targetCode, cancellation).ConfigureAwait(false);
                JToken translated = ParseTranslatedText(response);
                if (translated is JArray single && single.Count == 1)
                    translated = single[0];
                results[i] = translated != null && translated.Type == JTokenType.String ? (string)translated : null;
            }
            return results;
        }

        private async Task<string> PostAsync(JToken q, string sourceCode, string targetCode, CancellationToken cancellation)
        {
            ThrowIfDisposed();
            var body = new JObject
            {
                ["q"] = q,
                ["source"] = sourceCode,
                ["target"] = targetCode,
                ["format"] = "text",
            };
            if (Options.ApiKey.Length > 0)
                body["api_key"] = Options.ApiKey;

            using (var request = new HttpRequestMessage(HttpMethod.Post, _endpoint))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", "AutoTranslate-CS2/1.0");
                request.Content = HttpTransport.Json(body.ToString(Formatting.None));
                try
                {
                    return await HttpTransport.SendAsync(request, Options.TimeoutSeconds, cancellation, EngineName, Options.ApiKey).ConfigureAwait(false);
                }
                catch (TranslationHttpException exception) when (exception.StatusCode == 403)
                {
                    throw new TranslationHttpException(403, exception.Message
                        + (Options.ApiKey.Length == 0 ? " — this server requires an API key." : " — the API key was rejected."));
                }
            }
        }

        private static JToken ParseTranslatedText(string response)
        {
            JObject json;
            try
            {
                json = JObject.Parse(response);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("LibreTranslate returned a response that is not JSON.", exception);
            }
            JToken error = json["error"];
            if (error != null && error.Type != JTokenType.Null)
                throw new InvalidDataException("LibreTranslate: " + HttpTransport.Sanitize(error.ToString(), null));
            JToken translated = json["translatedText"];
            if (translated == null)
                throw new InvalidDataException("LibreTranslate returned no translatedText.");
            return translated;
        }
    }
}
