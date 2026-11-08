using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AutoTranslate.Providers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoTranslate.Core
{
    /// <summary>
    /// What Auto Translate must leave alone: the file ModsData/AutoTranslate/exclusions.json merged with the two fields
    /// of the options page. File format:
    /// <code>{ "terms": [ "Al Qassim" ], "texts": [ "Exact text" ], "mods": [ "SignBuilder" ] }</code>
    /// <list type="bullet">
    /// <item>terms: words or phrases kept unchanged INSIDE translated texts (masked as tokens, see
    /// <see cref="ProtectedTerms"/>); a text that is only a term is left as it is.</item>
    /// <item>texts: whole texts never translated (trimmed, case-insensitive).</item>
    /// <item>mods: mods whose texts are never translated, by assembly name, file name or options page title
    /// (case-insensitive; spaces, '-', '_' and '.' are ignored, so "Sign Builder" matches "SignBuilder").</item>
    /// </list>
    /// Immutable; every check is a hash lookup or the precompiled term expression. Rebuilt only when the file or the
    /// options change.
    /// </summary>
    internal sealed class Exclusions
    {
        public const string FileName = "exclusions.json";

        /// <summary>Mods excluded in a new exclusions.json: mods that bring their own Arabic texts, and Auto Translate itself.</summary>
        public static readonly string[] DefaultMods = { "SignBuilderSA", "SignBuilder", "ArabicStreetNames", "AutoTranslate" };

        /// <summary>Terms written in a new exclusions.json, as an example of the format.</summary>
        public static readonly string[] DefaultTerms = { "Sign Builder", "Auto Translate" };

        public static readonly Exclusions Empty = new Exclusions(ProtectedTerms.Empty, new List<string>(), new List<string>(), null);

        private static readonly char[] kSeparators = { ',', ';', '\n', '\r', '،', '؛' };

        private readonly HashSet<string> m_Texts;
        private readonly HashSet<string> m_ModKeys;
        private readonly List<string> m_TextList;
        private readonly List<string> m_ModList;

        private Exclusions(ProtectedTerms terms, List<string> texts, List<string> mods, string fileError)
        {
            Terms = terms ?? ProtectedTerms.Empty;
            m_TextList = texts;
            m_ModList = mods;
            m_Texts = new HashSet<string>(texts, StringComparer.OrdinalIgnoreCase);
            m_ModKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mod in mods)
            {
                var key = ModKey(mod);
                if (key.Length > 0) m_ModKeys.Add(key);
            }
            FileError = fileError;

            var signature = new StringBuilder();
            foreach (var term in Terms.Terms) signature.Append("t:").Append(term.ToLowerInvariant()).Append('\u0001');
            var sortedTexts = new List<string>(m_Texts);
            sortedTexts.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var text in sortedTexts) signature.Append("x:").Append(text.ToLowerInvariant()).Append('\u0001');
            var sortedMods = new List<string>(m_ModKeys);
            sortedMods.Sort(StringComparer.Ordinal);
            foreach (var mod in sortedMods) signature.Append("m:").Append(mod).Append('\u0001');
            Signature = signature.ToString();
        }

        public static string FilePath => Path.Combine(Paths.DataDir, FileName);

        public ProtectedTerms Terms { get; }

        public IReadOnlyList<string> Texts => m_TextList;

        public IReadOnlyList<string> Mods => m_ModList;

        /// <summary>Equal for equal lists (ignoring case and order).</summary>
        public string Signature { get; private set; }

        /// <summary>Why exclusions.json could not be read; null when it was read (or created).</summary>
        public string FileError { get; }

        public bool HasMods => m_ModKeys.Count > 0;

        /// <summary>Panels switched off from the floating button (exclusions.json "panels"); their texts and mods are already merged above.</summary>
        public IReadOnlyList<PanelEntry> Panels { get; private set; } = new List<PanelEntry>();

        /// <summary>The "mods" entries of exclusions.json itself (without the panels' mods).</summary>
        public IReadOnlyList<string> FileMods { get; private set; } = new List<string>();

        /// <summary>The entries of the "Excluded mods" options field.</summary>
        public IReadOnlyList<string> FieldMods { get; private set; } = new List<string>();

        // Mods and windows the player set to another language than the main target (panels with a "language").
        private Dictionary<string, string> m_ModLanguages = new Dictionary<string, string>(StringComparer.Ordinal);
        private Dictionary<string, string> m_TextLanguages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The language a mod is shown in (any of its names may match), or null for the main target.</summary>
        public string LanguageForMod(string assemblyName, string displayName, string title)
        {
            if (m_ModLanguages.Count == 0) return null;
            foreach (var name in new[] { assemblyName, displayName, title })
            {
                var key = ModKey(name);
                if (key.Length > 0 && m_ModLanguages.TryGetValue(key, out var language)) return language;
            }
            return null;
        }

        /// <summary>The language a text of a window set to another language is shown in, or null.</summary>
        public string LanguageForText(string text)
        {
            if (m_TextLanguages.Count == 0 || string.IsNullOrEmpty(text)) return null;
            return m_TextLanguages.TryGetValue(text.Trim(), out var language) ? language : null;
        }

        private void SetLanguages(List<PanelEntry> panels)
        {
            var mods = new Dictionary<string, string>(StringComparer.Ordinal);
            var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var signature = new StringBuilder(Signature);
            foreach (var panel in panels)
            {
                if (string.IsNullOrEmpty(panel.Language) || LanguageCodes.IsBlocked(panel.Language)) continue;
                var key = ModKey(panel.Mod);
                if (key.Length > 0) mods[key] = panel.Language;
                foreach (var text in panel.Texts)
                {
                    var trimmed = text.Trim();
                    if (trimmed.Length > 0) texts[trimmed] = panel.Language;
                }
                signature.Append("l:").Append(panel.Id).Append('=').Append(panel.Language).Append('\u0001');
            }
            m_ModLanguages = mods;
            m_TextLanguages = texts;
            Signature = signature.ToString();
        }

        /// <summary>
        /// True when the whole text must stay as it is: it is in the "texts" list, or it is one of the protected terms.
        /// Allocation free unless the text has outer whitespace or there are terms.
        /// </summary>
        public bool IsExcludedText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (m_Texts.Count > 0)
            {
                if (m_Texts.Contains(text)) return true;
                if (TextRules.HasOuterWhitespace(text) && m_Texts.Contains(text.Trim())) return true;
            }
            return !Terms.IsEmpty && text.Length <= ProtectedTerms.MaxTermLength + 16 && Terms.IsTerm(text);
        }

        /// <summary>True when any of the names (assembly name, file name, options title) is in the "mods" list.</summary>
        public bool IsExcludedMod(string assemblyName, string displayName, string title)
        {
            if (m_ModKeys.Count == 0) return false;
            return Matches(assemblyName) || Matches(displayName) || Matches(title);
        }

        private bool Matches(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var key = ModKey(name);
            return key.Length > 0 && m_ModKeys.Contains(key);
        }

        /// <summary>"Sign Builder", "sign-builder" and "SignBuilder" give the same key.</summary>
        internal static string ModKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var builder = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (char.IsWhiteSpace(c) || c == '-' || c == '_' || c == '.') continue;
                builder.Append(char.ToLowerInvariant(c));
            }
            return builder.ToString();
        }

        /// <summary>Splits an options field: entries separated by commas, semicolons (also Arabic ones) or line breaks.</summary>
        public static List<string> SplitField(string field)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(field)) return list;
            foreach (var part in field.Split(kSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                var entry = part.Trim();
                if (entry.Length > 0) list.Add(entry);
            }
            return list;
        }

        /// <summary>The last write time of exclusions.json (DateTime.MinValue when missing); a cheap file system query.</summary>
        public static DateTime FileStamp()
        {
            try
            {
                var path = FilePath;
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            }
            catch (Exception)
            {
                return DateTime.MinValue;
            }
        }

        /// <summary>Writes exclusions.json with the defaults when it does not exist. Never throws.</summary>
        public static void EnsureFile()
        {
            var path = FilePath;
            try
            {
                if (File.Exists(path)) return;
                Directory.CreateDirectory(Paths.DataDir);
                var root = new JObject
                {
                    ["terms"] = new JArray(DefaultTerms),
                    ["texts"] = new JArray(),
                    ["mods"] = new JArray(DefaultMods),
                };
                var temp = path + ".tmp";
                File.WriteAllText(temp, root.ToString(Formatting.Indented), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(temp);
                else File.Move(temp, path);
                Mod.Log.Info($"Created {path} with the default exclusions.");
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"Creating {path} failed: {e.Message}");
            }
        }

        /// <summary>
        /// Reads exclusions.json (creating it with the defaults when missing) and merges the options fields. Never throws:
        /// an unreadable file is logged and reported in <see cref="FileError"/>, and only the options fields apply then.
        /// </summary>
        public static Exclusions Load(string termsField, string modsField)
        {
            EnsureFile();
            var terms = new List<string>();
            var texts = new List<string>();
            var mods = new List<string>();
            string error = null;

            var path = FilePath;
            try
            {
                if (File.Exists(path))
                {
                    var root = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                    ReadList(root, "terms", terms);
                    ReadList(root, "texts", texts);
                    ReadList(root, "mods", mods);
                }
            }
            catch (Exception e)
            {
                error = e.Message;
                Mod.Log.Warn($"{path} could not be read ({e.Message}); only the exclusions typed on the options page apply.");
            }

            var fileMods = new List<string>(mods);
            var fieldMods = SplitField(modsField);
            var panels = new List<PanelEntry>();
            if (error == null)
            {
                try
                {
                    if (File.Exists(path)) ReadPanels(JObject.Parse(File.ReadAllText(path, Encoding.UTF8)), panels);
                }
                catch (Exception)
                {
                    // Read above without error a moment ago; a concurrent edit is picked up at the next check.
                }
            }
            foreach (var panel in panels)
            {
                // A panel with a language is translated into it, not excluded.
                if (!string.IsNullOrEmpty(panel.Language)) continue;
                texts.AddRange(panel.Texts);
                if (!string.IsNullOrEmpty(panel.Mod)) mods.Add(panel.Mod);
            }

            terms.AddRange(SplitField(termsField));
            mods.AddRange(fieldMods);

            var cleanTexts = new List<string>();
            var seenTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var text in texts)
            {
                var trimmed = text.Trim();
                if (trimmed.Length > 0 && seenTexts.Add(trimmed)) cleanTexts.Add(trimmed);
            }
            var cleanMods = new List<string>();
            var seenMods = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mod in mods)
            {
                var trimmed = mod.Trim();
                if (trimmed.Length > 0 && seenMods.Add(ModKey(trimmed))) cleanMods.Add(trimmed);
            }

            var protectedTerms = ProtectedTerms.Create(terms);
            if (protectedTerms.Ignored > 0)
                Mod.Log.Info($"{protectedTerms.Ignored} protected term(s) were ignored (empty, without a letter or digit, longer than {ProtectedTerms.MaxTermLength} characters, or over {ProtectedTerms.MaxTerms}).");
            var result = new Exclusions(protectedTerms, cleanTexts, cleanMods, error)
            {
                Panels = panels,
                FileMods = fileMods,
                FieldMods = fieldMods,
            };
            result.SetLanguages(panels);
            return result;
        }

        private static void ReadPanels(JObject root, List<PanelEntry> target)
        {
            if (!(root["panels"] is JArray array)) return;
            foreach (var item in array)
            {
                if (!(item is JObject obj)) continue;
                var entry = new PanelEntry
                {
                    Id = (string)obj["id"] ?? "",
                    Label = (string)obj["label"] ?? "",
                    Mod = (string)obj["mod"] ?? "",
                    Language = ((string)obj["language"] ?? "").Trim(),
                };
                ReadList(obj, "texts", entry.Texts);
                if (entry.Id.Length == 0 || (entry.Texts.Count == 0 && entry.Mod.Length == 0)) continue;
                target.Add(entry);
            }
        }

        // ------------------------------------------------------------------ editing exclusions.json (floating button)

        /// <summary>Adds a switched-off panel to exclusions.json. Main thread. False when the file could not be written.</summary>
        public static bool AddPanel(PanelEntry entry)
        {
            return EditFile(root =>
            {
                if (!(root["panels"] is JArray array))
                {
                    array = new JArray();
                    root["panels"] = array;
                }
                array.Add(new JObject
                {
                    ["id"] = entry.Id,
                    ["label"] = entry.Label ?? "",
                    ["mod"] = entry.Mod ?? "",
                    ["language"] = entry.Language ?? "",
                    ["texts"] = new JArray(entry.Texts),
                });
            });
        }

        /// <summary>Removes a switched-off panel from exclusions.json.</summary>
        public static bool RemovePanel(string id)
        {
            return EditFile(root =>
            {
                if (!(root["panels"] is JArray array)) return;
                for (var i = array.Count - 1; i >= 0; i--)
                    if (array[i] is JObject obj && string.Equals((string)obj["id"], id, StringComparison.Ordinal)) array.RemoveAt(i);
            });
        }

        /// <summary>Removes from the "mods" list of exclusions.json every entry that names one of these mod names.</summary>
        public static bool RemoveFileMod(params string[] names)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in names)
            {
                var key = ModKey(name);
                if (key.Length > 0) keys.Add(key);
            }
            return EditFile(root =>
            {
                var token = root["mods"];
                if (token != null && token.Type == JTokenType.String)
                {
                    // "mods": "A, B" → a list without the removed ones.
                    var list = new JArray();
                    foreach (var entry in SplitField((string)token))
                        if (!keys.Contains(ModKey(entry))) list.Add(entry);
                    root["mods"] = list;
                    return;
                }
                if (!(token is JArray array)) return;
                for (var i = array.Count - 1; i >= 0; i--)
                    if (array[i].Type == JTokenType.String && keys.Contains(ModKey((string)array[i]))) array.RemoveAt(i);
            });
        }

        /// <summary>
        /// Reads exclusions.json, applies the change and writes it back atomically (other fields kept). An unreadable
        /// file is never overwritten: the player's own edits are worth more than a click.
        /// </summary>
        private static bool EditFile(Action<JObject> change)
        {
            EnsureFile();
            var path = FilePath;
            try
            {
                var root = File.Exists(path) ? JObject.Parse(File.ReadAllText(path, Encoding.UTF8)) : new JObject();
                change(root);
                Directory.CreateDirectory(Paths.DataDir);
                var temp = path + ".tmp";
                File.WriteAllText(temp, root.ToString(Formatting.Indented), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
                return true;
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"Editing {path} failed: {e.Message}");
                return false;
            }
        }

        private static void ReadList(JObject root, string name, List<string> target)
        {
            var token = root[name];
            if (token == null) return;
            if (token.Type == JTokenType.String)
            {
                // A single string is accepted too ("terms": "Al Qassim, Riyadh").
                target.AddRange(SplitField((string)token));
                return;
            }
            if (!(token is JArray array)) return;
            foreach (var item in array)
            {
                if (item.Type == JTokenType.String)
                {
                    var value = (string)item;
                    if (!string.IsNullOrWhiteSpace(value)) target.Add(value);
                }
            }
        }
    }
}

namespace AutoTranslate.Core
{
    /// <summary>A panel switched off from the floating button: the texts it showed, and its mod when it was recognised.</summary>
    internal sealed class PanelEntry
    {
        public string Id = "";
        public string Label = "";
        /// <summary>The mod's assembly name ("" when the panel could not be attributed to a mod).</summary>
        public string Mod = "";
        /// <summary>"" = not translated (left as the mod wrote it); otherwise the language it is translated into.</summary>
        public string Language = "";
        public readonly System.Collections.Generic.List<string> Texts = new System.Collections.Generic.List<string>();
    }
}
