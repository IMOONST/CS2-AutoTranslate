using System;
using System.Globalization;
using AutoTranslate.Providers;
using Game.SceneFlow;

namespace AutoTranslate.Core
{
    /// <summary>Why nothing is translated.</summary>
    public enum OffReason
    {
        None,
        /// <summary>The player switched Auto Translate off.</summary>
        Disabled,
        /// <summary>Auto: the game and Windows are both in English.</summary>
        AllEnglish,
        /// <summary>Game language mode: the game is in English.</summary>
        GameEnglish,
        /// <summary>Windows language mode: Windows is in English (or its language is unknown).</summary>
        SystemEnglish,
        /// <summary>Manual mode without a language, or with English.</summary>
        NoLanguage,
    }

    /// <summary>What the translation service runs with; built from the settings and the game and Windows languages.</summary>
    internal sealed class ServiceConfig
    {
        public bool Enabled;
        /// <summary>Target language code ("ar", "de", "zh-Hans"); "" when off.</summary>
        public string Target = "";
        public OffReason Reason;
        public bool Interfaces;
        public ProviderOptions Provider = new ProviderOptions();

        public bool SameAs(ServiceConfig other)
        {
            if (other == null) return false;
            return Enabled == other.Enabled
                && Target == other.Target
                && Reason == other.Reason
                && Interfaces == other.Interfaces
                && Provider.Kind == other.Provider.Kind
                && Provider.ApiKey == other.Provider.ApiKey
                && Provider.ServiceUrl == other.Provider.ServiceUrl
                && Provider.Region == other.Provider.Region
                && Provider.TimeoutSeconds == other.Provider.TimeoutSeconds;
        }

        /// <summary>Only the interface switch differs (the service need not restart).</summary>
        public bool SameExceptInterfaces(ServiceConfig other)
        {
            if (other == null) return false;
            return Enabled == other.Enabled
                && Target == other.Target
                && Reason == other.Reason
                && Provider.Kind == other.Provider.Kind
                && Provider.ApiKey == other.Provider.ApiKey
                && Provider.ServiceUrl == other.Provider.ServiceUrl
                && Provider.Region == other.Provider.Region
                && Provider.TimeoutSeconds == other.Provider.TimeoutSeconds;
        }
    }

    /// <summary>
    /// Resolves the target language. Auto: the game's language when it is not English; otherwise the Windows language
    /// when it is not English; otherwise off (there is nothing to translate into).
    /// </summary>
    internal static class TargetResolver
    {
        private static string s_SystemLanguage;
        private static bool s_SystemResolved;

        /// <summary>The language of the game's active locale ("en", "de", "zh-Hans"); null before the locales load.</summary>
        public static string GameLanguage()
        {
            try
            {
                var locale = GameManager.instance?.localizationManager?.activeLocaleId;
                return string.IsNullOrEmpty(locale) ? null : LanguageCodes.FromLocaleId(locale);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The Windows display language (CultureInfo.CurrentUICulture, then Unity's Application.systemLanguage); null when
        /// unknown. Resolved once, on first use (call it first on the main thread: Unity's API is main-thread only).
        /// </summary>
        public static string SystemLanguage
        {
            get
            {
                if (!s_SystemResolved)
                {
                    s_SystemLanguage = DetectSystemLanguage();
                    s_SystemResolved = true;
                }
                return s_SystemLanguage;
            }
        }

        private static string DetectSystemLanguage()
        {
            string fromCulture = null;
            try
            {
                var culture = CultureInfo.CurrentUICulture;
                if (culture != null && !culture.Equals(CultureInfo.InvariantCulture) && !string.IsNullOrEmpty(culture.Name))
                    fromCulture = LanguageCodes.FromLocaleId(culture.Name);
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"Reading the Windows language failed: {e.Message}");
            }

            string fromUnity = null;
            try
            {
                fromUnity = LanguageCodes.FromSystemLanguage(UnityEngine.Application.systemLanguage);
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"Reading Unity's system language failed: {e.Message}");
            }

            // Mono can report the invariant or an English culture although Windows is set to another language;
            // Unity reads the Windows setting directly, so a non-English answer from it wins over an English culture.
            if (!string.IsNullOrEmpty(fromCulture) && !TextRules.IsEnglish(fromCulture)) return fromCulture;
            if (!string.IsNullOrEmpty(fromUnity)) return fromUnity;
            return fromCulture;
        }

        /// <summary>The service configuration for the current settings (null settings give "off").</summary>
        public static ServiceConfig Build(Setting setting)
        {
            var config = new ServiceConfig();
            if (setting == null)
            {
                config.Reason = OffReason.Disabled;
                return config;
            }

            config.Interfaces = setting.TranslateInterfaces;
            config.Provider = new ProviderOptions
            {
                Kind = setting.Provider,
                ApiKey = (setting.ApiKey ?? "").Trim(),
                ServiceUrl = (setting.ServiceUrl ?? "").Trim(),
                Region = (setting.Region ?? "").Trim(),
                TimeoutSeconds = 20,
            };

            if (!setting.Enabled)
            {
                config.Reason = OffReason.Disabled;
                return config;
            }

            string target = null;
            switch (setting.TargetMode)
            {
                case Setting.TargetLanguageMode.GameLanguage:
                    target = GameLanguage();
                    if (string.IsNullOrEmpty(target) || TextRules.IsEnglish(target)) config.Reason = OffReason.GameEnglish;
                    break;
                case Setting.TargetLanguageMode.SystemLanguage:
                    target = SystemLanguage;
                    if (string.IsNullOrEmpty(target) || TextRules.IsEnglish(target)) config.Reason = OffReason.SystemEnglish;
                    break;
                case Setting.TargetLanguageMode.Manual:
                    target = (setting.ManualLanguage ?? "").Trim();
                    if (string.IsNullOrEmpty(target) || TextRules.IsEnglish(target)) config.Reason = OffReason.NoLanguage;
                    break;
                default:
                    var game = GameLanguage();
                    if (!string.IsNullOrEmpty(game) && !TextRules.IsEnglish(game))
                    {
                        target = game;
                    }
                    else
                    {
                        var system = SystemLanguage;
                        if (!string.IsNullOrEmpty(system) && !TextRules.IsEnglish(system)) target = system;
                        else config.Reason = OffReason.AllEnglish;
                    }
                    break;
            }

            if (config.Reason != OffReason.None) return config;
            config.Enabled = true;
            config.Target = target;
            return config;
        }
    }
}
