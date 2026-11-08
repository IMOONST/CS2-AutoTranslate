using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal;
using Game.Modding;
using Game.SceneFlow;

namespace AutoTranslate.Core
{
    /// <summary>One mod (or one group of localization sources that could not be attributed to a mod).</summary>
    internal sealed class ModEntry
    {
        /// <summary>The mod's assembly name ("SignBuilder"); for unattributed sources, the assembly of their type.</summary>
        public string AssemblyName;

        /// <summary>The name of the mod's asset (its dll file name, as the game lists it); "" when unknown.</summary>
        public string DisplayName = "";

        /// <summary>The title of the mod's options page (its en-US "Options.SECTION[...]" text); "" when it has none.</summary>
        public string Title = "";

        /// <summary>False when the sources could not be attributed to a loaded mod.</summary>
        public bool IsMod;

        /// <summary>"mod" (attributed to a loaded mod, by its assembly or its keys) or "unattributed".</summary>
        public string Attribution = "unattributed";

        /// <summary>Localization keys with a text, found in the last scan.</summary>
        public int TextCount;

        /// <summary>In the exclusion list at the last scan.</summary>
        public bool Excluded;
    }

    /// <summary>
    /// Attributes localization sources (LocalizationManager.m_UserSources) to mods. Main thread only.
    /// <para>
    /// 1. The source object's type → its Assembly → GameManager.instance.modManager.TryGetExecutableAsset(Assembly)
    /// (verified in the game's ModManager): a mod that implements IDictionarySource itself.<br/>
    /// 2. Otherwise (a mod using a type of the game, e.g. a memory source), the source's keys: a mod's options page keys
    /// embed its assembly name ("Options.SECTION[SignBuilder.SignBuilder.Mod]", "Options.OPTION[SignBuilder.…]", the
    /// ModSetting id is "&lt;assembly&gt;.&lt;namespace&gt;.&lt;type&gt;"), and most mods prefix their own keys the same way;
    /// the mod named by most keys wins.<br/>
    /// 3. Otherwise the sources are grouped under the assembly name of their type (not a mod).
    /// </para>
    /// </summary>
    internal sealed class ModCatalog
    {
        private readonly Dictionary<Assembly, ModEntry> m_ByAssembly = new Dictionary<Assembly, ModEntry>();
        private readonly Dictionary<string, ModEntry> m_ByName = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ModEntry> m_Unattributed = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every entry that got at least one source in this catalog's lifetime (one scan).</summary>
        public readonly List<ModEntry> Used = new List<ModEntry>();

        private readonly HashSet<ModEntry> m_UsedSet = new HashSet<ModEntry>();

        /// <summary>Reads the loaded mods from the game's mod manager.</summary>
        public static ModCatalog Build()
        {
            var catalog = new ModCatalog();
            ModManager manager = null;
            try { manager = GameManager.instance?.modManager; }
            catch (Exception) { /* not ready */ }
            if (manager == null) return catalog;
            try
            {
                foreach (var info in manager)
                {
                    try
                    {
                        var asset = info?.asset;
                        if (asset == null) continue;
                        Assembly assembly = null;
                        try { assembly = asset.assembly; }
                        catch (Exception) { /* not loaded */ }
                        string name = assembly?.GetName().Name;
                        if (string.IsNullOrEmpty(name))
                        {
                            try { name = asset.name; }
                            catch (Exception) { /* no definition */ }
                        }
                        if (string.IsNullOrEmpty(name)) continue;
                        string display = "";
                        try { display = asset.GetMeta().displayName ?? ""; }
                        catch (Exception) { /* no metadata */ }
                        var entry = new ModEntry { AssemblyName = name, DisplayName = display, IsMod = true, Attribution = "mod" };
                        if (assembly != null && !catalog.m_ByAssembly.ContainsKey(assembly)) catalog.m_ByAssembly[assembly] = entry;
                        if (!catalog.m_ByName.ContainsKey(name)) catalog.m_ByName[name] = entry;
                    }
                    catch (Exception e)
                    {
                        Mod.Log.Warn($"Reading a mod's information failed: {e.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"Listing the loaded mods failed: {e.Message}");
            }
            return catalog;
        }

        /// <summary>The mod a source belongs to (never null). <paramref name="entries"/> are the source's entries.</summary>
        public ModEntry Identify(IDictionarySource source, List<KeyValuePair<string, string>> entries)
        {
            Assembly assembly = null;
            try { assembly = source.GetType().Assembly; }
            catch (Exception) { /* dynamic type */ }

            ModEntry entry = null;
            if (assembly != null && m_ByAssembly.TryGetValue(assembly, out var direct))
            {
                entry = direct;
            }
            else if (m_ByName.Count > 0 && entries != null && entries.Count > 0)
            {
                entry = ByKeys(entries);
            }

            if (entry == null)
            {
                string name = "(unknown)";
                try { if (assembly != null) name = assembly.GetName().Name; }
                catch (Exception) { /* keep the placeholder */ }
                if (!m_Unattributed.TryGetValue(name, out entry))
                {
                    entry = new ModEntry { AssemblyName = name, IsMod = false, Attribution = "unattributed" };
                    m_Unattributed[name] = entry;
                }
            }

            if (m_UsedSet.Add(entry)) Used.Add(entry);
            return entry;
        }

        private ModEntry ByKeys(List<KeyValuePair<string, string>> entries)
        {
            Dictionary<ModEntry, int> votes = null;
            // A sample is enough to recognise the prefix; large sources are not walked completely.
            int limit = Math.Min(entries.Count, 400);
            for (int i = 0; i < limit; i++)
            {
                var key = entries[i].Key;
                var name = AssemblyPrefix(key);
                if (name == null || !m_ByName.TryGetValue(name, out var candidate)) continue;
                if (votes == null) votes = new Dictionary<ModEntry, int>();
                votes.TryGetValue(candidate, out var count);
                votes[candidate] = count + 1;
            }
            if (votes == null) return null;
            ModEntry best = null;
            int bestCount = 0;
            foreach (var pair in votes)
            {
                if (pair.Value > bestCount)
                {
                    best = pair.Key;
                    bestCount = pair.Value;
                }
            }
            return best;
        }

        /// <summary>"Options.OPTION[SignBuilder.SignBuilder.Mod.Setting.X]" → "SignBuilder"; "SignBuilder.Panel.Title" → "SignBuilder"; null otherwise.</summary>
        internal static string AssemblyPrefix(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            int start = key.IndexOf('[');
            start = start >= 0 ? start + 1 : 0;
            int end = key.IndexOf('.', start);
            if (end <= start) return null;
            return key.Substring(start, end - start);
        }

        /// <summary>True when the key is the title of a mod's options page ("Options.SECTION[&lt;assembly&gt;.…]").</summary>
        internal static bool IsOptionsTitleKey(string key, string assemblyName)
        {
            const string prefix = "Options.SECTION[";
            return key.Length > prefix.Length + assemblyName.Length
                && key.StartsWith(prefix, StringComparison.Ordinal)
                && string.Compare(key, prefix.Length, assemblyName, 0, assemblyName.Length, StringComparison.OrdinalIgnoreCase) == 0
                && key[prefix.Length + assemblyName.Length] == '.';
        }
    }
}
