using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Colossal;
using Colossal.Localization;

namespace AutoTranslate.Core
{
    /// <summary>
    /// Reflection on Colossal.Localization.LocalizationManager (verified against the game's decompiled code):
    /// <list type="bullet">
    /// <item>m_UserSources : List&lt;(string, IDictionarySource)&gt; — every source added with AddSource (mods; vanilla text
    /// comes from LocaleAssets by another path). RemoveSource does NOT remove from it.</item>
    /// <item>m_LocaleInfos : Dictionary&lt;string, LocaleInfo&gt;, LocaleInfo.m_Sources : List&lt;IDictionarySource&gt; — the
    /// load order of each locale.</item>
    /// <item>m_FallbackDictionary : LocalizationDictionary — the en-US dictionary.</item>
    /// <item>NotifyActiveDictionaryChanged() — raises onActiveDictionaryChanged (synchronously on the main thread).</item>
    /// </list>
    /// Every member is optional: a missing one disables only the feature that needs it. Main thread only.
    /// </summary>
    internal static class LocalizationAccess
    {
        private const BindingFlags kInstance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static readonly FieldInfo s_UserSources;
        private static readonly FieldInfo s_LocaleInfos;
        private static readonly FieldInfo s_FallbackDictionary;
        private static readonly MethodInfo s_Notify;
        private static FieldInfo s_InfoSources;

        static LocalizationAccess()
        {
            var type = typeof(LocalizationManager);
            s_UserSources = type.GetField("m_UserSources", kInstance);
            s_LocaleInfos = type.GetField("m_LocaleInfos", kInstance);
            s_FallbackDictionary = type.GetField("m_FallbackDictionary", kInstance);
            s_Notify = type.GetMethod("NotifyActiveDictionaryChanged", kInstance, null, Type.EmptyTypes, null);
            if (s_UserSources == null || s_UserSources.FieldType != typeof(List<(string, IDictionarySource)>))
            {
                Mod.Log.Warn("LocalizationManager.m_UserSources not found: mod texts cannot be scanned.");
            }
            if (s_Notify == null) Mod.Log.Warn("LocalizationManager.NotifyActiveDictionaryChanged not found: translations are applied by reloading the locale.");
        }

        public static bool CanScan => s_UserSources != null && s_UserSources.FieldType == typeof(List<(string, IDictionarySource)>);

        public static bool CanNotify => s_Notify != null;

        /// <summary>The live list of sources added by mods (do not keep it across frames without re-reading).</summary>
        public static List<(string, IDictionarySource)> UserSources(LocalizationManager manager)
        {
            if (manager == null || !CanScan) return null;
            try { return s_UserSources.GetValue(manager) as List<(string, IDictionarySource)>; }
            catch (Exception) { return null; }
        }

        /// <summary>The fallback (en-US) dictionary; the active one when the field is missing and en-US is active.</summary>
        public static LocalizationDictionary FallbackDictionary(LocalizationManager manager)
        {
            if (manager == null) return null;
            if (s_FallbackDictionary != null)
            {
                try
                {
                    if (s_FallbackDictionary.GetValue(manager) is LocalizationDictionary dictionary) return dictionary;
                }
                catch (Exception)
                {
                    // Fall through.
                }
            }
            try
            {
                return manager.activeLocaleId == manager.fallbackLocaleId ? manager.activeDictionary : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Raises onActiveDictionaryChanged the way the game does. False when that is not possible.</summary>
        public static bool Notify(LocalizationManager manager)
        {
            if (manager == null || s_Notify == null) return false;
            try
            {
                s_Notify.Invoke(manager, null);
                return true;
            }
            catch (Exception e)
            {
                Mod.Log.Warn(e, "Raising onActiveDictionaryChanged failed");
                return false;
            }
        }

        /// <summary>Moves <paramref name="source"/> to the end of the locale's load order (and of the user sources), without reloading.</summary>
        public static void MoveToEnd(LocalizationManager manager, string localeId, IDictionarySource source)
        {
            if (manager == null || source == null || localeId == null) return;
            try
            {
                var sources = LocaleSources(manager, localeId);
                if (sources != null)
                {
                    int index = sources.IndexOf(source);
                    if (index >= 0 && index != sources.Count - 1)
                    {
                        sources.RemoveAt(index);
                        sources.Add(source);
                    }
                }
                var user = UserSources(manager);
                if (user != null)
                {
                    var entry = (localeId, source);
                    int index = user.IndexOf(entry);
                    if (index >= 0 && index != user.Count - 1)
                    {
                        user.RemoveAt(index);
                        user.Add(entry);
                    }
                }
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"Reordering the localization sources failed: {e.Message}");
            }
        }

        /// <summary>True when <paramref name="source"/> is the last source loaded into the locale (or that cannot be told).</summary>
        public static bool IsLast(LocalizationManager manager, string localeId, IDictionarySource source)
        {
            try
            {
                var sources = LocaleSources(manager, localeId);
                return sources == null || (sources.Count > 0 && ReferenceEquals(sources[sources.Count - 1], source));
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// Removes the (locale, source) pair from the user sources after RemoveSource (which leaves it there, so the
        /// same pair could never be added again and would be re-added on a full locale reload).
        /// </summary>
        public static void ForgetUserSource(LocalizationManager manager, string localeId, IDictionarySource source)
        {
            try
            {
                UserSources(manager)?.Remove((localeId, source));
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"Removing a localization source from the user list failed: {e.Message}");
            }
        }

        private static List<IDictionarySource> LocaleSources(LocalizationManager manager, string localeId)
        {
            if (s_LocaleInfos == null) return null;
            if (!(s_LocaleInfos.GetValue(manager) is IDictionary infos) || !infos.Contains(localeId)) return null;
            var info = infos[localeId];
            if (info == null) return null;
            if (s_InfoSources == null || s_InfoSources.DeclaringType != info.GetType())
                s_InfoSources = info.GetType().GetField("m_Sources", kInstance);
            return s_InfoSources?.GetValue(info) as List<IDictionarySource>;
        }
    }
}
