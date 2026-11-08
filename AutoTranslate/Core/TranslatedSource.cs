using System.Collections.Generic;
using Colossal;

namespace AutoTranslate.Core
{
    /// <summary>
    /// Auto Translate's one localization source: localization id → translated text, added to the active game locale as
    /// its last source so a reload of the locale (language switch, another mod reloading) brings the translations back.
    /// Between reloads, new translations are written straight into the active dictionary (see AutoTranslateSystem);
    /// this source is kept in step so that reloads give the same result. Its entries are swapped as a whole
    /// (never mutated), so the game may read it from any thread.
    /// </summary>
    internal sealed class TranslatedSource : IDictionarySource
    {
        private static readonly Dictionary<string, string> s_Empty = new Dictionary<string, string>();

        private volatile Dictionary<string, string> m_Entries = s_Empty;
        private volatile bool m_Active = true;

        public TranslatedSource(string localeId)
        {
            LocaleId = localeId;
        }

        /// <summary>The game locale this source was added to.</summary>
        public string LocaleId { get; }

        public int Count => m_Active ? m_Entries.Count : 0;

        /// <summary>Replaces the entries; the dictionary must not be changed afterwards.</summary>
        public void SetEntries(Dictionary<string, string> entries)
        {
            m_Entries = entries ?? s_Empty;
        }

        /// <summary>Serves nothing from now on (the source is being removed).</summary>
        public void Deactivate()
        {
            m_Active = false;
            m_Entries = s_Empty;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return m_Active ? m_Entries : s_Empty;
        }

        public void Unload()
        {
        }

        public override string ToString() => "AutoTranslate translations (" + LocaleId + ")";
    }
}
