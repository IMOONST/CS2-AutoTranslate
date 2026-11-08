using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoTranslate.Providers;
using Newtonsoft.Json;

namespace AutoTranslate.Core
{
    /// <summary>
    /// The persistent translation memory of one target language: every machine translation is kept forever (until the
    /// player clears the cache) in CacheDir/&lt;language&gt;.json, so a text is never sent to an engine twice.
    /// <para>
    /// FORMAT. A plain JSON object <c>{ "source text": "translation", ... }</c>, one entry per line, UTF-8 without BOM,
    /// written and read with Newtonsoft's streaming reader/writer (no intermediate DOM or string of the whole file):
    /// tens of thousands of entries load in a few tens of milliseconds.
    /// </para>
    /// <para>
    /// LOADING. <see cref="EnsureLoading"/> reads the whole file into memory on a background thread, once.
    /// <see cref="IsLoaded"/> turns true only after every entry is in memory. A file that cannot be parsed is renamed
    /// to .broken and the last good copy (.bak, kept by every save) is loaded instead; when the main file is missing but
    /// a complete temporary file exists (the game stopped between the two steps of a fallback replace), that one is used.
    /// </para>
    /// <para>
    /// SAVING. A change schedules a save 2 seconds later (changes in between join the same save, so a new translation
    /// reaches the disk within about 2 seconds); <see cref="Flush"/> and <see cref="Dispose"/> save at once. A save writes
    /// &lt;file&gt;.tmp completely (flushed to disk), then swaps it in with File.Replace, which keeps the previous file
    /// as .bak. Where File.Replace is not available (it can fail on Mono), the previous file is copied to .bak, deleted
    /// and the temporary file moved in its place; at every moment either the main file, or .tmp plus .bak, hold a
    /// complete copy, so a crash during a save never loses the memory.
    /// </para>
    /// <para>
    /// PROTECTED TERMS. A translation made before a term was protected may have translated that term. The terms each
    /// file was built with are recorded in &lt;language&gt;.terms.json; when the list changes (<see cref="ApplyTerms"/>,
    /// or at load), every entry whose source text contains a term that was added OR removed is forgotten, so those texts
    /// are translated again with the new list (entries without such a term stay valid and are kept).
    /// </para>
    /// Thread-safe. Lookups take a short lock and never touch the disk.
    /// </summary>
    internal sealed class TranslationCache : IDisposable
    {
        private static readonly TimeSpan kSaveDelay = TimeSpan.FromSeconds(2);
        private static readonly Encoding kUtf8 = new UTF8Encoding(false);
        private const int kBufferSize = 1 << 16;

        private readonly object m_Lock = new object();
        private readonly object m_IoLock = new object();
        private readonly Dictionary<string, string> m_Map = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Timer m_SaveTimer;
        private readonly Action m_Changed;
        private Task m_LoadTask;
        private volatile bool m_Loaded;
        private bool m_Dirty;
        private bool m_SaveScheduled;
        private bool m_Disposed;
        private int m_Generation;
        private long m_FileSize;

        // Protected terms: the list wanted now, and the list the entries in memory are valid for.
        private ProtectedTerms m_Terms = ProtectedTerms.Empty;
        private List<string> m_RecordedTerms = new List<string>();
        private bool m_TermsFileDirty;
        private int m_TermsVersion;

        /// <param name="changed">Called (from any thread) after the content or the file changed: loaded, saved, invalidated.</param>
        public TranslationCache(string language, Action changed = null)
        {
            Language = language;
            FilePath = Path.Combine(Paths.CacheDir, Paths.SafeName(language) + ".json");
            TermsPath = Path.Combine(Paths.CacheDir, Paths.SafeName(language) + ".terms.json");
            m_Changed = changed;
            m_SaveTimer = new Timer(OnSaveTimer, null, Timeout.Infinite, Timeout.Infinite);
        }

        public string Language { get; }

