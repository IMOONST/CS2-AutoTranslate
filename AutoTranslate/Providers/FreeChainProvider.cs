using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutoTranslate.Core;

namespace AutoTranslate.Providers
{
    /// <summary>
    /// "Free (automatic)": the free engines in order — Google's Chrome-extension endpoint, then its web endpoint (each
    /// has its own quota; Microsoft's free Edge endpoint closed its token service in 2026). When one refuses (rate limit,
    /// network error, unusable response) the request moves to the next, and the refusing engine rests for a growing
    /// time (1, 2, 4, 8, 15 minutes); a success clears its record. Only when every engine is resting does the call fail
    /// with a rate-limit error, so the service pauses instead of hammering the services.
    /// </summary>
    internal sealed class FreeChainProvider : ITranslationProvider
    {
        private sealed class Member
        {
            public ITranslationProvider Provider;
            public DateTime RestUntil = DateTime.MinValue;
            public int Refusals;
        }

        private readonly List<Member> _members = new List<Member>();
        private readonly object _lock = new object();

        public FreeChainProvider(ProviderOptions options)
        {
            _members.Add(new Member { Provider = new GoogleDictProvider(Copy(options, ProviderKind.GoogleFree)) });
            _members.Add(new Member { Provider = new GoogleFreeProvider(Copy(options, ProviderKind.GoogleFree)) });
        }

        public ProviderKind Kind => ProviderKind.FreeAuto;
        /// <summary>The first engine's limits; the second splits calls into its own smaller requests.</summary>
        public int MaxTextsPerRequest => 50;
        public int MaxCharsPerRequest => 5000;

        public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken cancellation)
        {
            Exception last = null;
            foreach (var member in _members)
            {
                lock (_lock)
                {
                    if (DateTime.UtcNow < member.RestUntil) continue;
                }
                try
                {
                    var results = await member.Provider.TranslateAsync(texts, sourceLanguage, targetLanguage, cancellation).ConfigureAwait(false);
                    lock (_lock) member.Refusals = 0;
                    return results;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (NotSupportedException e)
                {
                    // This engine lacks the language: try the next without resting it.
                    last = e;
                }
                catch (Exception e) when (e is TranslationHttpException || e is IOException || e is TimeoutException || HttpRequestFailure(e))
                {
                    last = e;
                    lock (_lock)
                    {
                        member.Refusals++;
                        var minutes = Math.Min(15, 1 << Math.Min(member.Refusals - 1, 4));
                        member.RestUntil = DateTime.UtcNow + TimeSpan.FromMinutes(minutes);
                    }
                    Mod.Log.Info($"{member.Provider.Kind} refused ({e.Message}); trying the next free engine. It rests {Math.Min(15, 1 << Math.Min(member.Refusals - 1, 4))} min.");
                }
            }

            if (last is NotSupportedException) throw last;
            // Every engine is resting: report a rate limit so the service pauses.
            throw new TranslationHttpException(429, "HTTP 429 Too Many Requests (free engines): all free engines are resting"
                + (last != null ? " — last: " + last.Message : "") + ".");
        }

        /// <summary>Other transport failures (DNS, TLS, unusable JSON) also move to the next engine.</summary>
        private static bool HttpRequestFailure(Exception e) =>
            e is System.Net.Http.HttpRequestException || e is InvalidDataException || e is Newtonsoft.Json.JsonException;

        private static ProviderOptions Copy(ProviderOptions options, ProviderKind kind) => new ProviderOptions
        {
            Kind = kind,
            ApiKey = "",
            ServiceUrl = "",
            Region = "",
            TimeoutSeconds = options.TimeoutSeconds,
        };

        public void Dispose()
        {
            foreach (var member in _members) member.Provider.Dispose();
        }
    }
}
