using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AutoTranslate.Core
{
    /// <summary>
    /// Hand-made translations for one target language. They always win over machine translation and are never sent to a
    /// translation engine. Read from BuiltInOverridesDir/&lt;language&gt;/*.json (community dictionaries shipped with the
    /// mod), then OverridesDir/&lt;language&gt;/*.json (the player's own files, which win). File format:
    /// <code>{ "keys": { "&lt;localization id&gt;": "translation" }, "texts": { "&lt;English text&gt;": "translation" } }</code>
    /// Files in a folder are read in name order; a later file wins over an earlier one. Immutable once loaded.
    /// </summary>
    internal sealed class Overrides
    {
        public static readonly Overrides Empty = new Overrides("", new Dictionary<string, string>(), new Dictionary<string, string>(), 0);

        private readonly Dictionary<string, string> m_Keys;
        private readonly Dictionary<string, string> m_Texts;

        private Overrides(string language, Dictionary<string, string> keys, Dictionary<string, string> texts, int files)
        {
            Language = language;
            m_Keys = keys;
            m_Texts = texts;
            FileCount = files;
        }

        public string Language { get; }

        /// <summary>Number of files read.</summary>
        public int FileCount { get; }

        /// <summary>Translations by localization id.</summary>
        public IReadOnlyDictionary<string, string> Keys => m_Keys;

        /// <summary>Translations by source (English) text.</summary>
        public IReadOnlyDictionary<string, string> Texts => m_Texts;

        public bool TryGetKey(string id, out string translation) => m_Keys.TryGetValue(id, out translation);

        public bool TryGetText(string text, out string translation) => m_Texts.TryGetValue(text, out translation);

        /// <summary>Reads the override files of <paramref name="language"/>; never throws (bad files are logged and skipped).</summary>
        public static Overrides Load(string language)
        {
            if (string.IsNullOrEmpty(language)) return Empty;
            var keys = new Dictionary<string, string>(StringComparer.Ordinal);
            var texts = new Dictionary<string, string>(StringComparer.Ordinal);
            int files = 0;
            files += LoadFolder(Path.Combine(Paths.BuiltInOverridesDir, Paths.SafeName(language)), keys, texts);
            files += LoadFolder(Path.Combine(Paths.OverridesDir, Paths.SafeName(language)), keys, texts);
            if (files > 0)
                Mod.Log.Info($"Overrides {language}: {files} file(s), {keys.Count} keys, {texts.Count} texts.");
            return new Overrides(language, keys, texts, files);
        }

        private static int LoadFolder(string dir, Dictionary<string, string> keys, Dictionary<string, string> texts)
        {
            string[] paths;
            try
            {
                if (!Directory.Exists(dir)) return 0;
                paths = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly);
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"Listing override files in {dir} failed: {e.Message}");
                return 0;
            }
            Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
            int count = 0;
            foreach (var path in paths)
            {
                try
                {
                    var root = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                    int added = Read(root["keys"] as JObject, keys, trimKeys: false);
                    added += Read(root["texts"] as JObject, texts, trimKeys: true);
                    count++;
                    if (added == 0) Mod.Log.Info($"Override file {path} has no translations.");
                }
                catch (Exception e)
                {
                    Mod.Log.Warn($"Override file {path} could not be read and was skipped: {e.Message}");
                }
            }
            return count;
        }

        private static int Read(JObject section, Dictionary<string, string> target, bool trimKeys)
        {
            if (section == null) return 0;
            int added = 0;
            foreach (var property in section.Properties())
            {
                if (property.Value == null || property.Value.Type != JTokenType.String) continue;
                var key = trimKeys ? property.Name.Trim() : property.Name;
                var value = (string)property.Value;
                if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(value)) continue;
                target[key] = value;
                added++;
            }
            return added;
        }
    }
}