        public string FilePath { get; }

        private string TempPath => FilePath + ".tmp";

        private string BackupPath => FilePath + ".bak";

        private string BrokenPath => FilePath + ".broken";

        private string TermsPath { get; }

        /// <summary>True once the whole file was read (or found missing / unreadable).</summary>
        public bool IsLoaded => m_Loaded;

        /// <summary>Raised (from a background thread) when entries were forgotten because the protected terms changed.</summary>
        public event Action<int> Invalidated;

        public int Count
        {
            get { lock (m_Lock) return m_Map.Count; }
        }

        /// <summary>Size of the file on disk after the last load or save (0 when there is none).</summary>
        public long FileSize => Interlocked.Read(ref m_FileSize);

        /// <summary>Starts reading the file in the background (once); the task completes when it is fully loaded.</summary>
        public Task EnsureLoading()
        {
            lock (m_Lock)
            {
                if (m_LoadTask == null) m_LoadTask = Task.Run((Action)Load);
                return m_LoadTask;
            }
        }

        public bool TryGet(string text, out string translation)
        {
            lock (m_Lock)
            {
                return m_Map.TryGetValue(text, out translation);
            }
        }

        public void Set(string text, string translation)
        {
            if (string.IsNullOrEmpty(text) || translation == null) return;
            lock (m_Lock)
            {
                if (m_Disposed) return;
                if (m_Map.TryGetValue(text, out var existing) && existing == translation) return;
                m_Map[text] = translation;
                m_Dirty = true;
                ScheduleSaveLocked();
            }
        }

        /// <summary>
        /// Sets the protected terms. Once the file is loaded, entries containing a term that was added or removed since
        /// they were made are forgotten in the background (<see cref="Invalidated"/> reports how many).
        /// </summary>
        public void ApplyTerms(ProtectedTerms terms)
        {
            terms = terms ?? ProtectedTerms.Empty;
            int version;
            lock (m_Lock)
            {
                if (m_Disposed) return;
                m_Terms = terms;
                version = ++m_TermsVersion;
                if (!m_Loaded) return; // Load applies them.
            }
            Task.Run(() => InvalidateForTerms(version));
        }

