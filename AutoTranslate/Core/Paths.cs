using System;
using System.IO;

namespace AutoTranslate.Core
{
    /// <summary>
    /// Folders of Auto Translate. The mod folder holds the dll and the shipped community dictionaries; the data folder
    /// (ModsData/AutoTranslate in the game's user data folder, next to other mods' data) holds the translation cache,
    /// the player's override dictionaries and exports.
    /// </summary>
    public static class Paths
    {
        public const string DataFolderName = "AutoTranslate";

        private static readonly object s_Lock = new object();
        private static string s_ModDir;
        private static string s_DataDir;

        /// <summary>Called by <see cref="Mod.OnLoad"/> on the main thread with the folder of the mod's dll.</summary>
        public static void Initialize(string modDir)
        {
            lock (s_Lock)
            {
                if (!string.IsNullOrEmpty(modDir)) s_ModDir = modDir;
            }
            // Resolves the data folder now, on the main thread (Unity's persistentDataPath is main-thread only).
            EnsureResolved();
        }

        /// <summary>
        /// Resolves the folders if that has not happened yet. Call it once on the main thread before any background
        /// code uses the paths (AutoTranslateSystem does).
        /// </summary>
        public static void EnsureResolved()
        {
            var _ = DataDir;
            var __ = ModDir;
        }

        /// <summary>The folder of the mod's dll (its install folder).</summary>
        public static string ModDir
        {
            get
            {
                lock (s_Lock)
                {
                    if (string.IsNullOrEmpty(s_ModDir))
                    {
                        try { s_ModDir = Path.GetDirectoryName(typeof(Paths).Assembly.Location) ?? ""; }
                        catch (Exception) { s_ModDir = ""; }
                    }
                    return s_ModDir;
                }
            }
        }

        /// <summary>ModsData/AutoTranslate under the game's user data folder (created on demand by the users of the paths).</summary>
        public static string DataDir
        {
            get
            {
                lock (s_Lock)
                {
                    if (s_DataDir == null) s_DataDir = Path.Combine(UserDataRoot(), "ModsData", DataFolderName);
                    return s_DataDir;
                }
            }
        }

        /// <summary>The translation cache: one JSON file per target language.</summary>
        public static string CacheDir => Path.Combine(DataDir, "Cache");

        /// <summary>The player's override dictionaries: one folder per target language ("ar", "de", "zh-Hans").</summary>
        public static string OverridesDir => Path.Combine(DataDir, "Overrides");

        /// <summary>Community dictionaries shipped with the mod (ModDir/Resources/Overrides/&lt;language&gt;).</summary>
        public static string BuiltInOverridesDir => Path.Combine(ModDir, "Resources", "Overrides");

        /// <summary>Exports of untranslated texts for community translators.</summary>
        public static string ExportDir => Path.Combine(DataDir, "Export");

        /// <summary>The list of mods with localization texts, written by "Export mod list" (names to use in exclusions).</summary>
        public static string ModListFile => Path.Combine(DataDir, "mods.json");

        /// <summary>A file name safe form of a language code ("zh-Hans" stays "zh-Hans").</summary>
        public static string SafeName(string language)
        {
            if (string.IsNullOrEmpty(language)) return "none";
            var chars = language.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '-' && c != '_')
                    chars[i] = '_';
            }
            return new string(chars);
        }

        private static string UserDataRoot()
        {
            try
            {
                // Same root the game and other mods use (…/LocalLow/Colossal Order/Cities Skylines II).
                var path = UnityEngine.Application.persistentDataPath;
                if (!string.IsNullOrEmpty(path)) return path;
            }
            catch (Exception)
            {
                // Not on the main thread (or no Unity): use the known default location instead.
            }
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(profile, "AppData", "LocalLow", "Colossal Order", "Cities Skylines II");
        }
    }
}
