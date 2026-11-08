using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutoTranslate.Providers;

namespace AutoTranslate.Core
{
    /// <summary>
    /// The translation service shared by the localization part (AutoTranslateSystem) and the interface hook
    /// (Interface/). Lookups (<see cref="TryGet"/>) hit override dictionaries and the cache only; misses are queued with
    /// <see cref="Request"/> and translated by ONE background loop (batched, masked, rate limited, retried with
    /// backoff). Results come back to the main thread through <see cref="Translated"/>, raised by
    /// AutoTranslateSystem's update. Nothing here blocks the main thread or touches the network on it.
    /// </summary>
    public sealed class TranslationService : IDisposable
    {
        /// <summary>Longest text that is translated.</summary>
        public const int MaxTextLength = TextRules.MaxTextLength;

        private const int kMaxAttempts = 3;
        private const int kMaxQueued = 20000;
        private const int kMaxInterfaceTexts = 20000;
        private static readonly TimeSpan kMinRequestGap = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan kPauseAfterFailures = TimeSpan.FromSeconds(30);

        private static readonly object s_InstanceLock = new object();
        private static volatile TranslationService s_Instance;
        private static bool s_Disposed;

        private readonly Dictionary<string, TranslationCache> m_Caches = new Dictionary<string, TranslationCache>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<(Session session, string text)> m_Results = new ConcurrentQueue<(Session, string)>();
        private readonly List<string> m_Drained = new List<string>();
        private volatile Session m_Session;
        /// <summary>
        /// Sessions for other languages than the main target, created on demand: mods (or windows) the player chose to
        /// see in another language. Each has its own engine, queue and translation memory. Main thread.
        /// </summary>
        private readonly Dictionary<string, Session> m_Extra = new Dictionary<string, Session>(StringComparer.OrdinalIgnoreCase);
        private ServiceConfig m_Config;
        /// <summary>Texts we injected in another language than the main target (the interface hook leaves them alone).</summary>
        private HashSet<string> m_ForeignTexts = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Localization texts of mods shown in another language: text to that language (for the interface hook).</summary>
        private Dictionary<string, string> m_ModLanguageTexts = new Dictionary<string, string>(StringComparer.Ordinal);
        private volatile HashSet<string> m_GameTexts = new HashSet<string>(StringComparer.Ordinal);
        private volatile Exclusions m_Exclusions = Exclusions.Empty;
        private volatile HashSet<string> m_ExcludedModTexts = new HashSet<string>(StringComparer.Ordinal);
        private volatile string m_LastError = "";
        private volatile bool m_ResetPending;
        private volatile bool m_Paused;
        private int m_Version;
        private bool m_Disposed;

        private TranslationService()
        {
        }

        /// <summary>The service; created on first use, null after the mod is disposed.</summary>
        public static TranslationService Instance
        {
            get
            {
                var instance = s_Instance;
                if (instance != null) return instance;
                lock (s_InstanceLock)
                {
                    if (s_Instance == null && !s_Disposed) s_Instance = new TranslationService();
                    return s_Instance;
                }
            }
        }

        /// <summary>True when there is a target language (overrides and cache work even when the engine is unavailable).</summary>
        public bool Enabled => m_Session != null;

        /// <summary>Target language code ("ar", "de", "zh-Hans"); "" when off.</summary>
        public string TargetLanguage => m_Session?.Target ?? "";

        /// <summary>Whether interface texts are translated (the "Translate mod interfaces" option).</summary>
        public bool TranslateInterfaces => m_Session != null && m_Session.Interfaces;

        /// <summary>
        /// Raised on the main thread with the source texts (as passed to <see cref="Request"/>, trimmed) that just got
        /// translations; look them up with <see cref="TryGet"/>.
        /// </summary>
        public event Action<IReadOnlyList<string>> Translated;

        /// <summary>Raised on the main thread when the target language, the engine, the overrides or the cache changed: every earlier lookup may be stale.</summary>
        public event Action Reset;

        // ------------------------------------------------------------------ public API

        /// <summary>
        /// An override or cached translation of <paramref name="text"/>. Main thread; cheap; never queues. Texts in the
        /// exclusion list never have one (checked first, before the override and cache lookups); overrides always win
        /// over the cache.
        /// </summary>
        public bool TryGet(string text, TextOrigin origin, out string translated) => TryGet(text, origin, null, out translated);

