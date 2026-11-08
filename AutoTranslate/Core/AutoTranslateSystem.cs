using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using AutoTranslate.Providers;
using Colossal;
using Colossal.Localization;
using Game;
using Game.SceneFlow;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoTranslate.Core
{
    /// <summary>
    /// Owns the translation service and translates the localization of other mods.
    /// <para>
    /// Scan: every source mods added with LocalizationManager.AddSource (m_UserSources) is read; a mod key's source text
    /// is its en-US value (else the fallback locale's, else any). A key needs translation when the mod has no entry for
    /// the target language with a value different from its English one. Translations go into the ACTIVE game locale
    /// (the target language need not be a game locale: Arabic is not one), overriding the mod's English there.
    /// </para>
    /// <para>
    /// Injection: one <see cref="TranslatedSource"/> added to the active locale (kept as its last source, so locale
    /// reloads bring the translations back). New translations are written straight into the active dictionary and the
    /// game's change notification is raised once, at most every 2 seconds: that costs only the changed entries, where
    /// adding or removing a source reloads all or part of the locale. Notifications caused by Auto Translate itself
    /// are ignored by its own handler; external changes (another mod reloading, a language switch) are re-checked.
    /// </para>
    /// <para>
    /// Exclusions (see <see cref="Exclusions"/>): each source is attributed to a mod (<see cref="ModCatalog"/>); keys of
    /// excluded mods are never translated and their injected texts are removed at the next refresh; excluded texts stay
    /// as written; protected terms are masked by the service. All of it is decided before any cache lookup or queueing.
    /// </para>
    /// Per-frame cost when idle: a few comparisons, no allocations.
    /// </summary>
    public partial class AutoTranslateSystem : GameSystemBase
    {
        private const float kRefreshInterval = 2f;
        private const float kMenuCheckInterval = 1f;
        private const float kGameCheckInterval = 5f;
        private const float kApplyDelay = 0.75f;
        private const float kMinRescanInterval = 2f;
        private const int kMaxNotificationsPerWindow = 12;
        private const float kNotificationWindow = 10f;

        // Requests from the options page (main thread).
        private static bool s_ApplyRequested;
        private static bool s_RetranslateRequested;
        private static bool s_ClearCacheRequested;
        private static bool s_ExportRequested;
        private static bool s_ExportModsRequested;
        private static bool s_ExclusionsReloadRequested;

        // Status for the options page.
        private static int s_StatusVersion;
        private static int s_ModsWithTexts;
        private static int s_ExcludedMods;
        private static List<ModEntry> s_ModList = new List<ModEntry>();
        private static int s_FoundCount;
        private static int s_TranslatedCount;
        private static int s_WaitingCount;
        private static int s_FailedCount;
        private static string s_Message = "";
        private static OffReason s_OffReason = OffReason.Disabled;

        private LocalizationManager m_Loc;
        private TranslationService m_Service;
        private Action<IReadOnlyList<string>> m_OnTranslated;
        private Action m_OnReset;
        private ServiceConfig m_Config;

        // Settings last seen (compared without allocating).
        private bool m_SeenEnabled;
        private Setting.TargetLanguageMode m_SeenMode;
        private string m_SeenManual;
        private bool m_SeenInterfaces;
        private ProviderKind m_SeenProvider;
        private string m_SeenKey;
        private string m_SeenUrl;
        private string m_SeenRegion;
        private Setting m_SeenSetting;

        private float m_CreatedAt;
        private float m_ApplyAt = -1f;
        private float m_NextSlowCheck;
        private int m_UserSourceCount = -1;
        private string m_LastLocale;
        private bool m_DictionaryChanged;
        private bool m_SelfNotify;
        private bool m_RescanRequested;
        private float m_RescanAt;
        private float m_LastRescan = -100f;
        private bool m_RefreshPending;
        private float m_NextRefreshAt;
        private float m_LastRefresh = -100f;
        private bool m_FullVerify;
        private int m_LastFailedCount;
        private float m_NotificationWindowStart;
        private int m_NotificationsInWindow;
        private bool m_NotificationLimitLogged;

        // Exclusions: the options fields and exclusions.json as last applied, and a field edit waiting to settle.
        private const float kExclusionsCheckInterval = 1f;
        private const float kFileCheckInterval = 2f;
        private const float kFieldSettleDelay = 3f;
        private string m_AppliedTermsField;
        private string m_AppliedModsField;
        private DateTime m_ExclusionsStamp = DateTime.MinValue;
        private float m_NextExclusionsCheck;
        private float m_NextFileCheck;
        private float m_FieldsChangedAt = -1f;
        private string m_PendingTermsField;
        private string m_PendingModsField;

        // Scan state.
        private readonly Dictionary<string, KeyInfo> m_Keys = new Dictionary<string, KeyInfo>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> m_Desired = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> m_KeysByText = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> m_Written = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> m_DirtyKeys = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Keys of mods shown in another language, waiting for a translation: English text (trimmed) to keys.</summary>
        private readonly Dictionary<string, List<string>> m_LangKeysByText = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        /// <summary>Texts to queue per other language: language to (English-sourced, auto-detected).</summary>
        private readonly Dictionary<string, (List<string> english, List<string> auto)> m_LangRequests = new Dictionary<string, (List<string>, List<string>)>(StringComparer.OrdinalIgnoreCase);
        private TranslatedSource m_Source;
        private string m_SourceLocale;
        private string m_InjectedTarget = "";
        private string m_GameTextsSignature;

        private sealed class KeyInfo
        {
            public string English;
            public int EnglishRank = int.MaxValue;
            public string TargetValue;
            /// <summary>The mod of the source the English text came from.</summary>
            public ModEntry Mod;
            /// <summary>A source of an excluded mod has this key: it is never translated.</summary>
            public bool Excluded;
        }

        /// <summary>One localization source, read once per scan.</summary>
        private sealed class SourceRead
        {
            public List<KeyValuePair<string, string>> Entries;
            public ModEntry Mod;
        }

        private sealed class ReferenceComparer : IEqualityComparer<IDictionarySource>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(IDictionarySource x, IDictionarySource y) => ReferenceEquals(x, y);
            public int GetHashCode(IDictionarySource obj) => RuntimeHelpers.GetHashCode(obj);
        }

        // ------------------------------------------------------------------ status and requests (options page)

        /// <summary>Changes whenever a status value changes.</summary>
        public static int StatusVersion
        {
            get
            {
                var service = TranslationService.Instance;
                return unchecked(s_StatusVersion * 397 + (service != null ? service.Version : 0));
            }
        }

        /// <summary>Mod texts found in the localization of other mods.</summary>
        public static int FoundCount => s_FoundCount;

        /// <summary>Mod texts with a translation (override, the mod's own, or machine).</summary>
        public static int TranslatedCount => s_TranslatedCount;

        /// <summary>Mod texts waiting for the engine.</summary>
        public static int WaitingCount => s_WaitingCount;

        /// <summary>Mod texts the engine failed to translate this session.</summary>
        public static int FailedCount => s_FailedCount;

        /// <summary>Mods (or unattributed source groups) with localization texts, found in the last scan.</summary>
        public static int ModsWithTexts => s_ModsWithTexts;

        /// <summary>Of <see cref="ModsWithTexts"/>, those in the exclusion list.</summary>
        public static int ExcludedMods => s_ExcludedMods;

        /// <summary>Last maintenance result (export path, cache cleared, …); "" when none.</summary>
        public static string Message => s_Message;

        /// <summary>Why nothing is translated (None when translating).</summary>
        public static OffReason CurrentOffReason => s_OffReason;

        /// <summary>The settings changed: re-resolve the target and restart the engine if needed (debounced).</summary>
        public static void RequestApply() => s_ApplyRequested = true;

        /// <summary>Re-read the override files, retry failed texts and rescan every mod.</summary>
        public static void RequestRetranslate() => s_RetranslateRequested = true;

        /// <summary>Delete every cached translation and translate again.</summary>
        public static void RequestClearCache() => s_ClearCacheRequested = true;

        /// <summary>Write the texts without a human translation to ExportDir.</summary>
        public static void RequestExport() => s_ExportRequested = true;

        /// <summary>Write the mods found in the last scan (names to use in exclusions) to <see cref="Paths.ModListFile"/>.</summary>
        public static void RequestExportMods() => s_ExportModsRequested = true;

        /// <summary>Re-read exclusions.json and the exclusion fields now.</summary>
        public static void RequestReloadExclusions() => s_ExclusionsReloadRequested = true;

        private static void SetMessage(string message)
        {
            s_Message = message ?? "";
            s_StatusVersion++;
        }

        // ------------------------------------------------------------------ lifecycle

        protected override void OnCreate()
        {
            base.OnCreate();
            s_Instance = this;
            Paths.EnsureResolved();
            var _ = TargetResolver.SystemLanguage; // Unity's language API is main-thread only: resolve it here.

            m_CreatedAt = UnityEngine.Time.realtimeSinceStartup;
            m_OnTranslated = OnTranslated;
            m_OnReset = OnReset;
            m_Loc = GameManager.instance?.localizationManager;
            if (m_Loc != null) m_Loc.onActiveDictionaryChanged += OnActiveDictionaryChanged;
            AttachService();
            if (!LocalizationAccess.CanScan) SetMessage("m_UserSources unavailable");

            // Exclusions before anything is looked up or loaded (the protected terms decide which cached entries are valid).
            ApplyExclusions(force: true);

            // The translation memory of the likely target starts loading now, in the background, so it is complete (or
            // nearly) when the service starts after the game's boot. A wrong guess only costs memory.
            try
            {
                var preview = TargetResolver.Build(Mod.Settings);
                if (preview.Enabled && m_Service != null) m_Service.Preload(preview.Target);
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"Resolving the target language early failed: {e.Message}");
            }
        }

        /// <summary>
        /// Which mods the texts of a panel belong to (the floating button's click mode). A text counts for a mod when it
        /// is the English text of one of its keys, or what the game shows for that key (its own target-language text or
        /// our translation). <paramref name="english"/> receives, for each recognised text, its English text. Main thread.
        /// </summary>
        internal static Dictionary<ModEntry, int> FindOwners(ICollection<string> texts, Dictionary<string, string> english)
        {
            var votes = new Dictionary<ModEntry, int>();
            var system = s_Instance;
            if (system == null || texts == null || texts.Count == 0) return votes;
            var owner = new Dictionary<string, ModEntry>(StringComparer.Ordinal);
            // Common words ("Default", "Reset", "OK") appear in many mods: a text found in two mods tells nothing.
            var shared = new HashSet<string>(StringComparer.Ordinal);

            void Check(string value, KeyInfo info)
            {
                if (string.IsNullOrEmpty(value)) return;
                var key = TextRules.HasOuterWhitespace(value) ? value.Trim() : value;
                if (!texts.Contains(key)) return;
                if (owner.TryGetValue(key, out var known))
                {
                    if (!ReferenceEquals(known, info.Mod)) shared.Add(key);
                    return;
                }
                owner[key] = info.Mod;
                if (english != null && !string.IsNullOrEmpty(info.English)) english[key] = info.English.Trim();
            }

            foreach (var pair in system.m_Keys)
            {
                var info = pair.Value;
                if (info.Mod == null || !info.Mod.IsMod) continue;
                Check(info.English, info);
                Check(info.TargetValue, info);
                if (system.m_Desired.TryGetValue(pair.Key, out var desired)) Check(desired, info);
                if (system.m_Written.TryGetValue(pair.Key, out var written)) Check(written, info);
            }

            foreach (var pair in owner)
            {
                if (shared.Contains(pair.Key)) continue;
                votes[pair.Value] = votes.TryGetValue(pair.Value, out var count) ? count + 1 : 1;
            }
            return votes;
        }

        private static AutoTranslateSystem s_Instance;

        protected override void OnDestroy()
        {
            if (ReferenceEquals(s_Instance, this)) s_Instance = null;
            if (m_Loc != null) m_Loc.onActiveDictionaryChanged -= OnActiveDictionaryChanged;
            if (m_Service != null)
            {
                m_Service.Translated -= m_OnTranslated;
                m_Service.Reset -= m_OnReset;
            }
            m_Service = null;
            base.OnDestroy();
        }

        private void AttachService()
        {
            var service = TranslationService.Instance;
            if (service == m_Service) return;
            if (m_Service != null)
            {
                m_Service.Translated -= m_OnTranslated;
                m_Service.Reset -= m_OnReset;
            }
            m_Service = service;
            if (m_Service != null)
            {
                m_Service.Translated += m_OnTranslated;
                m_Service.Reset += m_OnReset;
            }
        }

        protected override void OnUpdate()
        {
            if (m_Service == null)
            {
                AttachService();
                if (m_Service == null) return;
            }
            if (m_Loc == null)
            {
                m_Loc = GameManager.instance?.localizationManager;
                if (m_Loc == null) return;
                m_Loc.onActiveDictionaryChanged += OnActiveDictionaryChanged;
            }

            float now = UnityEngine.Time.realtimeSinceStartup;

            // Translations finished in the background (raises Translated / Reset, handled below).
            m_Service.Pump();

            // The first start waits until the game has booted (its interface language is applied then), so Auto does
            // not briefly translate into the wrong language; at most 15 s.
            if (m_Config == null && Mod.Settings != null
                && (GameManager.instance == null || !GameManager.instance.isGameLoading || now > 15f + m_CreatedAt))
            {
                RememberSettings(Mod.Settings);
                m_LastLocale = SafeActiveLocale();
                ApplyConfig();
            }

            if (s_ApplyRequested)
            {
                s_ApplyRequested = false;
                // The master switch applies at once (original texts back, network work stopped); other settings are
                // debounced, since text fields apply on every keystroke.
                bool switched = Mod.Settings != null && m_Config != null && Mod.Settings.Enabled != m_SeenEnabled;
                m_ApplyAt = switched ? now : now + kApplyDelay;
            }
            if (m_ApplyAt >= 0f && now >= m_ApplyAt && m_Config != null)
            {
                m_ApplyAt = -1f;
                if (Mod.Settings != null) RememberSettings(Mod.Settings);
                ApplyConfig();
            }
            if (s_RetranslateRequested)
            {
                s_RetranslateRequested = false;
                Retranslate();
            }
            if (s_ClearCacheRequested)
            {
                s_ClearCacheRequested = false;
                m_Service.ClearCache();
                SetMessage(Setting.Text("Cache cleared.", "تم مسح الذاكرة المؤقتة."));
            }
            if (s_ExportRequested)
            {
                s_ExportRequested = false;
                Export();
            }
            if (s_ExportModsRequested)
            {
                s_ExportModsRequested = false;
                ExportMods();
            }
            if (s_ExclusionsReloadRequested)
            {
                s_ExclusionsReloadRequested = false;
                ApplyExclusions(force: true);
            }
            if (now >= m_NextExclusionsCheck)
            {
                m_NextExclusionsCheck = now + kExclusionsCheckInterval;
                CheckExclusions(now);
            }

            if (now >= m_NextSlowCheck)
            {
                bool inGame = GameManager.instance != null && !GameManager.instance.isGameLoading
                    && (GameManager.instance.gameMode & GameMode.GameOrEditor) != 0;
                m_NextSlowCheck = now + (inGame ? kGameCheckInterval : kMenuCheckInterval);
                SlowCheck(now);
            }

            if (m_DictionaryChanged)
            {
                m_DictionaryChanged = false;
                HandleDictionaryChange(now);
            }

            if (m_RescanRequested && now >= m_RescanAt)
            {
                if (now - m_LastRescan < kMinRescanInterval) m_RescanAt = m_LastRescan + kMinRescanInterval;
                else Rescan(now);
            }

            if (m_RefreshPending && now >= m_NextRefreshAt) RefreshInjection(now);
        }

        // ------------------------------------------------------------------ settings

        private void RememberSettings(Setting s)
        {
            m_SeenSetting = s;
            m_SeenEnabled = s.Enabled;
            m_SeenMode = s.TargetMode;
            m_SeenManual = s.ManualLanguage;
            m_SeenInterfaces = s.TranslateInterfaces;
            m_SeenProvider = s.Provider;
            m_SeenKey = s.ApiKey;
            m_SeenUrl = s.ServiceUrl;
            m_SeenRegion = s.Region;
        }

        private bool SettingsChanged(Setting s)
        {
            return s != m_SeenSetting
                || s.Enabled != m_SeenEnabled
                || s.TargetMode != m_SeenMode
                || !string.Equals(s.ManualLanguage, m_SeenManual, StringComparison.Ordinal)
                || s.TranslateInterfaces != m_SeenInterfaces
                || s.Provider != m_SeenProvider
                || !string.Equals(s.ApiKey, m_SeenKey, StringComparison.Ordinal)
                || !string.Equals(s.ServiceUrl, m_SeenUrl, StringComparison.Ordinal)
                || !string.Equals(s.Region, m_SeenRegion, StringComparison.Ordinal);
        }

        /// <summary>Resolves the configuration and restarts the service when it changed.</summary>
        private void ApplyConfig()
        {
            var config = TargetResolver.Build(Mod.Settings);
            if (config.SameAs(m_Config)) return;
            var previous = m_Config;
            m_Config = config;
            s_OffReason = config.Reason;
            s_StatusVersion++;
            if (previous != null && config.SameExceptInterfaces(previous) && m_Service.Enabled)
            {
                m_Service.SetInterfaces(config.Interfaces);
                return;
            }
            // Raises Reset, which schedules the rescan.
            m_Service.Configure(config);
            OptionsLocalization.ReloadIfStale();
        }

        // ------------------------------------------------------------------ exclusions

        /// <summary>
        /// Watches the exclusion fields of the options page (applied once they stayed unchanged for 3 s, so typing a
        /// term does not protect, and invalidate cached texts for, every intermediate prefix) and exclusions.json (its
        /// write time, every 2 s). Cheap: string comparisons and one file time query.
        /// </summary>
        private void CheckExclusions(float now)
        {
            var setting = Mod.Settings;
            var terms = setting?.ExcludedTerms ?? "";
            var mods = setting?.ExcludedMods ?? "";
            bool fieldsDiffer = !string.Equals(terms, m_AppliedTermsField, StringComparison.Ordinal)
                || !string.Equals(mods, m_AppliedModsField, StringComparison.Ordinal);
            if (fieldsDiffer)
            {
                if (m_FieldsChangedAt < 0f
                    || !string.Equals(terms, m_PendingTermsField, StringComparison.Ordinal)
                    || !string.Equals(mods, m_PendingModsField, StringComparison.Ordinal))
                {
                    m_FieldsChangedAt = now;
                    m_PendingTermsField = terms;
                    m_PendingModsField = mods;
                }
                else if (now - m_FieldsChangedAt >= kFieldSettleDelay)
                {
                    ApplyExclusions(force: true);
                    return;
                }
            }
            else
            {
                m_FieldsChangedAt = -1f;
            }

            if (now >= m_NextFileCheck)
            {
                m_NextFileCheck = now + kFileCheckInterval;
                if (Exclusions.FileStamp() != m_ExclusionsStamp) ApplyExclusions(force: true);
            }
        }

        /// <summary>Reads exclusions.json and the options fields and hands the result to the service (which raises Reset when they changed).</summary>
        private void ApplyExclusions(bool force)
        {
            var setting = Mod.Settings;
            var terms = setting?.ExcludedTerms ?? "";
            var mods = setting?.ExcludedMods ?? "";
            if (!force && string.Equals(terms, m_AppliedTermsField, StringComparison.Ordinal)
                && string.Equals(mods, m_AppliedModsField, StringComparison.Ordinal)
                && Exclusions.FileStamp() == m_ExclusionsStamp) return;

            var exclusions = Exclusions.Load(terms, mods);
            m_AppliedTermsField = terms;
            m_AppliedModsField = mods;
            m_FieldsChangedAt = -1f;
            m_ExclusionsStamp = Exclusions.FileStamp();
            if (m_Service == null) return;
            var before = m_Service.CurrentExclusions;
            m_Service.SetExclusions(exclusions);
            if (!string.Equals(before.Signature, exclusions.Signature, StringComparison.Ordinal))
            {
                Mod.Log.Info($"Exclusions: {exclusions.Terms.Count} protected terms, {exclusions.Texts.Count} texts, {exclusions.Mods.Count} mods.");
                s_StatusVersion++;
            }
            if (exclusions.FileError != null)
                SetMessage(Setting.Text(
                    $"{Exclusions.FilePath} could not be read: {exclusions.FileError}",
                    $"تعذّرت قراءة {Exclusions.FilePath}: {exclusions.FileError}"));
        }

        private void Retranslate()
        {
            ApplyExclusions(force: true);
            if (m_Service.Enabled)
            {
                // Raises Reset → rescan with the new overrides.
                m_Service.Retranslate();
                SetMessage(Setting.Text("Overrides reloaded; failed texts are retried.", "أُعيد تحميل ملفات الترجمة اليدوية وستُعاد محاولة النصوص التي فشلت."));
            }
            else
            {
                ApplyConfig();
                RequestRescan(0f);
            }
            m_LastRescan = -100f;
        }

        // ------------------------------------------------------------------ events

        private void OnActiveDictionaryChanged()
        {
            if (m_SelfNotify) return;
            m_DictionaryChanged = true;
        }

        private void OnReset()
        {
            // Target, engine, overrides or cache changed: every lookup may differ now.
            RequestRescan(0f);
            m_LastRescan = -100f;
            s_StatusVersion++;
        }

        private void OnTranslated(IReadOnlyList<string> texts)
        {
            bool any = false;
            for (int i = 0; i < texts.Count; i++)
            {
                if (!m_KeysByText.TryGetValue(texts[i], out var keys)) continue;
                m_KeysByText.Remove(texts[i]);
                foreach (var key in keys)
                {
                    if (!m_Keys.TryGetValue(key, out var info)) continue;
                    if (!m_Service.TryGet(info.English, TextOrigin.Localization, out var translated)) continue;
                    m_Desired[key] = translated;
                    m_DirtyKeys.Add(key);
                    s_TranslatedCount++;
                    s_WaitingCount = Math.Max(0, s_WaitingCount - 1);
                    any = true;
                }
            }
            // Mods shown in another language.
            for (int i = 0; i < texts.Count && m_LangKeysByText.Count > 0; i++)
            {
                if (!m_LangKeysByText.TryGetValue(texts[i], out var keys)) continue;
                for (int k = keys.Count - 1; k >= 0; k--)
                {
                    var key = keys[k];
                    if (!m_Keys.TryGetValue(key, out var info) || info.Mod?.Language == null)
                    {
                        keys.RemoveAt(k);
                        continue;
                    }
                    // The text may have been translated into another language than this key's: it keeps waiting then.
                    if (!m_Service.TryGet(info.English, TextOrigin.Localization, info.Mod.Language, out var translated)) continue;
                    keys.RemoveAt(k);
                    m_Desired[key] = translated;
                    m_Service.AddForeignText(translated);
                    m_DirtyKeys.Add(key);
                    s_TranslatedCount++;
                    any = true;
                }
                if (keys.Count == 0) m_LangKeysByText.Remove(texts[i]);
            }
            if (!any) return;
            s_StatusVersion++;
            ScheduleRefresh(UnityEngine.Time.realtimeSinceStartup);
        }

        private void HandleDictionaryChange(float now)
        {
            // Another mod's options text may have been reloaded in the wrong language.
            OptionsLocalization.ReloadIfStale();

            var active = SafeActiveLocale();
            if (m_Config == null)
            {
                // Not started yet: the first start reads the language anyway.
                m_LastLocale = active;
                return;
            }
            if (active != m_LastLocale)
            {
                m_LastLocale = active;
                // Auto follows the game's language; the injection moves to the new locale either way.
                var before = m_Config;
                ApplyConfig();
                if (before == m_Config) RequestRescan(0f);
                m_LastRescan = -100f;
                return;
            }

            var sources = LocalizationAccess.UserSources(m_Loc);
            if (sources != null && sources.Count != m_UserSourceCount)
            {
                RequestRescan(0.5f);
                return;
            }

            // Something reloaded the locale (or wrote into it): put back what differs, right away.
            if (m_Desired.Count > 0)
            {
                m_FullVerify = true;
                m_RefreshPending = true;
                m_NextRefreshAt = now;
            }
        }

        private void SlowCheck(float now)
        {
            var setting = Mod.Settings;
            if (setting != null && SettingsChanged(setting))
            {
                bool switched = setting.Enabled != m_SeenEnabled;
                RememberSettings(setting);
                if (m_ApplyAt < 0f || switched) m_ApplyAt = switched ? now : now + kApplyDelay;
            }

            var sources = LocalizationAccess.UserSources(m_Loc);
            if (sources != null && m_UserSourceCount >= 0 && sources.Count != m_UserSourceCount && !m_RescanRequested)
                RequestRescan(0.5f);

            int failed = m_Service.FailedCount;
            if (failed != m_LastFailedCount)
            {
                m_LastFailedCount = failed;
                RecountWaiting();
            }
        }

        private void RequestRescan(float delay)
        {
            float at = UnityEngine.Time.realtimeSinceStartup + delay;
            if (!m_RescanRequested || at < m_RescanAt) m_RescanAt = at;
            m_RescanRequested = true;
        }

        private void ScheduleRefresh(float now)
        {
            if (m_RefreshPending) return;
            m_RefreshPending = true;
            m_NextRefreshAt = Math.Max(now, m_LastRefresh + kRefreshInterval);
        }

        // ------------------------------------------------------------------ scan

        private void Rescan(float now)
        {
            m_RescanRequested = false;
            m_LastRescan = now;
            var target = m_Service.TargetLanguage;

            if (!m_Service.Enabled || m_Loc == null)
            {
                ClearInjection();
                m_Keys.Clear();
                m_KeysByText.Clear();
                m_Desired.Clear();
                m_InjectedTarget = "";
                var allSources = LocalizationAccess.UserSources(m_Loc);
                m_UserSourceCount = allSources?.Count ?? -1;
                s_FoundCount = s_TranslatedCount = s_WaitingCount = s_FailedCount = 0;
                s_StatusVersion++;
                return;
            }

            if (!string.Equals(target, m_InjectedTarget, StringComparison.Ordinal))
            {
                // A different language: take the old translations out completely before injecting new ones.
                ClearInjection();
                m_InjectedTarget = target;
            }

            ScanSources(target);
            BuildDesired(target, out var requestEnglish, out var requestAuto);
            RebuildGameTexts();

            // While the translation memory loads, nothing is queued (any of these texts may be in it): the end of the
            // load raises Reset, and this scan runs again with every saved translation available.
            bool memoryReady = m_Service.CacheReady;
            if (memoryReady)
            {
                if (requestEnglish.Count > 0) m_Service.Request(requestEnglish, TextOrigin.Localization, "en");
                if (requestAuto.Count > 0) m_Service.Request(requestAuto, TextOrigin.Localization, "auto");
                foreach (var pair in m_LangRequests)
                {
                    if (pair.Value.english.Count > 0) m_Service.Request(pair.Value.english, TextOrigin.Localization, "en", pair.Key);
                    if (pair.Value.auto.Count > 0) m_Service.Request(pair.Value.auto, TextOrigin.Localization, "auto", pair.Key);
                }
            }

            m_FullVerify = true;
            m_RefreshPending = true;
            m_NextRefreshAt = now;
            m_LastFailedCount = m_Service.FailedCount;
            RecountWaiting();
            Mod.Log.Info($"Scanned mod localization: {s_FoundCount} texts, {s_TranslatedCount} translated, {s_WaitingCount} waiting for {target}"
                + (memoryReady ? "." : " (translation memory still loading; nothing sent yet)."));
        }

        /// <summary>Reads every mod source into <see cref="m_Keys"/>.</summary>
        private void ScanSources(string target)
        {
            m_Keys.Clear();
            var live = LocalizationAccess.UserSources(m_Loc);
            if (live == null)
            {
                m_UserSourceCount = -1;
                return;
            }
            // A copy: reading a source may make its mod add more sources.
            var sources = live.ToArray();
            m_UserSourceCount = sources.Length;

            string fallback = null;
            try { fallback = m_Loc.fallbackLocaleId; }
            catch (Exception) { /* none */ }

            var read = new Dictionary<IDictionarySource, SourceRead>(ReferenceComparer.Instance);
            var order = new List<(string localeId, SourceRead source)>(sources.Length);
            var isTarget = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<IDictionaryEntryError>();
            var ownAssembly = typeof(AutoTranslateSystem).Assembly;
            var catalog = ModCatalog.Build();
            var exclusions = m_Service.CurrentExclusions;

            // 1. Read every source once and attribute it to a mod.
            foreach (var (localeId, source) in sources)
            {
                if (source == null || localeId == null) continue;
                // Auto Translate's own sources: its translations and its options page.
                if (source is TranslatedSource || source.GetType().Assembly == ownAssembly) continue;

                if (!read.TryGetValue(source, out var sourceRead))
                {
                    var entries = new List<KeyValuePair<string, string>>();
                    try
                    {
                        errors.Clear();
                        var result = source.ReadEntries(errors, new Dictionary<string, int>(StringComparer.Ordinal));
                        if (result != null)
                            foreach (var pair in result)
                                if (!string.IsNullOrEmpty(pair.Key) && pair.Value != null) entries.Add(pair);
                    }
                    catch (Exception e)
                    {
                        Mod.Log.Warn($"Reading the localization source '{source}' ({localeId}) failed: {e.Message}");
                    }
                    ModEntry mod;
                    try { mod = catalog.Identify(source, entries); }
                    catch (Exception e)
                    {
                        Mod.Log.Warn($"Finding the mod of the localization source '{source}' failed: {e.Message}");
                        mod = catalog.Identify(source, null);
                    }
                    sourceRead = new SourceRead { Entries = entries, Mod = mod };
                    read[source] = sourceRead;
                }
                order.Add((localeId, sourceRead));
            }

            // 2. Each mod's options page title (preferably its en-US one), then whether the mod is excluded.
            var titleRanks = new Dictionary<ModEntry, int>();
            foreach (var (localeId, sourceRead) in order)
            {
                var mod = sourceRead.Mod;
                if (!mod.IsMod) continue;
                int titleRank = localeId == "en-US" ? 0 : TextRules.IsEnglish(localeId) ? 1 : 2;
                if (titleRanks.TryGetValue(mod, out var best) && best <= titleRank) continue;
                foreach (var pair in sourceRead.Entries)
                {
                    if (pair.Value.Length == 0 || !ModCatalog.IsOptionsTitleKey(pair.Key, mod.AssemblyName)) continue;
                    mod.Title = pair.Value.Trim();
                    titleRanks[mod] = titleRank;
                    break;
                }
            }
            foreach (var mod in catalog.Used)
            {
                mod.Excluded = exclusions.IsExcludedMod(mod.AssemblyName, mod.DisplayName, mod.Title);
                var language = mod.Excluded ? null : exclusions.LanguageForMod(mod.AssemblyName, mod.DisplayName, mod.Title);
                mod.Language = string.IsNullOrEmpty(language) || TextRules.SameLanguage(language, target) ? null : language;
            }

            // 3. The keys. Values of excluded mods are collected for the interface hook (see IsExcludedInterfaceText).
            var excludedValues = new HashSet<string>(StringComparer.Ordinal);
            var languageValues = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (localeId, sourceRead) in order)
            {
                var entries = sourceRead.Entries;
                var mod = sourceRead.Mod;

                int rank;
                if (localeId == "en-US") rank = 0;
                else if (localeId == fallback) rank = 1;
                else if (TextRules.IsEnglish(localeId)) rank = 2;
                else rank = 3;

                if (!isTarget.TryGetValue(localeId, out var forTarget))
                {
                    string language = null;
                    try { language = LanguageCodes.FromLocaleId(localeId); }
                    catch (Exception) { /* unknown id */ }
                    forTarget = TextRules.SameLanguage(language ?? localeId, target);
                    isTarget[localeId] = forTarget;
                }

                foreach (var pair in entries)
                {
                    if (!m_Keys.TryGetValue(pair.Key, out var info))
                    {
                        info = new KeyInfo();
                        m_Keys[pair.Key] = info;
                    }
                    if (mod.Language != null && pair.Value.Length > 0 && !forTarget)
                        languageValues[TextRules.HasOuterWhitespace(pair.Value) ? pair.Value.Trim() : pair.Value] = mod.Language;
                    if (mod.Excluded)
                    {
                        info.Excluded = true;
                        if (pair.Value.Length > 0) excludedValues.Add(TextRules.HasOuterWhitespace(pair.Value) ? pair.Value.Trim() : pair.Value);
                    }
                    if (forTarget)
                    {
                        if (pair.Value.Length > 0) info.TargetValue = pair.Value;
                        // A target-language entry is a source text only when nothing better exists.
                        if (info.English == null) { info.English = pair.Value; info.EnglishRank = 4; info.Mod = mod; }
                        continue;
                    }
                    if (rank <= info.EnglishRank)
                    {
                        info.English = pair.Value;
                        info.EnglishRank = rank;
                        info.Mod = mod;
                    }
                }
            }

            foreach (var info in m_Keys.Values)
            {
                // A "translation" that equals the English text was copied, not translated.
                if (info.TargetValue != null && info.EnglishRank < 4 && string.Equals(info.TargetValue, info.English, StringComparison.Ordinal))
                    info.TargetValue = null;
                if (info.Mod != null && !string.IsNullOrWhiteSpace(info.English) && TextRules.HasLetters(info.English)) info.Mod.TextCount++;
            }

            // 4. The mod list for the options page and the export.
            var list = new List<ModEntry>();
            int excludedMods = 0;
            foreach (var mod in catalog.Used)
            {
                if (mod.TextCount == 0) continue;
                list.Add(mod);
                if (mod.Excluded) excludedMods++;
            }
            list.Sort((a, b) => string.Compare(a.AssemblyName, b.AssemblyName, StringComparison.OrdinalIgnoreCase));
            s_ModList = list;
            s_ModsWithTexts = list.Count;
            s_ExcludedMods = excludedMods;
            m_Service.SetExcludedModTexts(excludedValues);
            m_Service.SetModLanguageTexts(languageValues);
        }

        /// <summary>Decides each key's text in the target language (or queues it), into <see cref="m_Desired"/>.</summary>
        private void BuildDesired(string target, out List<string> requestEnglish, out List<string> requestAuto)
        {
            requestEnglish = new List<string>();
            requestAuto = new List<string>();
            m_Desired.Clear();
            m_KeysByText.Clear();
            m_LangKeysByText.Clear();
            m_LangRequests.Clear();
            m_DirtyKeys.Clear();
            var foreign = new HashSet<string>(StringComparer.Ordinal);
            var overrides = m_Service.CurrentOverrides;
            var exclusions = m_Service.CurrentExclusions;
            int found = 0, translated = 0;

            foreach (var pair in m_Keys)
            {
                var key = pair.Key;
                var info = pair.Value;
                // Excluded mods: never touched (no override, no cache, no engine). A key no longer in m_Desired is taken
                // out of the game's localization by the next refresh (the locale is reloaded without it).
                if (info.Excluded) continue;
                bool counts = !string.IsNullOrWhiteSpace(info.English) && TextRules.HasLetters(info.English);
                if (counts) found++;

                // A mod the player set to another language: its English texts are translated into that language (the
                // main target's overrides and the mod's own main-language texts do not apply).
                var language = info.Mod?.Language;
                if (language != null)
                {
                    if (!counts || info.EnglishRank == 4 || exclusions.IsExcludedText(info.English)) continue;
                    var source = info.English;
                    if (TextRules.IsInTargetScript(source, language)) continue;
                    if (m_Service.TryGet(source, TextOrigin.Localization, language, out var inLanguage))
                    {
                        m_Desired[key] = inLanguage;
                        foreign.Add(inLanguage.Trim());
                        translated++;
                        continue;
                    }
                    if (source.Length > TranslationService.MaxTextLength) continue;
                    var sourceCore = source.Trim();
                    if (!m_LangKeysByText.TryGetValue(sourceCore, out var langKeys))
                    {
                        langKeys = new List<string>(1);
                        m_LangKeysByText[sourceCore] = langKeys;
                    }
                    langKeys.Add(key);
                    if (!m_LangRequests.TryGetValue(language, out var lists))
                    {
                        lists = (new List<string>(), new List<string>());
                        m_LangRequests[language] = lists;
                    }
                    (info.EnglishRank <= 2 ? lists.english : lists.auto).Add(sourceCore);
                    continue;
                }

                // An override by key is the player's explicit choice for this key: it wins over everything below.
                if (overrides.TryGetKey(key, out var byKey))
                {
                    m_Desired[key] = byKey;
                    if (counts) translated++;
                    continue;
                }
                // Excluded texts stay exactly as the mod wrote them (checked before the cache lookup).
                if (counts && exclusions.IsExcludedText(info.English)) continue;
                if (info.TargetValue != null)
                {
                    // The mod's own translation (possibly for a locale the game does not have, e.g. "ar-SA").
                    m_Desired[key] = info.TargetValue;
                    if (counts) translated++;
                    continue;
                }
                if (!counts || info.EnglishRank == 4) continue;
                var english = info.English;
                if (TextRules.IsInTargetScript(english, target)) continue;
                if (m_Service.TryGet(english, TextOrigin.Localization, out var known))
                {
                    m_Desired[key] = known;
                    translated++;
                    continue;
                }
                if (english.Length > TranslationService.MaxTextLength) continue;

                var core = english.Trim();
                if (!m_KeysByText.TryGetValue(core, out var keys))
                {
                    keys = new List<string>(1);
                    m_KeysByText[core] = keys;
                    if (info.EnglishRank <= 2) requestEnglish.Add(core);
                    else requestAuto.Add(core);
                }
                keys.Add(key);
            }

            // Community / player translations of keys no mod has (e.g. the game's own texts).
            foreach (var pair in overrides.Keys)
            {
                if (m_Desired.ContainsKey(pair.Key)) continue;
                if (m_Keys.TryGetValue(pair.Key, out var info) && info.Excluded) continue;
                m_Desired[pair.Key] = pair.Value;
            }

            m_Service.SetForeignTexts(foreign);
            s_FoundCount = found;
            s_TranslatedCount = translated;
            s_StatusVersion++;
        }

        private void RecountWaiting()
        {
            int waiting = 0, failed = 0;
            foreach (var pair in m_KeysByText)
            {
                if (m_Service.HasFailed(pair.Key)) failed += pair.Value.Count;
                else waiting += pair.Value.Count;
            }
            if (waiting != s_WaitingCount || failed != s_FailedCount)
            {
                s_WaitingCount = waiting;
                s_FailedCount = failed;
                s_StatusVersion++;
            }
        }

        /// <summary>The values of the game's own entries (en-US and the active locale), for <see cref="TranslationService.IsGameText"/>.</summary>
        private void RebuildGameTexts()
        {
            var fallback = LocalizationAccess.FallbackDictionary(m_Loc);
            LocalizationDictionary active = null;
            try { active = m_Loc.activeDictionary; }
            catch (Exception) { /* none */ }

            var signature = (active?.localeID ?? "") + "|" + (active?.entryCount ?? 0) + "|" + (fallback?.entryCount ?? 0) + "|" + m_Keys.Count;
            if (signature == m_GameTextsSignature) return;
            m_GameTextsSignature = signature;

            var texts = new HashSet<string>(StringComparer.Ordinal);
            AddGameTexts(fallback, texts);
            if (active != null && !ReferenceEquals(active, fallback)) AddGameTexts(active, texts);
            m_Service.SetGameTexts(texts);
        }

        private void AddGameTexts(LocalizationDictionary dictionary, HashSet<string> texts)
        {
            if (dictionary == null) return;
            try
            {
                foreach (var pair in dictionary.entries)
                {
                    if (string.IsNullOrEmpty(pair.Value) || m_Keys.ContainsKey(pair.Key) || m_Written.ContainsKey(pair.Key)) continue;
                    texts.Add(pair.Value);
                }
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"Reading the game's texts failed: {e.Message}");
            }
        }

        // ------------------------------------------------------------------ injection

        private void RefreshInjection(float now)
        {
            m_RefreshPending = false;
            m_LastRefresh = now;
            if (m_Loc == null) return;
            var active = SafeActiveLocale();
            if (active == null) return;

            if (m_Source != null && m_SourceLocale != active) Detach();

            if (m_Source == null)
            {
                m_DirtyKeys.Clear();
                m_FullVerify = false;
                if (m_Desired.Count == 0) return;
                Attach(active);
                return;
            }

            bool removed = false;
            foreach (var key in m_Written.Keys)
            {
                if (!m_Desired.ContainsKey(key)) { removed = true; break; }
            }

            m_Source.SetEntries(new Dictionary<string, string>(m_Desired, StringComparer.Ordinal));
            if (!LocalizationAccess.IsLast(m_Loc, m_SourceLocale, m_Source))
                LocalizationAccess.MoveToEnd(m_Loc, m_SourceLocale, m_Source);

            if (removed)
            {
                // Entries cannot be taken out of the dictionary: reload the locale (this source now lacks them).
                m_Written.Clear();
                Reload();
                m_FullVerify = true;
            }

            LocalizationDictionary dictionary;
            try { dictionary = m_Loc.activeDictionary; }
            catch (Exception) { return; }
            if (dictionary == null) return;

            int changed = 0;
            if (m_FullVerify)
            {
                foreach (var pair in m_Desired) changed += Write(dictionary, pair.Key, pair.Value);
            }
            else
            {
                foreach (var key in m_DirtyKeys)
                    if (m_Desired.TryGetValue(key, out var value)) changed += Write(dictionary, key, value);
            }
            m_FullVerify = false;
            m_DirtyKeys.Clear();

            if (changed > 0) NotifyChanged(now);
        }

        private int Write(LocalizationDictionary dictionary, string key, string value)
        {
            m_Written[key] = value;
            if (dictionary.TryGetValue(key, out var current) && string.Equals(current, value, StringComparison.Ordinal)) return 0;
            try
            {
                dictionary.Add(key, value);
                return 1;
            }
            catch (Exception)
            {
                // Invalid id (empty or whitespace): skip it.
                return 0;
            }
        }

        /// <summary>Adds a new source holding the translations to the locale (loads just that source and notifies once).</summary>
        private void Attach(string locale)
        {
            var source = new TranslatedSource(locale);
            source.SetEntries(new Dictionary<string, string>(m_Desired, StringComparer.Ordinal));
            m_Source = source;
            m_SourceLocale = locale;
            m_Written.Clear();
            foreach (var pair in m_Desired) m_Written[pair.Key] = pair.Value;
            m_SelfNotify = true;
            try
            {
                int before = LocalizationAccess.UserSources(m_Loc)?.Count ?? 0;
                m_Loc.AddSource(locale, source);
                // Our own source is not a new mod: do not rescan because of it.
                int after = LocalizationAccess.UserSources(m_Loc)?.Count ?? 0;
                if (m_UserSourceCount >= 0) m_UserSourceCount += after - before;
            }
            catch (Exception e)
            {
                Mod.Log.Error(e, "Adding the translations to the game's localization failed");
            }
            finally
            {
                m_SelfNotify = false;
            }
        }

        /// <summary>Takes the translations out of the game's localization.</summary>
        private void Detach()
        {
            var source = m_Source;
            var locale = m_SourceLocale;
            m_Source = null;
            m_SourceLocale = null;
            m_Written.Clear();
            if (source == null || m_Loc == null) return;
            source.Deactivate();
            m_SelfNotify = true;
            try
            {
                // Reloads that locale when it is active (or the fallback dictionary when it is en-US).
                int before = LocalizationAccess.UserSources(m_Loc)?.Count ?? 0;
                m_Loc.RemoveSource(locale, source);
                LocalizationAccess.ForgetUserSource(m_Loc, locale, source);
                int after = LocalizationAccess.UserSources(m_Loc)?.Count ?? 0;
                if (m_UserSourceCount >= 0) m_UserSourceCount += after - before;
                // The active locale merged the fallback dictionary while it held the translations: rebuild it.
                if (locale == m_Loc.fallbackLocaleId && m_Loc.activeLocaleId != locale) m_Loc.ReloadActiveLocale();
            }
            catch (Exception e)
            {
                Mod.Log.Warn(e, "Removing the translations from the game's localization failed");
            }
            finally
            {
                m_SelfNotify = false;
            }
        }

        private void ClearInjection()
        {
            m_DirtyKeys.Clear();
            Detach();
        }

        private void Reload()
        {
            m_SelfNotify = true;
            try { m_Loc.ReloadActiveLocale(); }
            catch (Exception e) { Mod.Log.Warn(e, "Reloading the active locale failed"); }
            finally { m_SelfNotify = false; }
        }

        /// <summary>Tells the game (and its UI) that the active dictionary changed; ignored by our own handler.</summary>
        private void NotifyChanged(float now)
        {
            if (now - m_NotificationWindowStart > kNotificationWindow)
            {
                m_NotificationWindowStart = now;
                m_NotificationsInWindow = 0;
            }
            if (++m_NotificationsInWindow > kMaxNotificationsPerWindow)
            {
                // Another mod keeps reverting the texts: stop the ping-pong; the next window tries again.
                if (!m_NotificationLimitLogged)
                {
                    m_NotificationLimitLogged = true;
                    Mod.Log.Warn("The localization keeps being reloaded by something else; Auto Translate updates it less often.");
                }
                m_RefreshPending = true;
                m_FullVerify = true;
                m_NextRefreshAt = m_NotificationWindowStart + kNotificationWindow;
                return;
            }
            m_SelfNotify = true;
            try
            {
                if (!LocalizationAccess.Notify(m_Loc)) m_Loc.ReloadActiveLocale();
            }
            catch (Exception e)
            {
                Mod.Log.Warn(e, "Notifying the localization change failed");
            }
            finally
            {
                m_SelfNotify = false;
            }
        }

        private string SafeActiveLocale()
        {
            try { return m_Loc?.activeLocaleId; }
            catch (Exception) { return null; }
        }

        // ------------------------------------------------------------------ export

        private void Export()
        {
            var target = m_Service.TargetLanguage;
            if (string.IsNullOrEmpty(target))
            {
                SetMessage(Setting.Text("Nothing to export: no target language.", "لا شيء للتصدير: لا توجد لغة هدف."));
                return;
            }
            try
            {
                var overrides = m_Service.CurrentOverrides;
                var keys = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var pair in m_Keys)
                {
                    var info = pair.Value;
                    if (info.Excluded || info.TargetValue != null || info.EnglishRank == 4) continue;
                    if (m_Service.IsExcludedText(info.English)) continue;
                    if (string.IsNullOrWhiteSpace(info.English) || !TextRules.HasLetters(info.English)) continue;
                    if (overrides.TryGetKey(pair.Key, out _) || overrides.TryGetText(info.English.Trim(), out _)) continue;
                    if (TextRules.IsInTargetScript(info.English, target)) continue;
                    keys[pair.Key] = info.English;
                }
                var texts = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var text in m_Service.InterfaceTextsSnapshot())
                {
                    if (overrides.TryGetText(text, out _) || m_Service.IsGameText(text) || m_Service.IsExcludedInterfaceText(text)) continue;
                    texts[text] = text;
                }

                var root = new JObject
                {
                    ["language"] = target,
                    ["generated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    ["about"] = "Texts of your mods without a human translation into '" + target + "'. Replace each value with its translation, "
                        + "remove the lines you do not translate, and put the file in ModsData/AutoTranslate/Overrides/" + target + "/ (see README.md).",
                    ["keys"] = JObject.FromObject(keys),
                    ["texts"] = JObject.FromObject(texts),
                };
                Directory.CreateDirectory(Paths.ExportDir);
                var path = Path.Combine(Paths.ExportDir, "untranslated-" + Paths.SafeName(target) + "-" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture) + ".json");
                File.WriteAllText(path, root.ToString(Formatting.Indented), new UTF8Encoding(false));
                Mod.Log.Info($"Exported {keys.Count} keys and {texts.Count} interface texts to {path}");
                SetMessage(Setting.Text(
                    $"Exported {keys.Count} mod texts and {texts.Count} interface texts to {path}",
                    $"صُدِّر {keys.Count} نصاً من المودات و{texts.Count} نصاً من الواجهات إلى {path}"));
                Setting.OpenFolder(Paths.ExportDir);
            }
            catch (Exception e)
            {
                Mod.Log.Warn(e, "Exporting untranslated texts failed");
                SetMessage(Setting.Text("Export failed: " + e.Message, "فشل التصدير: " + e.Message));
            }
        }

        /// <summary>Writes the mods with localization texts to mods.json: the names to put in the exclusion list.</summary>
        private void ExportMods()
        {
            try
            {
                if (!m_Service.Enabled && m_Loc != null)
                {
                    // Translation is off, so nothing was scanned: read the sources just for the list.
                    ScanSources("");
                    m_Keys.Clear();
                }
                var mods = s_ModList;
                var array = new JArray();
                foreach (var mod in mods)
                {
                    array.Add(new JObject
                    {
                        ["assembly"] = mod.AssemblyName,
                        ["displayName"] = mod.DisplayName ?? "",
                        ["title"] = mod.Title ?? "",
                        ["texts"] = mod.TextCount,
                        ["excluded"] = mod.Excluded,
                        ["attribution"] = mod.Attribution,
                    });
                }
                var root = new JObject
                {
                    ["generated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    ["about"] = "Mods whose localization Auto Translate found. To never translate a mod, add its \"assembly\", \"displayName\" or \"title\" "
                        + "to \"mods\" in " + Exclusions.FilePath + " or to the \"Excluded mods\" field of the options page. "
                        + "\"unattributed\" entries are sources whose mod could not be identified; they are listed by the assembly of their type.",
                    ["exclusionsFile"] = Exclusions.FilePath,
                    ["mods"] = array,
                };
                Directory.CreateDirectory(Paths.DataDir);
                var path = Paths.ModListFile;
                var temp = path + ".tmp";
                File.WriteAllText(temp, root.ToString(Formatting.Indented), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
                Mod.Log.Info($"Exported {mods.Count} mods to {path}");
                SetMessage(Setting.Text(
                    $"Exported {mods.Count} mods ({s_ExcludedMods} excluded) to {path}",
                    $"صُدِّرت قائمة {mods.Count} مود ({s_ExcludedMods} مستثنى) إلى {path}"));
                Setting.OpenFolder(Paths.DataDir);
            }
            catch (Exception e)
            {
                Mod.Log.Warn(e, "Exporting the mod list failed");
                SetMessage(Setting.Text("Export failed: " + e.Message, "فشل التصدير: " + e.Message));
            }
        }
    }
}
