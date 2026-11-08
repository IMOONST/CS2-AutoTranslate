using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

// Shared contracts of Auto Translate. Each part is written against these types:
//   Core/      (TranslationService, cache, overrides, localization scan/injection, settings) implements the service;
//   Providers/ (translation engines, text masking, language codes) implements ProviderFactory, TextMasking, LanguageCodes;
//   Interface/ + UI/ (runtime hook of mod interfaces) uses TranslationService.
// Change these only together with every part that uses them.
namespace AutoTranslate.Core
{
    /// <summary>Which translation engine translates texts that are not in the cache or an override file.</summary>
    public enum ProviderKind
    {
        /// <summary>Google Translate's free web endpoint (no key; rate limited).</summary>
        GoogleFree,
        /// <summary>DeepL API (key; free or pro, chosen by the key's ":fx" suffix).</summary>
        DeepL,
        /// <summary>LibreTranslate (self-hosted or public instance; URL and optional key).</summary>
        LibreTranslate,
        /// <summary>Microsoft Translator / Azure AI Translator (key and region).</summary>
        Microsoft,
        /// <summary>The free engines in turn (Google's Chrome-extension endpoint, then its web endpoint), switching when one refuses. The default.</summary>
        FreeAuto,
    }

    /// <summary>Settings a provider needs, copied from the options page when the service (re)starts.</summary>
    public sealed class ProviderOptions
    {
        public ProviderKind Kind;
        public string ApiKey = "";
        /// <summary>Base URL (LibreTranslate; optional override for the others).</summary>
        public string ServiceUrl = "";
        /// <summary>Azure resource region (Microsoft).</summary>
        public string Region = "";
        /// <summary>Request timeout in seconds.</summary>
        public int TimeoutSeconds = 20;
    }

    /// <summary>
    /// A translation engine. Implementations are thread-safe, never touch Unity or ECS objects, and are called from
    /// background tasks only. They receive MASKED texts (see <see cref="TextMasking"/>) and must return exactly one
    /// result per input, in order; a text that fails is returned as null (the service retries it later).
    /// </summary>
    public interface ITranslationProvider : IDisposable
    {
        ProviderKind Kind { get; }
        /// <summary>Most texts per <see cref="TranslateAsync"/> call.</summary>
        int MaxTextsPerRequest { get; }
        /// <summary>Most characters (sum of the texts) per call.</summary>
        int MaxCharsPerRequest { get; }

        /// <param name="sourceLanguage">ISO 639-1 code ("en") or "auto".</param>
        /// <param name="targetLanguage">ISO 639-1 code, or BCP-47 for Chinese ("zh-Hans", "zh-Hant") and Portuguese ("pt-BR").</param>
        Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken cancellation);
    }

    /// <summary>Where a text to translate came from (decides the source language and the cache bucket).</summary>
    public enum TextOrigin
    {
        /// <summary>A mod's localization entry (the game's localization system).</summary>
        Localization,
        /// <summary>Text shown in a mod's interface that does not go through the localization system.</summary>
        Interface,
    }

    /// <summary>A text with the parts that must not be translated replaced by tokens, and those parts in order.</summary>
    public sealed class MaskedText
    {
        public string Text;
        public List<string> Tokens = new List<string>();
        /// <summary>Nothing left to translate (only tokens, numbers or punctuation).</summary>
        public bool IsEmpty;
    }
}