        /// <summary>
        /// The same lookup in <paramref name="language"/> (null or the main target: the main session). Another language
        /// starts its session (and the loading of its memory) on first use. Main thread.
        /// </summary>
        public bool TryGet(string text, TextOrigin origin, string language, out string translated)
        {
            translated = null;
            var session = SessionFor(language);
            if (session == null || string.IsNullOrEmpty(text)) return false;
            if (origin == TextOrigin.Interface && !session.Interfaces) return false;
            if (m_Exclusions.IsExcludedText(text)) return false;

            if (LookUp(session, text, out translated)) return true;
            if (!TextRules.HasOuterWhitespace(text)) return false;
            var core = text.Trim();
            if (core.Length == 0 || !LookUp(session, core, out var coreTranslation)) return false;
            translated = TextRules.Rewrap(text, coreTranslation);
            return true;
        }

        /// <summary>
        /// Queues texts without a translation. Never blocks. Skips duplicates, empty texts, texts over
        /// <see cref="MaxTextLength"/> characters, texts already in the target language's script, texts with an override
        /// or a cached translation, and texts that failed this session. Source language: "en" for localization (taken
        /// from en-US), "auto" for interfaces.
        /// </summary>
        public void Request(IEnumerable<string> texts, TextOrigin origin)
        {
            Request(texts, origin, origin == TextOrigin.Localization ? "en" : "auto");
        }

        /// <summary>True when the text must stay exactly as it is (the exclusion "texts" list, or a protected term alone).</summary>
        public bool IsExcludedText(string text) => m_Exclusions.IsExcludedText(text);

        /// <summary>
        /// For interface texts: <see cref="IsExcludedText"/>, or the text equals a localization text of an excluded mod.
        /// The hook cannot tell which mod drew a text, so this is the closest it gets to excluding a mod's interface.
        /// </summary>
        public bool IsExcludedInterfaceText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (m_Exclusions.IsExcludedText(text)) return true;
            var set = m_ExcludedModTexts;
            if (set.Count == 0) return false;
            return set.Contains(text) || (TextRules.HasOuterWhitespace(text) && set.Contains(text.Trim()));
        }