        /// <summary>Forgets every translation and deletes the files (memory, backup, temporary and terms files).</summary>
        public void Clear()
        {
            lock (m_Lock)
            {
                m_Map.Clear();
                m_Dirty = false;
                m_SaveScheduled = false;
                m_Generation++;
                m_RecordedTerms = new List<string>(m_Terms.Terms);
                m_TermsFileDirty = !m_Terms.IsEmpty;
                if (!m_Disposed) m_SaveTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            lock (m_IoLock)
            {
                TryDelete(FilePath);
                TryDelete(TempPath);
                TryDelete(BackupPath);
                TryDelete(BrokenPath);
                TryDelete(TermsPath);
            }
            Interlocked.Exchange(ref m_FileSize, 0);
            m_Changed?.Invoke();
        }

        /// <summary>Writes pending changes now (blocking). Safe from any thread, also at process exit.</summary>
        public void Flush()
        {
            Save();
        }

        public void Dispose()
        {
            lock (m_Lock)
            {
                if (m_Disposed) return;
                m_Disposed = true;
                m_SaveScheduled = false;
            }
            try { m_SaveTimer.Dispose(); }
            catch (Exception) { /* already disposed */ }
            Save();
        }

        // ------------------------------------------------------------------ loading

        private void Load()
        {
            var started = DateTime.UtcNow;
            Dictionary<string, string> loaded = null;
            string loadedFrom = null;
            List<string> recorded = null;
            long size = 0;
            lock (m_IoLock)
            {
                try
                {
                    // A temporary file next to an existing main file is a save that did not finish: never use it.
                    if (File.Exists(FilePath)) TryDelete(TempPath);

                    if (File.Exists(FilePath))
                    {
                        loaded = TryRead(FilePath, out size);
                        if (loaded != null) loadedFrom = FilePath;
                        // The damaged file must not stay: the next save would turn it into the backup, over the good one.
                        else if (!MoveAside(FilePath, BrokenPath)) TryDelete(FilePath);
                    }
                    else if (File.Exists(TempPath))
                    {
                        // The game stopped between "delete the old file" and "move the new one in": the new one is complete.
                        loaded = TryRead(TempPath, out size);
                        if (loaded != null)
                        {
                            loadedFrom = TempPath;
                            try { File.Move(TempPath, FilePath); }
                            catch (Exception e) { Mod.Log.Warn($"Restoring {FilePath} from its temporary file failed: {e.Message}"); }
                        }
                        else
                        {
                            TryDelete(TempPath);
                        }
                    }

                    if (loaded == null && File.Exists(BackupPath))
                    {
                        loaded = TryRead(BackupPath, out size);
                        if (loaded != null)
                        {
                            loadedFrom = BackupPath;
                            Mod.Log.Warn($"Translation cache {Language}: using the backup {BackupPath}.");
                        }
                    }
                    recorded = ReadTerms();
                }
                catch (Exception e)
                {
                    Mod.Log.Warn($"Reading the translation cache {FilePath} failed ({e.Message}); starting with an empty cache.");
                }
            }

            int removed = 0;
            List<string> diff = null;
            lock (m_Lock)
            {
                if (loaded != null)
                {
                    foreach (var pair in loaded)
                    {
                        // Translations made while the file was loading are newer: keep them.
                        if (!m_Map.ContainsKey(pair.Key)) m_Map[pair.Key] = pair.Value;
                    }
                    // Loaded from the backup or a temporary file: write a proper main file soon.
                    if (loadedFrom != FilePath) { m_Dirty = true; ScheduleSaveLocked(); }
                }
                m_RecordedTerms = recorded ?? new List<string>();
                m_TermsVersion++;
                diff = m_Terms.Difference(m_RecordedTerms);
                if (diff.Count > 0)
                {
                    var changed = ProtectedTerms.Create(diff);
                    var keys = new List<string>();
                    foreach (var key in m_Map.Keys)
                        if (changed.Matches(key)) keys.Add(key);
                    foreach (var key in keys) m_Map.Remove(key);
                    removed = keys.Count;
                    m_RecordedTerms = new List<string>(m_Terms.Terms);
                    m_TermsFileDirty = true;
                    m_Dirty = true;
                    ScheduleSaveLocked();
                }
                m_Loaded = true;
            }
            Interlocked.Exchange(ref m_FileSize, size);

            var ms = (DateTime.UtcNow - started).TotalMilliseconds;
            if (loaded != null) Mod.Log.Info($"Translation cache {Language}: {loaded.Count} texts loaded in {ms:0} ms.");
            if (removed > 0)
                Mod.Log.Info($"Translation cache {Language}: {removed} texts forgotten because the protected terms changed ({string.Join(", ", diff)}); they are translated again.");
            m_Changed?.Invoke();
        }

        /// <summary>The entries of a cache file; null when it is not a valid file.</summary>
        private Dictionary<string, string> TryRead(string path, out long size)
        {
            size = 0;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, kBufferSize, FileOptions.SequentialScan))
                {
                    size = stream.Length;
                    // About 60 bytes per entry in practice: presizing avoids rehashing large dictionaries.
                    var map = new Dictionary<string, string>((int)Math.Min(size / 60 + 16, 1 << 20), StringComparer.Ordinal);
                    if (size == 0) return map;
                    using (var text = new StreamReader(stream, kUtf8, true, kBufferSize))
                    using (var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None, CloseInput = false })
                    {
                        if (!reader.Read()) return map;
                        if (reader.TokenType != JsonToken.StartObject) throw new JsonReaderException("The file is not a JSON object.");
                        while (reader.Read())
                        {
                            if (reader.TokenType == JsonToken.EndObject)
                            {
                                // Anything after the object other than whitespace means the file is damaged.
                                while (reader.Read())
                                    if (reader.TokenType != JsonToken.Comment) throw new JsonReaderException("Unexpected content after the object.");
                                return map;
                            }
                            if (reader.TokenType == JsonToken.Comment) continue;
                            if (reader.TokenType != JsonToken.PropertyName) throw new JsonReaderException("Unexpected " + reader.TokenType + ".");
                            var key = (string)reader.Value;
                            // Throws on an object or array value: such a file was not written by Auto Translate.
                            var value = reader.ReadAsString();
                            if (!string.IsNullOrEmpty(key) && value != null) map[key] = value;
                        }
                        throw new JsonReaderException("The file ends before the object is closed.");
                    }
                }
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"The translation cache file {path} is damaged or unreadable: {e.Message}");
                return null;
            }
        }

        private List<string> ReadTerms()
        {
            var terms = new List<string>();
            try
            {
                if (!File.Exists(TermsPath)) return terms;
                using (var text = new StreamReader(TermsPath, kUtf8, true))
                using (var reader = new JsonTextReader(text))
                {
                    if (!reader.Read() || reader.TokenType != JsonToken.StartArray) return terms;
                    while (reader.Read() && reader.TokenType != JsonToken.EndArray)
                        if (reader.TokenType == JsonToken.String) terms.Add((string)reader.Value);
                }
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"Reading {TermsPath} failed: {e.Message}");
            }
            return terms;
        }

        private void InvalidateForTerms(int version)
        {
            try
            {
                List<string> keys;
                ProtectedTerms changed;
                List<string> diff;
                lock (m_Lock)
                {
                    if (version != m_TermsVersion || m_Disposed) return;
                    diff = m_Terms.Difference(m_RecordedTerms);
                    if (diff.Count == 0) return;
                    changed = ProtectedTerms.Create(diff);
                    // A snapshot of the keys: the scan runs without the lock, lookups go on meanwhile.
                    keys = new List<string>(m_Map.Keys);
                }

                var matching = new List<string>();
                foreach (var key in keys)
                    if (changed.Matches(key)) matching.Add(key);

                lock (m_Lock)
                {
                    if (version != m_TermsVersion || m_Disposed) return;
                    foreach (var key in matching) m_Map.Remove(key);
                    m_RecordedTerms = new List<string>(m_Terms.Terms);
                    m_TermsFileDirty = true;
                    m_Dirty = true;
                    ScheduleSaveLocked();
                }
                Mod.Log.Info($"Translation cache {Language}: protected terms changed ({string.Join(", ", diff)}); {matching.Count} texts are translated again.");
                Invalidated?.Invoke(matching.Count);
                m_Changed?.Invoke();
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"Updating the translation cache {Language} for the protected terms failed: {e.Message}");
            }
        }

        // ------------------------------------------------------------------ saving

        private void ScheduleSaveLocked()
        {
            if (m_SaveScheduled || m_Disposed) return;
            m_SaveScheduled = true;
            m_SaveTimer.Change(kSaveDelay, Timeout.InfiniteTimeSpan);
        }

        private void OnSaveTimer(object state)
        {
            lock (m_Lock)
            {
                m_SaveScheduled = false;
            }
            Save();
        }

        private void Save()
        {
            KeyValuePair<string, string>[] snapshot;
            string[] terms = null;
            int generation;
            lock (m_Lock)
            {
                // Never overwrite the file with a partial map before it was read.
                if (!m_Dirty || !m_Loaded) return;
                snapshot = new KeyValuePair<string, string>[m_Map.Count];
                ((ICollection<KeyValuePair<string, string>>)m_Map).CopyTo(snapshot, 0);
                if (m_TermsFileDirty) terms = m_RecordedTerms.ToArray();
                m_Dirty = false;
                m_TermsFileDirty = false;
                generation = m_Generation;
            }

            try
            {
                lock (m_IoLock)
                {
                    lock (m_Lock)
                    {
                        // Cleared while the snapshot was taken: the snapshot is obsolete.
                        if (generation != m_Generation) return;
                    }
                    Directory.CreateDirectory(Paths.CacheDir);
                    long size = WriteCache(TempPath, snapshot);
                    ReplaceWithTemp();
                    Interlocked.Exchange(ref m_FileSize, size);
                    // After the entries: if the game stops in between, the old term list is still recorded, and the next
                    // load forgets (again) the texts containing the changed terms; it can never keep a stale translation.
                    if (terms != null) WriteTerms(terms);
                }
                m_Changed?.Invoke();
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"Saving the translation cache {FilePath} failed: {e.Message}");
                lock (m_Lock)
                {
                    m_Dirty = true;
                    if (terms != null) m_TermsFileDirty = true;
                    ScheduleSaveLocked();
                }
            }
        }

        private static long WriteCache(string path, KeyValuePair<string, string>[] entries)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, kBufferSize))
            {
                using (var text = new StreamWriter(stream, kUtf8, kBufferSize, leaveOpen: true))
                using (var writer = new JsonTextWriter(text) { Formatting = Formatting.None, CloseOutput = false })
                {
                    writer.WriteStartObject();
                    foreach (var pair in entries)
                    {
                        writer.WriteWhitespace("\n");
                        writer.WritePropertyName(pair.Key);
                        writer.WriteValue(pair.Value);
                    }
                    writer.WriteWhitespace("\n");
                    writer.WriteEndObject();
                    writer.WriteWhitespace("\n");
                    writer.Flush();
                    text.Flush();
                }
                // Really on disk before the old file is replaced.
                stream.Flush(true);
                return stream.Length;
            }
        }

        /// <summary>Puts the complete temporary file in place of the cache file, keeping the previous one as .bak.</summary>
        private void ReplaceWithTemp()
        {
            if (!File.Exists(FilePath))
            {
                File.Move(TempPath, FilePath);
                return;
            }
            try
            {
                File.Replace(TempPath, FilePath, BackupPath, true);
                return;
            }
            catch (Exception)
            {
                // Mono (and some file systems) cannot replace atomically: the safe sequence below. Without the temporary
                // file there is nothing to put in place (the caller retries the whole save).
                if (!File.Exists(TempPath)) throw;
            }
            if (!File.Exists(FilePath))
            {
                // A failed replace may already have moved the old file to the backup name.
                File.Move(TempPath, FilePath);
                return;
            }
            // 1. The current file becomes the backup (a copy, so the current file stays until the new one is ready).
            File.Copy(FilePath, BackupPath, true);
            // 2. From here until 3 completes, .tmp (new) and .bak (previous) are both complete; Load handles both.
            File.Delete(FilePath);
            // 3.
            File.Move(TempPath, FilePath);
        }

        private void WriteTerms(string[] terms)
        {
            var temp = TermsPath + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var text = new StreamWriter(stream, kUtf8, 4096, leaveOpen: true))
                using (var writer = new JsonTextWriter(text) { Formatting = Formatting.Indented, CloseOutput = false })
                {
                    writer.WriteStartArray();
                    foreach (var term in terms) writer.WriteValue(term);
                    writer.WriteEndArray();
                }
                stream.Flush(true);
            }
            TryDelete(TermsPath);
            File.Move(temp, TermsPath);
        }

        private static bool MoveAside(string path, string target)
        {
            try
            {
                // Keeps the unreadable file for inspection instead of overwriting it (and the good backup) at the next save.
                TryDelete(target);
                File.Move(path, target);
                Mod.Log.Warn($"The damaged file was renamed to {target}.");
                return true;
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"Renaming the damaged file {path} failed: {e.Message}");
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"Deleting {path} failed: {e.Message}");
            }
        }
    }
}
