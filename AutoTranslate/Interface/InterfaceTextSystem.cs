using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AutoTranslate.Core;
using Colossal.UI.Binding;
using Game.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoTranslate.Interface
{
    /// <summary>
    /// Bridges the UI hook (UI/src/hook.ts) and <see cref="TranslationService"/>: the hook collects the texts that mod
    /// interfaces draw directly into the page (not through the localization system), this system filters them, answers
    /// what the cache already knows and queues the rest for translation, then pushes the translations back in batches.
    /// Binding group "AutoTranslate" (the UI module id).
    /// <list type="bullet">
    /// <item>value <c>config</c>: JSON <c>{ enabled, target, rtl, generation }</c>. A new generation means the hook
    /// must restore every original text, forget its map and request again (cache reset, new target, new service).</item>
    /// <item>trigger <c>hello</c>: the hook (re)started with an empty state; forget what was sent to it.</item>
    /// <item>trigger <c>request</c>(string): JSON array of texts seen in the UI. The hook asks for a text once per
    /// generation; asking again for one already answered means the answer was lost, so it is sent again.</item>
    /// <item>value <c>translations</c>: JSON delta <c>{ nonce, generation, map: { text: translation } }</c> holding only
    /// texts the hook asked for and has not received yet. An empty translation means "leave this text as it is" (game
    /// text, already in the target script, not translatable), so the hook stops waiting for it.</item>
    /// </list>
    /// </summary>
    public partial class InterfaceTextSystem : UISystemBase
    {
        public const string Group = "AutoTranslate";

        /// <summary>Longest text taken from the UI; longer ones are paragraphs or data, not labels.</summary>
        private const int MaxTextLength = 500;
        /// <summary>Most texts handled from one request (the hook sends at most 200).</summary>
        private const int MaxTextsPerRequest = 400;
        /// <summary>Most texts kept waiting for the service; beyond this new requests are answered "leave as is".</summary>
        private const int MaxAwaiting = 20000;
        /// <summary>Minimum time between two pushes of <c>translations</c> (at most ~4 updates per second).</summary>
        private static readonly TimeSpan PushInterval = TimeSpan.FromMilliseconds(250);
        /// <summary>How often texts still waiting are looked up again, in case a Translated event did not name them.</summary>
        private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(3);

        private ValueBinding<string> m_Config;
        private ValueBinding<string> m_Translations;

        private TranslationService m_Service;
        private bool m_Enabled;
        private string m_Target = "";
        private int m_Generation;
        private int m_Nonce;

        /// <summary>Texts answered to the hook in this generation (never sent twice).</summary>
        private readonly HashSet<string> m_Sent = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Texts the hook asked for that the service is translating.</summary>
        private readonly HashSet<string> m_Awaiting = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Waiting texts that go into another language than the main target (a window or mod set to it).</summary>
        private readonly Dictionary<string, string> m_AwaitingLanguage = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>Answers waiting for the next push.</summary>
        private readonly Dictionary<string, string> m_Outbox = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Texts named by <see cref="TranslationService.Translated"/>, drained in <see cref="OnUpdate"/>.</summary>
        private readonly ConcurrentQueue<IReadOnlyList<string>> m_TranslatedQueue = new ConcurrentQueue<IReadOnlyList<string>>();
        private volatile bool m_ResetPending;

        private DateTime m_LastPush = DateTime.MinValue;
        private DateTime m_LastRecheck = DateTime.MinValue;

        private readonly Action<IReadOnlyList<string>> m_OnTranslated;
        private readonly Action m_OnReset;

        public InterfaceTextSystem()
        {
            m_OnTranslated = texts =>
            {
                if (texts != null && texts.Count > 0) m_TranslatedQueue.Enqueue(texts);
            };
            m_OnReset = () => m_ResetPending = true;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            AddBinding(m_Config = new ValueBinding<string>(Group, "config", BuildConfig()));
            AddBinding(m_Translations = new ValueBinding<string>(Group, "translations", ""));
            AddBinding(new TriggerBinding(Group, "hello", OnHello));
            AddBinding(new TriggerBinding<string>(Group, "request", OnRequest));
            CreateControls();
        }

        protected override void OnDestroy()
        {
            Attach(null);
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();

            // The service may not exist yet, or be replaced when the options change: follow it.
            var service = TranslationService.Instance;
            if (!ReferenceEquals(service, m_Service))
            {
                Attach(service);
                NewGeneration();
            }

            if (m_ResetPending)
            {
                m_ResetPending = false;
                NewGeneration();
            }

            UpdateConfig();
            UpdateControls();

            if (!m_Enabled)
            {
                while (m_TranslatedQueue.TryDequeue(out _)) { }
                return;
            }

            DrainTranslated();
            RecheckAwaiting();
            Push();
        }

        private void Attach(TranslationService service)
        {
            if (m_Service != null)
            {
                try
                {
                    m_Service.Translated -= m_OnTranslated;
                    m_Service.Reset -= m_OnReset;
                }
                catch (Exception e)
                {
                    Mod.Log.Warn(e, "Auto Translate: detaching the interface hook from the old service failed");
                }
            }

            m_Service = service;
            while (m_TranslatedQueue.TryDequeue(out _)) { }

            if (m_Service != null)
            {
                m_Service.Translated += m_OnTranslated;
                m_Service.Reset += m_OnReset;
            }
        }

        /// <summary>Forgets everything sent; the UI restores its originals and requests again.</summary>
        private void NewGeneration()
        {
            m_Generation++;
            ClearState();
        }

        private void ClearState()
        {
            m_Sent.Clear();
            m_Awaiting.Clear();
            m_AwaitingLanguage.Clear();
            m_Outbox.Clear();
        }

        private void UpdateConfig()
        {
            bool enabled;
            string target;
            try
            {
                enabled = m_Service != null && m_Service.Enabled && Mod.Settings != null && Mod.Settings.TranslateInterfaces;
                target = enabled ? (m_Service.TargetLanguage ?? "") : "";
            }
            catch (Exception e)
            {
                Mod.Log.Warn(e, "Auto Translate: reading the service state failed");
                enabled = false;
                target = "";
            }
            if (string.IsNullOrEmpty(target)) enabled = false;

            if (enabled != m_Enabled || !string.Equals(target, m_Target, StringComparison.Ordinal))
            {
                // Turning off, on, or switching language: the UI starts over.
                m_Enabled = enabled;
                m_Target = target;
                NewGeneration();
            }

            var config = BuildConfig();
            if (!string.Equals(config, m_Config.value, StringComparison.Ordinal)) m_Config.Update(config);
        }

        private string BuildConfig()
        {
            var sb = new StringBuilder(96);
            using (var writer = new JsonTextWriter(new System.IO.StringWriter(sb)))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("enabled");
                writer.WriteValue(m_Enabled);
                writer.WritePropertyName("target");
                writer.WriteValue(m_Target ?? "");
                writer.WritePropertyName("rtl");
                writer.WriteValue(Scripts.IsRightToLeft(m_Target));
                writer.WritePropertyName("generation");
                writer.WriteValue(m_Generation);
                writer.WriteEndObject();
            }
            return sb.ToString();
        }

        private void OnHello()
        {
            // A fresh page has no map: whatever was sent before is gone with the old page.
            ClearState();
        }

        private void OnRequest(string json)
        {
            if (!m_Enabled || m_Service == null || string.IsNullOrEmpty(json)) return;

            JArray array;
            try
            {
                array = JArray.Parse(json);
            }
            catch (Exception e)
            {
                Mod.Log.Warn(e, "Auto Translate: malformed interface text request");
                return;
            }

            Dictionary<string, List<string>> toRequest = null;
            var handled = 0;
            foreach (var token in array)
            {
                if (handled >= MaxTextsPerRequest) break;
                if (token.Type != JTokenType.String) continue;
                var text = (string)token;
                if (string.IsNullOrEmpty(text)) continue;
                handled++;

                if (m_Outbox.ContainsKey(text) || m_Awaiting.Contains(text)) continue;
                // Asked again after an answer was sent: the hook lost it (binding updates can coalesce), send it again.
                m_Sent.Remove(text);

                // Text we put there in another language (a mod set to it): not translated again.
                if (m_Service.IsForeignText(text) || !IsCandidate(text))
                {
                    m_Outbox[text] = "";
                    continue;
                }

                string translated;
                bool hit;
                string language;
                try
                {
                    language = m_Service.InterfaceLanguageFor(text);
                    hit = m_Service.TryGet(text, TextOrigin.Interface, language, out translated);
                }
                catch (Exception e)
                {
                    Mod.Log.Warn(e, "Auto Translate: cache lookup failed");
                    continue;
                }

                if (hit)
                {
                    m_Outbox[text] = Answer(text, translated);
                }
                else if (m_Awaiting.Count < MaxAwaiting)
                {
                    m_Awaiting.Add(text);
                    if (language != null) m_AwaitingLanguage[text] = language;
                    toRequest = toRequest ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    var group = language ?? "";
                    if (!toRequest.TryGetValue(group, out var list)) toRequest[group] = list = new List<string>();
                    list.Add(text);
                }
                else
                {
                    m_Outbox[text] = "";
                }
            }

            if (toRequest == null) return;
            foreach (var pair in toRequest)
            {
                try
                {
                    m_Service.Request(pair.Value, TextOrigin.Interface, "auto", pair.Key.Length == 0 ? null : pair.Key);
                }
                catch (Exception e)
                {
                    Mod.Log.Warn(e, "Auto Translate: queueing interface texts failed");
                    foreach (var text in pair.Value)
                    {
                        m_Awaiting.Remove(text);
                        m_AwaitingLanguage.Remove(text);
                    }
                }
            }
        }

        /// <summary>Whether a text from the UI should be translated at all.</summary>
        private bool IsCandidate(string text)
        {
            if (text.Length > MaxTextLength) return false;

            var latin = 0;
            var target = 0;
            var targetScript = Scripts.ForLanguage(m_Target);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '\u00C0' && c <= '\u024F' && c != '\u00D7' && c != '\u00F7'))
                {
                    latin++;
                }
                else if (targetScript != Script.Latin && Scripts.Is(targetScript, c))
                {
                    target++;
                }
            }

            // Numbers, symbols, single letters (units, keys) and texts already written in the target script.
            if (latin < 2) return false;
            if (target > 0 && target >= latin) return false;

            try
            {
                if (m_Service.IsGameText(text)) return false;
                // Exclusions. The hook only sees text in the page, not which mod drew it, so a mod exclusion can only be
                // applied to texts equal to a localization text of the excluded mod (collected by the scan); text an
                // excluded mod builds in its own code is not recognised. Excluded "texts" and protected terms alone are
                // skipped here; protected terms inside a text are masked by the service like for localization texts.
                if (m_Service.IsExcludedInterfaceText(text)) return false;
            }
            catch (Exception e)
            {
                Mod.Log.Warn(e, "Auto Translate: game text check failed");
                return false;
            }
            return true;
        }

        private static string Answer(string text, string translated)
        {
            if (string.IsNullOrEmpty(translated) || string.Equals(translated, text, StringComparison.Ordinal)) return "";
            return translated;
        }

        private void DrainTranslated()
        {
            while (m_TranslatedQueue.TryDequeue(out var texts))
            {
                if (m_Awaiting.Count == 0) continue;
                for (var i = 0; i < texts.Count; i++)
                {
                    var text = texts[i];
                    if (text != null && m_Awaiting.Contains(text)) Resolve(text);
                }
            }
        }

        /// <summary>Looks up waiting texts again now and then (a Translated event may name normalized texts).</summary>
        private void RecheckAwaiting()
        {
            if (m_Awaiting.Count == 0) return;
            var now = DateTime.UtcNow;
            if (now - m_LastRecheck < RecheckInterval) return;
            m_LastRecheck = now;

            List<string> found = null;
            foreach (var text in m_Awaiting)
            {
                string translated;
                bool hit;
                try
                {
                    hit = m_Service.TryGet(text, TextOrigin.Interface, LanguageOf(text), out translated);
                }
                catch (Exception e)
                {
                    Mod.Log.Warn(e, "Auto Translate: cache lookup failed");
                    return;
                }
                if (hit) (found ?? (found = new List<string>())).Add(text);
            }
            if (found == null) return;
            foreach (var text in found) Resolve(text);
        }

        private string LanguageOf(string text) => m_AwaitingLanguage.TryGetValue(text, out var language) ? language : null;

        private void Resolve(string text)
        {
            string translated;
            bool hit;
            try
            {
                hit = m_Service.TryGet(text, TextOrigin.Interface, LanguageOf(text), out translated);
            }
            catch (Exception e)
            {
                Mod.Log.Warn(e, "Auto Translate: cache lookup failed");
                return;
            }
            if (!hit) return;
            m_Awaiting.Remove(text);
            m_AwaitingLanguage.Remove(text);
            if (!m_Sent.Contains(text)) m_Outbox[text] = Answer(text, translated);
        }

        private void Push()
        {
            if (m_Outbox.Count == 0) return;
            var now = DateTime.UtcNow;
            if (now - m_LastPush < PushInterval) return;
            m_LastPush = now;

            m_Nonce++;
            var sb = new StringBuilder(64 + m_Outbox.Count * 48);
            using (var writer = new JsonTextWriter(new System.IO.StringWriter(sb)))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("nonce");
                writer.WriteValue(m_Nonce);
                writer.WritePropertyName("generation");
                writer.WriteValue(m_Generation);
                writer.WritePropertyName("map");
                writer.WriteStartObject();
                foreach (var pair in m_Outbox)
                {
                    writer.WritePropertyName(pair.Key);
                    writer.WriteValue(pair.Value);
                    m_Sent.Add(pair.Key);
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            m_Outbox.Clear();
            m_Translations.Update(sb.ToString());
        }

        /// <summary>Writing systems used to recognise texts already in the target language.</summary>
        private enum Script
        {
            Latin,
            Arabic,
            Hebrew,
            Cyrillic,
            Greek,
            Cjk,
            Hangul,
            Thai,
            Devanagari,
            Bengali,
            Tamil,
            Georgian,
            Armenian,
        }

        private static class Scripts
        {
            public static string BaseLanguage(string language)
            {
                if (string.IsNullOrEmpty(language)) return "";
                var cut = language.IndexOfAny(new[] { '-', '_' });
                return (cut > 0 ? language.Substring(0, cut) : language).ToLowerInvariant();
            }

            public static bool IsRightToLeft(string language)
            {
                switch (BaseLanguage(language))
                {
                    case "ar":
                    case "he":
                    case "iw":
                    case "fa":
                    case "ur":
                    case "ps":
                    case "yi":
                    case "sd":
                    case "ug":
                    case "ckb":
                    case "dv":
                        return true;
                    default:
                        return false;
                }
            }

            public static Script ForLanguage(string language)
            {
                switch (BaseLanguage(language))
                {
                    case "ar":
                    case "fa":
                    case "ur":
                    case "ps":
                    case "sd":
                    case "ug":
                    case "ckb":
                        return Script.Arabic;
                    case "he":
                    case "iw":
                    case "yi":
                        return Script.Hebrew;
                    case "ru":
                    case "uk":
                    case "be":
                    case "bg":
                    case "sr":
                    case "mk":
                    case "kk":
                    case "ky":
                    case "mn":
                    case "tg":
                        return Script.Cyrillic;
                    case "el":
                        return Script.Greek;
                    case "zh":
                    case "ja":
                        return Script.Cjk;
                    case "ko":
                        return Script.Hangul;
                    case "th":
                        return Script.Thai;
                    case "hi":
                    case "mr":
                    case "ne":
                        return Script.Devanagari;
                    case "bn":
                        return Script.Bengali;
                    case "ta":
                        return Script.Tamil;
                    case "ka":
                        return Script.Georgian;
                    case "hy":
                        return Script.Armenian;
                    default:
                        return Script.Latin;
                }
            }

            public static bool Is(Script script, char c)
            {
                switch (script)
                {
                    case Script.Arabic:
                        return (c >= '\u0600' && c <= '\u06FF') || (c >= '\u0750' && c <= '\u077F') || (c >= '\u08A0' && c <= '\u08FF') || (c >= '\uFB50' && c <= '\uFDFF') || (c >= '\uFE70' && c <= '\uFEFF');
                    case Script.Hebrew:
                        return (c >= '\u0590' && c <= '\u05FF') || (c >= '\uFB1D' && c <= '\uFB4F');
                    case Script.Cyrillic:
                        return c >= '\u0400' && c <= '\u052F';
                    case Script.Greek:
                        return (c >= '\u0370' && c <= '\u03FF') || (c >= '\u1F00' && c <= '\u1FFF');
                    case Script.Cjk:
                        return (c >= '\u3040' && c <= '\u30FF') || (c >= '\u3400' && c <= '\u4DBF') || (c >= '\u4E00' && c <= '\u9FFF') || (c >= '\uF900' && c <= '\uFAFF') || (c >= '\uFF66' && c <= '\uFF9F');
                    case Script.Hangul:
                        return (c >= '\uAC00' && c <= '\uD7AF') || (c >= '\u1100' && c <= '\u11FF') || (c >= '\u3130' && c <= '\u318F');
                    case Script.Thai:
                        return c >= '\u0E00' && c <= '\u0E7F';
                    case Script.Devanagari:
                        return c >= '\u0900' && c <= '\u097F';
                    case Script.Bengali:
                        return c >= '\u0980' && c <= '\u09FF';
                    case Script.Tamil:
                        return c >= '\u0B80' && c <= '\u0BFF';
                    case Script.Georgian:
                        return c >= '\u10A0' && c <= '\u10FF';
                    case Script.Armenian:
                        return c >= '\u0530' && c <= '\u058F';
                    default:
                        return false;
                }
            }
        }
    }
}
