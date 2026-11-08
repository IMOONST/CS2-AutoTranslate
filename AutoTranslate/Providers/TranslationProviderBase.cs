using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutoTranslate.Core;

namespace AutoTranslate.Providers
{
    /// <summary>
    /// Shared part of the engines: argument checks, language-code resolution, blank texts, trimming (TextMasking
    /// restores the outer whitespace), splitting a call into requests within the engine's own limits, and result checks.
    /// </summary>
    internal abstract class TranslationProviderBase : ITranslationProvider
    {
        protected readonly ProviderOptions Options;
        private volatile bool _disposed;

        protected TranslationProviderBase(ProviderOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            // Copy: the options page may change its instance while requests run.
            Options = new ProviderOptions
            {
                Kind = options.Kind,
                ApiKey = (options.ApiKey ?? "").Trim(),
                ServiceUrl = (options.ServiceUrl ?? "").Trim(),
                Region = (options.Region ?? "").Trim(),
                TimeoutSeconds = options.TimeoutSeconds <= 0 ? 20 : options.TimeoutSeconds,
            };
        }

        public abstract ProviderKind Kind { get; }
        public abstract int MaxTextsPerRequest { get; }
        public abstract int MaxCharsPerRequest { get; }

        /// <summary>Name used in error messages.</summary>
        protected abstract string EngineName { get; }

        /// <summary>The cost of one text against <see cref="MaxCharsPerRequest"/> (after the engine's own encoding).</summary>
        protected virtual int CostOf(string text)
        {
            return text.Length;
        }

        /// <summary>
        /// Translates one request's worth of trimmed, non-blank texts (count and size within the limits).
        /// Returns one entry per text, in order; null entries are failures of single texts.
        /// </summary>
        /// <param name="sourceCode">Engine code of the source language, or "auto".</param>
        /// <param name="targetCode">Engine code of the target language.</param>
        protected abstract Task<string[]> TranslateBatchAsync(IReadOnlyList<string> texts, string sourceCode, string targetCode, CancellationToken cancellation);

        public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken cancellation)
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().Name);
            if (texts == null)
                throw new ArgumentNullException(nameof(texts));
            cancellation.ThrowIfCancellationRequested();

            var results = new string[texts.Count];
            if (texts.Count == 0)
                return results;

            string targetCode = LanguageCodes.ForProvider(Kind, targetLanguage);
            if (targetCode == null || targetCode == LanguageCodes.Auto)
                throw new NotSupportedException(EngineName + " does not offer the target language '" + targetLanguage + "'.");
            string sourceCode = string.IsNullOrEmpty(sourceLanguage) ? LanguageCodes.Auto : LanguageCodes.ForProvider(Kind, sourceLanguage);
            if (sourceCode == null)
                sourceCode = LanguageCodes.Auto; // a source the engine does not list: let it detect

            // Same language: nothing to translate.
            bool sameLanguage = sourceCode != LanguageCodes.Auto
                && string.Equals(LanguageCodes.Normalize(sourceLanguage), LanguageCodes.Normalize(targetLanguage), StringComparison.OrdinalIgnoreCase);

            var pending = new List<int>(texts.Count);
            var trimmed = new string[texts.Count];
            for (int i = 0; i < texts.Count; i++)
            {
                string text = texts[i];
                if (text == null)
                    continue;
                string core = TextMasking.TrimAll(text);
                if (core.Length == 0 || sameLanguage)
                {
                    results[i] = text;
                    continue;
                }
                trimmed[i] = core;
                pending.Add(i);
            }

            int maxTexts = Math.Max(1, MaxTextsPerRequest);
            int maxChars = Math.Max(1, MaxCharsPerRequest);
            int start = 0;
            while (start < pending.Count)
            {
                cancellation.ThrowIfCancellationRequested();
                var batch = new List<string>();
                var indexes = new List<int>();
                int chars = 0;
                while (start < pending.Count && batch.Count < maxTexts)
                {
                    int index = pending[start];
                    int cost = CostOf(trimmed[index]);
                    if (batch.Count > 0 && chars + cost > maxChars)
                        break;
                    batch.Add(trimmed[index]);
                    indexes.Add(index);
                    chars += cost;
                    start++;
                }

                string[] translated = await TranslateBatchAsync(batch, sourceCode, targetCode, cancellation).ConfigureAwait(false);
                if (translated == null || translated.Length != batch.Count)
                    throw new InvalidOperationException(EngineName + " returned " + (translated == null ? 0 : translated.Length) + " translations for " + batch.Count + " texts.");
                for (int i = 0; i < indexes.Count; i++)
                    results[indexes[i]] = translated[i];
            }
            return results;
        }

        public void Dispose()
        {
            // The HttpClient is shared by every provider and lives as long as the process.
            _disposed = true;
        }

        protected void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().Name);
        }

        /// <summary>Joins a base URL and a path ("https://x/" + "translate").</summary>
        protected static string Combine(string baseUrl, string path)
        {
            return baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
        }

        /// <summary>Validates a user-entered absolute http(s) URL.</summary>
        protected static string RequireHttpUrl(string url, string engineName)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException(engineName + ": the server URL '" + url + "' is not a valid http(s) address.");
            return url;
        }
    }
}