        /// <summary>True when the text is one of the game's own (non-mod) texts; interfaces must not translate those.</summary>
        public bool IsGameText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            var set = m_GameTexts;
            if (set.Contains(text)) return true;
            return TextRules.HasOuterWhitespace(text) && set.Contains(text.Trim());
        }

        // ------------------------------------------------------------------ used by Core

        /// <summary>Increments whenever anything shown on the status page changes.</summary>
        internal int Version => Volatile.Read(ref m_Version);

        internal string LastError => m_LastError;

        /// <summary>The engine is pausing after repeated failures or a rate limit.</summary>
        internal bool Paused => m_Paused;

        /// <summary>The engine in use; null when off or unavailable.</summary>
        internal ProviderKind? ActiveProvider => m_Session?.Provider?.Kind;

        /// <summary>The engine could not be created (see <see cref="LastError"/>): only overrides and the cache work.</summary>
        internal bool EngineUnavailable => m_Session != null && m_Session.Provider == null;

        internal int PendingCount
        {
            get
            {
                var session = m_Session;
                if (session == null) return 0;
                lock (session.Lock) return session.Pending.Count;
            }
        }

        internal int FailedCount
        {
            get
            {
                var session = m_Session;
                if (session == null) return 0;
                lock (session.Lock) return session.Failed.Count;
            }
        }

        internal int TranslatedThisSession
        {
            get
            {
                var session = m_Session;
                return session == null ? 0 : Volatile.Read(ref session.TranslatedCount);
            }
        }

        internal Overrides CurrentOverrides => m_Session?.Overrides ?? Overrides.Empty;

        /// <summary>True when the text (trimmed) gave up after <c>3</c> attempts this session.</summary>
        internal bool HasFailed(string text)
        {
            var session = m_Session;
            if (session == null || string.IsNullOrEmpty(text)) return false;
            lock (session.Lock) return session.Failed.Contains(text.Trim());
        }

        /// <summary>Interface texts requested this session that have no override (for the export).</summary>
        internal List<string> InterfaceTextsSnapshot()
        {
            var session = m_Session;
            if (session == null) return new List<string>();
            lock (session.Lock) return new List<string>(session.InterfaceTexts);
        }

        internal void SetGameTexts(HashSet<string> texts)
        {
            m_GameTexts = texts ?? new HashSet<string>(StringComparer.Ordinal);
        }

        internal Exclusions CurrentExclusions => m_Exclusions;

        /// <summary>
        /// Uses new exclusions. Main thread. When they differ from the current ones, every cache forgets the entries
        /// affected by changed protected terms (in the background) and <see cref="Reset"/> is raised, so the
        /// localization is rescanned (excluded mods' injected texts are removed) and the interfaces start over.
        /// </summary>
        internal void SetExclusions(Exclusions exclusions)
        {
            exclusions = exclusions ?? Exclusions.Empty;
            var previous = m_Exclusions;
            if (string.Equals(previous.Signature, exclusions.Signature, StringComparison.Ordinal)) return;
            m_Exclusions = exclusions;
            if (!exclusions.Terms.SameTerms(previous.Terms))
            {
                lock (m_Caches)
                {
                    foreach (var cache in m_Caches.Values) cache.ApplyTerms(exclusions.Terms);
                }
            }
            BumpVersion();
            RaiseReset();
        }

        /// <summary>The localization texts of excluded mods (for <see cref="IsExcludedInterfaceText"/>); built by the scan.</summary>
        internal void SetExcludedModTexts(HashSet<string> texts)
        {
            m_ExcludedModTexts = texts ?? new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>
        /// True once the current language's translation memory is completely in memory. Until then nothing is queued
        /// for the engine (a text may be in the file), and a <see cref="Reset"/> follows the load so everyone looks again.
        /// </summary>
        internal bool CacheReady
        {
            get
            {
                var session = m_Session;
                return session != null && session.Cache.IsLoaded;
            }
        }

        /// <summary>Starts loading the translation memory of a language in the background (before the service starts).</summary>
        internal void Preload(string language)
        {
            if (m_Disposed || string.IsNullOrEmpty(language)) return;
            try { GetCache(language).EnsureLoading(); }
            catch (Exception e) { Mod.Log.Warn($"Preloading the translation cache failed: {e.Message}"); }
        }

        /// <summary>Writes every pending translation to disk now (blocking). Any thread; used at game exit.</summary>
        internal void FlushCaches()
        {
            TranslationCache[] caches;
            lock (m_Caches)
            {
                caches = new TranslationCache[m_Caches.Count];
                m_Caches.Values.CopyTo(caches, 0);
            }
            foreach (var cache in caches)
            {
                try { cache.Flush(); }
                catch (Exception e) { Mod.Log.Warn($"Saving the translation cache failed: {e.Message}"); }
            }
        }

        /// <summary>Translations saved for the current language (in memory, all written to disk within ~2 s); -1 when off or still loading.</summary>
        internal int SavedCount
        {
            get
            {
                var session = m_Session;
                if (session == null || !session.Cache.IsLoaded) return -1;
                return session.Cache.Count;
            }
        }

        /// <summary>Size of the current language's cache file in bytes (0 when none).</summary>
        internal long SavedBytes => m_Session?.Cache.FileSize ?? 0;

        /// <summary>Queues texts with an explicit source language ("en" or "auto").</summary>
        internal void Request(IEnumerable<string> texts, TextOrigin origin, string sourceLanguage) => Request(texts, origin, sourceLanguage, null);

        /// <summary>Queues texts for translation into <paramref name="language"/> (null: the main target).</summary>
        internal void Request(IEnumerable<string> texts, TextOrigin origin, string sourceLanguage, string language)
        {
            var session = SessionFor(language);
            if (session == null || texts == null) return;
            if (origin == TextOrigin.Interface && !session.Interfaces) return;
            // The translation memory is still loading: the texts may be in it. Nothing is queued now; the load raises
            // Reset, and the localization scan and the interface hook ask again then (see Configure).
            if (!session.Cache.IsLoaded) return;
            var exclusions = m_Exclusions;
            var excludedModTexts = m_ExcludedModTexts;
            bool queued = false;
            foreach (var raw in texts)
            {
                if (string.IsNullOrEmpty(raw) || raw.Length > MaxTextLength) continue;
                var text = TextRules.HasOuterWhitespace(raw) ? raw.Trim() : raw;
                if (text.Length == 0 || !TextRules.HasLetters(text)) continue;
                // Exclusions and overrides first: such texts are never sent to an engine.
                if (exclusions.IsExcludedText(text)) continue;
                if (origin == TextOrigin.Interface && excludedModTexts.Count > 0 && excludedModTexts.Contains(text)) continue;
                if (TextRules.IsInTargetScript(text, session.Target)) continue;
                if (session.Overrides.TryGetText(text, out _)) continue;
                if (session.Cache.TryGet(text, out _)) continue;
                lock (session.Lock)
                {
                    if (origin == TextOrigin.Interface && session.InterfaceTexts.Count < kMaxInterfaceTexts) session.InterfaceTexts.Add(text);
                    if (session.Provider == null) continue;
                    if (session.Failed.Contains(text) || session.Pending.Contains(text)) continue;
                    if (session.Pending.Count >= kMaxQueued)
                    {
                        if (!session.QueueFullLogged)
                        {
                            session.QueueFullLogged = true;
                            Mod.Log.Warn($"More than {kMaxQueued} texts are waiting for translation; further texts are skipped until the queue drains.");
                        }
                        continue;
                    }
                    session.Pending.Add(text);
                }
                session.Incoming.Enqueue(new WorkItem(text, sourceLanguage, origin == TextOrigin.Interface));
                queued = true;
            }
            if (queued)
            {
                BumpVersion();
                try { session.Signal.Release(); }
                catch (SemaphoreFullException) { /* already signalled */ }
                catch (ObjectDisposedException) { /* session stopped */ }
            }
        }

        /// <summary>
        /// Starts translating with <paramref name="config"/> (stopping the previous engine). Main thread. Raises
        /// <see cref="Reset"/>.
        /// </summary>
        internal void Configure(ServiceConfig config)
        {
            if (m_Disposed) return;
            StopSession();
            m_Paused = false;
            m_LastError = "";
            m_Config = config;

            if (config == null || !config.Enabled || string.IsNullOrEmpty(config.Target))
            {
                m_Session = null;
                BumpVersion();
                RaiseReset();
                return;
            }

            ITranslationProvider provider = null;
            try
            {
                provider = ProviderFactory.Create(config.Provider);
            }
            catch (Exception e)
            {
                m_LastError = e.Message;
                Mod.Log.Warn($"The translation engine {config.Provider.Kind} could not be started: {e.Message}");
            }

            var cache = GetCache(config.Target);
            var session = new Session(config.Target, config.Interfaces, provider, cache, Overrides.Load(config.Target));
            m_Session = session;
            var load = cache.EnsureLoading();
            if (!cache.IsLoaded)
            {
                // Lookups made before the file is read missed, and Request queued nothing meanwhile: once every entry is
                // in memory, everyone looks again (cached texts are injected without any network request).
                load.ContinueWith(_ => { if (m_Session == session) m_ResetPending = true; }, TaskScheduler.Default);
            }
            if (provider != null)
            {
                session.Loop = Task.Run(() => RunAsync(session));
            }
            Mod.Log.Info($"Translating into {config.Target} with {(provider != null ? provider.Kind.ToString() : "no engine (overrides and cache only)")}; interfaces {(config.Interfaces ? "on" : "off")}.");
            BumpVersion();
            RaiseReset();
        }

        /// <summary>Changes only the interface switch, without restarting the engine. Main thread.</summary>
        internal void SetInterfaces(bool enabled)
        {
            var session = m_Session;
            if (session == null || session.Interfaces == enabled) return;
            session.Interfaces = enabled;
            foreach (var extra in m_Extra.Values) extra.Interfaces = enabled;
            BumpVersion();
            RaiseReset();
        }

        /// <summary>Re-reads the override files and forgets this session's failures. Main thread. Raises <see cref="Reset"/>.</summary>
        internal void Retranslate()
        {
            var session = m_Session;
            if (session == null) return;
            session.Overrides = Overrides.Load(session.Target);
            lock (session.Lock) session.Failed.Clear();
            foreach (var extra in m_Extra.Values)
            {
                extra.Overrides = Overrides.Load(extra.Target);
                lock (extra.Lock) extra.Failed.Clear();
            }
            m_LastError = "";
            BumpVersion();
            RaiseReset();
        }

        /// <summary>Deletes every cached translation (all languages). Main thread. Raises <see cref="Reset"/>.</summary>
        internal void ClearCache()
        {
            lock (m_Caches)
            {
                foreach (var cache in m_Caches.Values) cache.Clear();
            }
            try
            {
                if (Directory.Exists(Paths.CacheDir))
                {
                    foreach (var file in Directory.GetFiles(Paths.CacheDir, "*.json*"))
                    {
                        try { File.Delete(file); }
                        catch (Exception e) { Mod.Log.Warn($"Deleting {file} failed: {e.Message}"); }
                    }
                }
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"Clearing the cache folder failed: {e.Message}");
            }
            var session = m_Session;
            if (session != null)
            {
                lock (session.Lock) session.Failed.Clear();
            }
            foreach (var extra in m_Extra.Values)
            {
                lock (extra.Lock) extra.Failed.Clear();
            }
            Mod.Log.Info("Translation cache cleared.");
            BumpVersion();
            RaiseReset();
        }

        /// <summary>
        /// Delivers the translations finished in the background: raises <see cref="Translated"/> (and a pending
        /// <see cref="Reset"/>). Called every frame by AutoTranslateSystem; allocation free when there is nothing new.
        /// </summary>
        internal void Pump()
        {
            if (m_ResetPending)
            {
                m_ResetPending = false;
                BumpVersion();
                RaiseReset();
            }
            if (m_Results.IsEmpty) return;
            var session = m_Session;
            m_Drained.Clear();
            while (m_Results.TryDequeue(out var result))
            {
                if (result.session == session || (result.session != null && m_Extra.TryGetValue(result.session.Target, out var extra) && extra == result.session))
                    m_Drained.Add(result.text);
            }
            if (m_Drained.Count == 0) return;
            BumpVersion();
            var handler = Translated;
            if (handler == null) return;
            var texts = m_Drained.ToArray();
            foreach (Action<IReadOnlyList<string>> listener in handler.GetInvocationList())
            {
                try { listener(texts); }
                catch (Exception e) { Mod.Log.Error(e, "A Translated listener failed"); }
            }
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            StopSession();
            m_Session = null;
            lock (m_Caches)
            {
                foreach (var cache in m_Caches.Values)
                {
                    try { cache.Dispose(); }
                    catch (Exception e) { Mod.Log.Warn($"Saving the translation cache failed: {e.Message}"); }
                }
                m_Caches.Clear();
            }
            Translated = null;
            Reset = null;
            lock (s_InstanceLock)
            {
                if (s_Instance == this) s_Instance = null;
                s_Disposed = true;
            }
        }

        // ------------------------------------------------------------------ other languages

        /// <summary>The session of a language: the main one for null, "" or the main target; null when translation is off.</summary>
        private Session SessionFor(string language)
        {
            var main = m_Session;
            if (main == null || string.IsNullOrEmpty(language) || string.Equals(language, main.Target, StringComparison.OrdinalIgnoreCase)) return main;
            if (m_Disposed) return null;
            if (m_Extra.TryGetValue(language, out var existing)) return existing;

            ITranslationProvider provider = null;
            try
            {
                if (m_Config != null) provider = ProviderFactory.Create(m_Config.Provider);
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"The translation engine for {language} could not be started: {e.Message}");
            }
            var cache = GetCache(language);
            var session = new Session(language, main.Interfaces, provider, cache, Overrides.Load(language));
            m_Extra[language] = session;
            var load = cache.EnsureLoading();
            if (!cache.IsLoaded) load.ContinueWith(_ => { if (m_Session == main) m_ResetPending = true; }, TaskScheduler.Default);
            if (provider != null) session.Loop = Task.Run(() => RunAsync(session));
            Mod.Log.Info($"Also translating into {language} (mods or windows chosen for that language).");
            return session;
        }

        /// <summary>Replaces the texts injected in other languages (built by the localization scan). Main thread.</summary>
        internal void SetForeignTexts(HashSet<string> texts) => m_ForeignTexts = texts ?? new HashSet<string>(StringComparer.Ordinal);

        internal void AddForeignText(string text)
        {
            if (!string.IsNullOrEmpty(text)) m_ForeignTexts.Add(text.Trim());
        }

        /// <summary>A text we put on screen in another language than the main target: the interface hook leaves it.</summary>
        internal bool IsForeignText(string text) => !string.IsNullOrEmpty(text) && m_ForeignTexts.Count > 0 && m_ForeignTexts.Contains(text.Trim());

        /// <summary>Only the mods and windows the player picked are translated (see <see cref="Exclusions.MainLanguage"/>).</summary>
        public bool ChosenOnly => m_Config != null && m_Config.ChosenOnly;

        internal void SetModLanguageTexts(Dictionary<string, string> texts) => m_ModLanguageTexts = texts ?? new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// The language an interface text must be translated into: a window or mod the player set to another language
        /// (its saved texts, or a localization text of that mod); null for the main target.
        /// </summary>
        internal string InterfaceLanguageFor(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var byPanel = m_Exclusions.LanguageForText(text);
            if (byPanel != null) return byPanel;
            return m_ModLanguageTexts.Count > 0 && m_ModLanguageTexts.TryGetValue(text.Trim(), out var language) ? language : null;
        }

        // ------------------------------------------------------------------ internals

        private static bool LookUp(Session session, string text, out string translated)
        {
            if (session.Overrides.TryGetText(text, out translated)) return true;
            return session.Cache.TryGet(text, out translated);
        }

        private TranslationCache GetCache(string language)
        {
            lock (m_Caches)
            {
                if (!m_Caches.TryGetValue(language, out var cache))
                {
                    cache = new TranslationCache(language, BumpVersion);
                    // The terms are known before the file is read, so the load already drops entries they invalidate.
                    cache.ApplyTerms(m_Exclusions.Terms);
                    cache.Invalidated += removed =>
                    {
                        // Entries were forgotten: lookups made before must be repeated (raised on the main thread by Pump).
                        if (removed > 0 && m_Session != null) m_ResetPending = true;
                    };
                    m_Caches[language] = cache;
                }
                return cache;
            }
        }

        private void StopSession()
        {
            foreach (var extra in m_Extra.Values) StopOne(extra);
            m_Extra.Clear();
            var session = m_Session;
            if (session == null) return;
            m_Session = null;
            StopOne(session);
        }

        private static void StopOne(Session session)
        {
            try { session.Cts.Cancel(); }
            catch (Exception) { /* already cancelled */ }
            var provider = session.Provider;
            if (provider == null) return;
            if (session.Loop == null)
            {
                SafeDispose(provider);
            }
            else
            {
                session.Loop.ContinueWith(_ => SafeDispose(provider), TaskScheduler.Default);
            }
        }

        private static void SafeDispose(IDisposable disposable)
        {
            try { disposable.Dispose(); }
            catch (Exception e) { Mod.Log.Warn($"Closing the translation engine failed: {e.Message}"); }
        }

        private void RaiseReset()
        {
            var handler = Reset;
            if (handler == null) return;
            foreach (Action listener in handler.GetInvocationList())
            {
                try { listener(); }
                catch (Exception e) { Mod.Log.Error(e, "A Reset listener failed"); }
            }
        }

        private void BumpVersion() => Interlocked.Increment(ref m_Version);

        /// <summary>The background loop of one session: batches, masks, translates, unmasks, caches.</summary>
        private async Task RunAsync(Session session)
        {
            var token = session.Cts.Token;
            var provider = session.Provider;
            var waiting = new List<WorkItem>();
            var batch = new List<WorkItem>();
            var masked = new List<MaskedText>();
            var inputs = new List<string>();
            var lastRequest = DateTime.MinValue;
            int consecutiveFailures = 0;
            // Leading urgent items in <waiting>; rate-limit pauses that followed each other without a success.
            int urgentCount = 0;
            int rateLimitStreak = 0;

            try
            {
                try { await session.Cache.EnsureLoading().ConfigureAwait(false); }
                catch (Exception e) { Mod.Log.Warn($"Loading the cache failed: {e.Message}"); }

                int maxTexts = Math.Max(1, provider.MaxTextsPerRequest);
                int maxChars = Math.Max(1, provider.MaxCharsPerRequest);

                while (!token.IsCancellationRequested)
                {
                    // What the player sees now goes first; the bulk of mod localization follows.
                    while (session.Incoming.TryDequeue(out var incoming))
                    {
                        if (incoming.Urgent) waiting.Insert(urgentCount++, incoming);
                        else waiting.Add(incoming);
                    }
                    if (waiting.Count == 0)
                    {
                        await session.Signal.WaitAsync(token).ConfigureAwait(false);
                        continue;
                    }

                    var now = DateTime.UtcNow;
                    if (now < session.PauseUntil)
                    {
                        await Task.Delay(session.PauseUntil - now, token).ConfigureAwait(false);
                        m_Paused = false;
                        BumpVersion();
                        continue;
                    }

                    // Takes the ready items of one source language, within the engine's limits.
                    batch.Clear();
                    masked.Clear();
                    inputs.Clear();
                    string sourceLanguage = null;
                    int chars = 0;
                    var earliest = DateTime.MaxValue;
                    for (int i = 0; i < waiting.Count; i++)
                    {
                        var item = waiting[i];
                        if (item.NotBefore > now)
                        {
                            if (item.NotBefore < earliest) earliest = item.NotBefore;
                            continue;
                        }
                        var exclusions = m_Exclusions;
                        if (exclusions.IsExcludedText(item.Text))
                        {
                            // Excluded while it waited: it stays as it is.
                            Complete(session, item.Text, deliver: false);
                            waiting.RemoveAt(i--);
                            continue;
                        }
                        if (session.Overrides.TryGetText(item.Text, out _) || session.Cache.TryGet(item.Text, out _))
                        {
                            // An override file (re-read while it waited) or a translation made meanwhile (same text
                            // queued from elsewhere, or an older session): never sent to the engine.
                            Complete(session, item.Text, deliver: true);
                            waiting.RemoveAt(i--);
                            continue;
                        }
                        MaskedText mask;
                        var terms = exclusions.Terms;
                        item.Terms = terms;
                        try { mask = TextMasking.Mask(item.Text, terms); }
                        catch (Exception e)
                        {
                            Mod.Log.Warn($"Masking a text failed ({e.Message}); it is left untranslated.");
                            mask = null;
                        }
                        if (mask == null || mask.IsEmpty || string.IsNullOrWhiteSpace(mask.Text))
                        {
                            // Nothing to translate (only placeholders, numbers or punctuation): the text stands as it is.
                            session.Cache.Set(item.Text, item.Text);
                            Complete(session, item.Text, deliver: false);
                            waiting.RemoveAt(i--);
                            continue;
                        }
                        if (sourceLanguage != null && item.SourceLanguage != sourceLanguage) continue;
                        if (batch.Count > 0 && (batch.Count >= maxTexts || chars + mask.Text.Length > maxChars)) continue;
                        sourceLanguage = item.SourceLanguage;
                        chars += mask.Text.Length;
                        item.Mask = mask;
                        batch.Add(item);
                        masked.Add(mask);
                        inputs.Add(mask.Text);
                        waiting.RemoveAt(i--);
                    }
                    urgentCount = 0;
                    for (int u = 0; u < waiting.Count && waiting[u].Urgent; u++) urgentCount++;

                    if (batch.Count == 0)
                    {
                        if (waiting.Count == 0) continue;
                        var delay = earliest == DateTime.MaxValue ? TimeSpan.FromMilliseconds(500) : earliest - now;
                        if (delay < TimeSpan.FromMilliseconds(50)) delay = TimeSpan.FromMilliseconds(50);
                        if (delay > TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
                        await Task.Delay(delay, token).ConfigureAwait(false);
                        continue;
                    }

                    var gap = lastRequest + kMinRequestGap - DateTime.UtcNow;
                    if (gap > TimeSpan.Zero) await Task.Delay(gap, token).ConfigureAwait(false);
                    lastRequest = DateTime.UtcNow;

                    IReadOnlyList<string> results;
                    try
                    {
                        results = await provider.TranslateAsync(inputs.ToArray(), sourceLanguage, session.Target, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception e)
                    {
                        var message = Flatten(e);
                        m_LastError = message;
                        consecutiveFailures++;
                        bool rateLimited = message.IndexOf("429", StringComparison.Ordinal) >= 0
                            || message.IndexOf("too many requests", StringComparison.OrdinalIgnoreCase) >= 0;
                        Mod.Log.Warn($"Translating {batch.Count} text(s) with {provider.Kind} failed: {message}");
                        var retryAt = DateTime.UtcNow;
                        if (rateLimited)
                        {
                            // The engine refused the request, not the texts: they go back to the front untouched
                            // (no attempt counted), and the pause grows while refusals continue: 30 s, 1, 2, 4, 8, 10 min.
                            for (int b = batch.Count - 1; b >= 0; b--)
                            {
                                batch[b].NotBefore = retryAt;
                                waiting.Insert(0, batch[b]);
                            }
                            var pause = TimeSpan.FromSeconds(Math.Min(600, kPauseAfterFailures.TotalSeconds * (1 << Math.Min(rateLimitStreak, 5))));
                            rateLimitStreak++;
                            session.PauseUntil = DateTime.UtcNow + pause;
                            consecutiveFailures = 0;
                            m_Paused = true;
                            Mod.Log.Info($"Pausing translation for {pause.TotalSeconds:0} s (rate limited, {rateLimitStreak} in a row).");
                        }
                        else
                        {
                            foreach (var item in batch) Fail(session, item, waiting, retryAt);
                            if (consecutiveFailures >= 3)
                            {
                                session.PauseUntil = DateTime.UtcNow + kPauseAfterFailures;
                                consecutiveFailures = 0;
                                m_Paused = true;
                                Mod.Log.Info($"Pausing translation for {kPauseAfterFailures.TotalSeconds:0} s (repeated failures).");
                            }
                        }
                        BumpVersion();
                        continue;
                    }

                    rateLimitStreak = 0;
                    bool anySuccess = false;
                    var failedAt = DateTime.UtcNow;
                    for (int i = 0; i < batch.Count; i++)
                    {
                        var item = batch[i];
                        if (!ReferenceEquals(item.Terms, m_Exclusions.Terms))
                        {
                            // The protected terms changed during the request: this translation may have translated a
                            // term (or kept one that is no longer protected). Queued again, masked with the new terms.
                            item.Mask = null;
                            waiting.Add(item);
                            continue;
                        }
                        var raw = results != null && i < results.Count ? results[i] : null;
                        string translation = null;
                        if (!string.IsNullOrWhiteSpace(raw))
                        {
                            try { translation = TextMasking.Unmask(masked[i], raw); }
                            catch (Exception e) { Mod.Log.Warn($"Unmasking a translation failed: {e.Message}"); }
                        }
                        if (string.IsNullOrWhiteSpace(translation))
                        {
                            Fail(session, item, waiting, failedAt);
                            continue;
                        }
                        anySuccess = true;
                        session.Cache.Set(item.Text, translation.Trim());
                        Interlocked.Increment(ref session.TranslatedCount);
                        Complete(session, item.Text, deliver: true);
                    }
                    if (anySuccess)
                    {
                        consecutiveFailures = 0;
                    }
                    else
                    {
                        consecutiveFailures++;
                        if (consecutiveFailures >= 3)
                        {
                            session.PauseUntil = DateTime.UtcNow + kPauseAfterFailures;
                            consecutiveFailures = 0;
                            m_Paused = true;
                        }
                    }
                    BumpVersion();
                }
            }
            catch (OperationCanceledException)
            {
                // Session stopped.
            }
            catch (ObjectDisposedException)
            {
                // Session stopped.
            }
            catch (Exception e)
            {
                m_LastError = Flatten(e);
                Mod.Log.Error(e, "The translation loop stopped unexpectedly");
            }
        }

        private void Complete(Session session, string text, bool deliver)
        {
            lock (session.Lock) session.Pending.Remove(text);
            if (deliver) m_Results.Enqueue((session, text));
        }

        private static void Fail(Session session, WorkItem item, List<WorkItem> waiting, DateTime now)
        {
            item.Attempts++;
            item.Mask = null;
            if (item.Attempts >= kMaxAttempts)
            {
                lock (session.Lock)
                {
                    session.Pending.Remove(item.Text);
                    session.Failed.Add(item.Text);
                }
                return;
            }
            // 2 s, then 4 s.
            item.NotBefore = now + TimeSpan.FromSeconds(1 << item.Attempts);
            waiting.Add(item);
        }

        private static string Flatten(Exception e)
        {
            while (e is AggregateException aggregate && aggregate.InnerException != null) e = aggregate.InnerException;
            var message = e.Message ?? e.GetType().Name;
            if (e.InnerException != null && !string.IsNullOrEmpty(e.InnerException.Message) && message.IndexOf(e.InnerException.Message, StringComparison.Ordinal) < 0)
                message += " (" + e.InnerException.Message + ")";
            return message;
        }

        private sealed class WorkItem
        {
            public readonly string Text;
            public readonly string SourceLanguage;
            public int Attempts;
            public DateTime NotBefore;
            public MaskedText Mask;

            /// <summary>The protected terms <see cref="Mask"/> was made with.</summary>
            public ProtectedTerms Terms;

            /// <summary>Text on screen right now (a mod interface): translated before the background localization work.</summary>
            public readonly bool Urgent;

            public WorkItem(string text, string sourceLanguage, bool urgent = false)
            {
                Text = text;
                Urgent = urgent;
                SourceLanguage = string.IsNullOrEmpty(sourceLanguage) ? "auto" : sourceLanguage;
            }
        }

        private sealed class Session
        {
            public readonly string Target;
            public readonly ITranslationProvider Provider;
            public readonly TranslationCache Cache;
            public readonly CancellationTokenSource Cts = new CancellationTokenSource();
            public readonly ConcurrentQueue<WorkItem> Incoming = new ConcurrentQueue<WorkItem>();
            public readonly SemaphoreSlim Signal = new SemaphoreSlim(0, 1);
            public readonly object Lock = new object();
            public readonly HashSet<string> Pending = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> Failed = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> InterfaceTexts = new HashSet<string>(StringComparer.Ordinal);
            public volatile Overrides Overrides;
            public volatile bool Interfaces;
            public Task Loop;
            public DateTime PauseUntil = DateTime.MinValue;
            public int TranslatedCount;
            public bool QueueFullLogged;

            public Session(string target, bool interfaces, ITranslationProvider provider, TranslationCache cache, Overrides overrides)
            {
                Target = target;
                Interfaces = interfaces;
                Provider = provider;
                Cache = cache;
                Overrides = overrides ?? Overrides.Empty;
            }
        }
    }
}
