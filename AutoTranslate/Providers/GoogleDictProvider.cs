using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutoTranslate.Core;
using Newtonsoft.Json.Linq;

namespace AutoTranslate.Providers
{
    /// <summary>
    /// Google's translation endpoint used by the Chrome dictionary extension (clients5.google.com, client=dict-chrome-ex).
    /// It has its own request quota, separate from the web endpoint of <see cref="GoogleFreeProvider"/>, and takes
    /// several texts per request (repeated "q" form fields, POST). With a source language the response is a JSON array
    /// of strings; with "auto" each item is [translation, detected language].
    /// </summary>
    internal sealed class GoogleDictProvider : TranslationProviderBase
    {
        private const string Endpoint = "https://clients5.google.com/translate_a/t?client=dict-chrome-ex";

        public GoogleDictProvider(ProviderOptions options) : base(options)
        {
        }

        /// <summary>Reported as Google: it speaks Google's language codes.</summary>
        public override ProviderKind Kind => ProviderKind.GoogleFree;
        public override int MaxTextsPerRequest => 50;
        public override int MaxCharsPerRequest => 5000;
        protected override string EngineName => "Google (Chrome endpoint)";

        protected override async Task<string[]> TranslateBatchAsync(IReadOnlyList<string> texts, string sourceCode, string targetCode, CancellationToken cancellation)
        {
            ThrowIfDisposed();
            string url = Endpoint + "&sl=" + HttpTransport.EscapeData(sourceCode == LanguageCodes.Auto ? "auto" : sourceCode)
                + "&tl=" + HttpTransport.EscapeData(targetCode);
            var fields = new List<KeyValuePair<string, string>>(texts.Count);
            foreach (var text in texts) fields.Add(new KeyValuePair<string, string>("q", text));

            string response;
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
                request.Content = HttpTransport.Form(fields);
                response = await HttpTransport.SendAsync(request, Options.TimeoutSeconds, cancellation, EngineName).ConfigureAwait(false);
            }

            JArray items;
            try
            {
                items = JToken.Parse(response) as JArray;
            }
            catch (Exception exception)
            {
                throw new InvalidDataException(EngineName + " returned a response that is not JSON.", exception);
            }
            if (items == null || items.Count != texts.Count)
                throw new InvalidDataException(EngineName + " returned " + (items == null ? 0 : items.Count) + " results for " + texts.Count + " texts.");

            var results = new string[texts.Count];
            for (int i = 0; i < texts.Count; i++)
            {
                var item = items[i];
                if (item.Type == JTokenType.String) results[i] = (string)item;
                else if (item is JArray pair && pair.Count > 0 && pair[0].Type == JTokenType.String) results[i] = (string)pair[0];
            }
            return results;
        }
    }
}
