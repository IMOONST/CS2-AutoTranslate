using System;
using AutoTranslate.Core;

namespace AutoTranslate.Providers
{
    /// <summary>Creates the translation engine chosen on the options page.</summary>
    public static class ProviderFactory
    {
        /// <summary>
        /// A new provider for <paramref name="options"/> (the options are copied; later changes need a new provider).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
        /// <exception cref="InvalidOperationException">A required setting is missing or invalid (key, server URL); the message says which, without the key.</exception>
        /// <exception cref="NotSupportedException">Unknown <see cref="ProviderOptions.Kind"/>.</exception>
        public static ITranslationProvider Create(ProviderOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            switch (options.Kind)
            {
                case ProviderKind.GoogleFree: return new GoogleFreeProvider(options);
                case ProviderKind.DeepL: return new DeepLProvider(options);
                case ProviderKind.LibreTranslate: return new LibreTranslateProvider(options);
                case ProviderKind.Microsoft: return new MicrosoftProvider(options);
                case ProviderKind.FreeAuto: return new FreeChainProvider(options);
                default: throw new NotSupportedException("Unknown translation engine " + options.Kind + ".");
            }
        }
    }
}
