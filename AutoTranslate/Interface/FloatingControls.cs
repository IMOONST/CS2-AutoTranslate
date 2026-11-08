using System;
using System.Collections.Generic;
using AutoTranslate.Core;
using Colossal.UI.Binding;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoTranslate.Interface
{
    /// <summary>
    /// The floating button (UI/src/floating.tsx): its state, and the click mode that switches the translation of one
    /// mod window off or on.
    /// <list type="bullet">
    /// <item>value <c>controls</c>: JSON <c>{ arabic, show, x, y, enabled, interfaces, target, translated, waiting,
    /// entries: [{ id, label, kind }] }</c>. Entries are everything switched off: panels and mods switched off with a
    /// click, and the mods of exclusions.json and of the options field.</item>
    /// <item>trigger <c>toggleEnabled</c>: the master switch of the options page.</item>
    /// <item>trigger <c>pick</c>(string): JSON <c>{ texts: [...], label }</c>, the texts (originals) of the window that was
    /// clicked. Answered in <c>pickResult</c>.</item>
    /// <item>trigger <c>restore</c>(string): an entry id; its translation comes back.</item>
    /// <item>trigger <c>moveButton</c>(string): JSON <c>{ x, y }</c>, where the button was dropped.</item>
    /// <item>trigger <c>hideButton</c>: hides the button (the options page shows it again).</item>
    /// <item>value <c>pickResult</c>: JSON <c>{ nonce, action: "off" | "on" | "none" | "error", label }</c>.</item>
    /// </list>
    /// A click decides, in this order: a window switched off before (same texts) is switched on again; a window of a
    /// recognised mod switches that mod off — or on again when it is excluded; a window of no known mod switches its
    /// own texts off. Everything is saved in exclusions.json ("panels", "mods"), so it lasts across games.
    /// </summary>
    public partial class InterfaceTextSystem
    {
        private const int MaxPickTexts = 400;
        private static readonly TimeSpan ControlsInterval = TimeSpan.FromMilliseconds(400);

        private ValueBinding<string> m_Controls;
        private ValueBinding<string> m_PickResult;
        private DateTime m_LastControls = DateTime.MinValue;
        private int m_PickNonce;

        private void CreateControls()
        {
            AddBinding(m_Controls = new ValueBinding<string>(Group, "controls", "{}"));
            AddBinding(m_PickResult = new ValueBinding<string>(Group, "pickResult", ""));
            AddBinding(new TriggerBinding(Group, "toggleEnabled", OnToggleEnabled));
            AddBinding(new TriggerBinding<string>(Group, "pick", OnPick));
            AddBinding(new TriggerBinding<string>(Group, "restore", OnRestore));
            AddBinding(new TriggerBinding<string>(Group, "moveButton", OnMoveButton));
            AddBinding(new TriggerBinding(Group, "hideButton", OnHideButton));
        }

        /// <summary>Rebuilds the state a few times per second; the binding only updates when it changed.</summary>
        private void UpdateControls(bool force = false)
        {
            var now = DateTime.UtcNow;
            if (!force && now - m_LastControls < ControlsInterval) return;
            m_LastControls = now;
            try
            {
                var json = BuildControls();
                if (!string.Equals(json, m_Controls.value, StringComparison.Ordinal)) m_Controls.Update(json);
            }
            catch (Exception e)
            {
                Mod.Log.Warn(e, "Auto Translate: building the floating button state failed");
            }
        }

        private string BuildControls()
        {
            var settings = Mod.Settings;
            var exclusions = m_Service?.CurrentExclusions ?? Exclusions.Empty;
            var entries = new JArray();
            var listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var panel in exclusions.Panels)
            {
                entries.Add(new JObject
                {
                    ["id"] = "panel:" + panel.Id,
                    ["label"] = panel.Label,
                    ["kind"] = string.IsNullOrEmpty(panel.Mod) ? "panel" : "mod",
                });
                if (!string.IsNullOrEmpty(panel.Mod)) listed.Add(Exclusions.ModKey(panel.Mod));
            }
            foreach (var mod in exclusions.FileMods)
            {
                if (!listed.Add(Exclusions.ModKey(mod))) continue;
                entries.Add(new JObject { ["id"] = "file:" + mod, ["label"] = mod, ["kind"] = "mod" });
            }
            foreach (var mod in exclusions.FieldMods)
            {
                if (!listed.Add(Exclusions.ModKey(mod))) continue;
                entries.Add(new JObject { ["id"] = "field:" + mod, ["label"] = mod, ["kind"] = "mod" });
            }

            var root = new JObject
            {
                ["arabic"] = OptionsLocalization.IsArabic(),
                ["show"] = settings == null || settings.ShowFloatingButton,
                ["x"] = settings?.ButtonX ?? -1,
                ["y"] = settings?.ButtonY ?? -1,
                ["enabled"] = settings != null && settings.Enabled,
                ["interfaces"] = settings != null && settings.TranslateInterfaces,
                ["target"] = m_Target ?? "",
                ["translated"] = AutoTranslateSystem.TranslatedCount,
                ["waiting"] = AutoTranslateSystem.WaitingCount,
                ["entries"] = entries,
            };
            return root.ToString(Formatting.None);
        }

        private void OnToggleEnabled()
        {
            var settings = Mod.Settings;
            if (settings == null) return;
            settings.Enabled = !settings.Enabled;
            settings.ApplyAndSave();
            UpdateControls(force: true);
        }

        private void OnHideButton()
        {
            var settings = Mod.Settings;
            if (settings == null) return;
            settings.ShowFloatingButton = false;
            settings.ApplyAndSave();
            UpdateControls(force: true);
        }

        private void OnMoveButton(string json)
        {
            var settings = Mod.Settings;
            if (settings == null || string.IsNullOrEmpty(json)) return;
            try
            {
                var obj = JObject.Parse(json);
                var x = (int)Math.Round((double?)obj["x"] ?? -1);
                var y = (int)Math.Round((double?)obj["y"] ?? -1);
                if (x == settings.ButtonX && y == settings.ButtonY) return;
                settings.ButtonX = Math.Max(-1, x);
                settings.ButtonY = Math.Max(-1, y);
                settings.ApplyAndSave();
                UpdateControls(force: true);
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"Auto Translate: malformed button position: {e.Message}");
            }
        }

        private void OnRestore(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            var ok = true;
            if (id.StartsWith("panel:", StringComparison.Ordinal))
            {
                ok = Exclusions.RemovePanel(id.Substring(6));
            }
            else if (id.StartsWith("file:", StringComparison.Ordinal))
            {
                ok = Exclusions.RemoveFileMod(id.Substring(5));
            }
            else if (id.StartsWith("field:", StringComparison.Ordinal))
            {
                RemoveFieldMod(id.Substring(6));
            }
            AutoTranslateSystem.RequestReloadExclusions();
            if (!ok) SendPickResult("error", "");
            UpdateControls(force: true);
        }

        /// <summary>Removes the entries naming these mods from the "Excluded mods" options field.</summary>
        private static bool RemoveFieldMod(params string[] names)
        {
            var settings = Mod.Settings;
            if (settings == null) return false;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in names)
            {
                var key = Exclusions.ModKey(name);
                if (key.Length > 0) keys.Add(key);
            }
            var kept = new List<string>();
            var removed = false;
            foreach (var entry in Exclusions.SplitField(settings.ExcludedMods))
            {
                if (keys.Contains(Exclusions.ModKey(entry))) removed = true;
                else kept.Add(entry);
            }
            if (!removed) return false;
            settings.ExcludedMods = string.Join(", ", kept);
            settings.ApplyAndSave();
            return true;
        }

        private void SendPickResult(string action, string label)
        {
            m_PickNonce++;
            var obj = new JObject { ["nonce"] = m_PickNonce, ["action"] = action, ["label"] = label ?? "" };
            m_PickResult.Update(obj.ToString(Formatting.None));
        }

        private void OnPick(string json)
        {
            try
            {
                Pick(json);
            }
            catch (Exception e)
            {
                Mod.Log.Warn(e, "Auto Translate: the click mode failed");
                SendPickResult("error", "");
            }
            UpdateControls(force: true);
        }

        private void Pick(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            var obj = JObject.Parse(json);
            var texts = new HashSet<string>(StringComparer.Ordinal);
            if (obj["texts"] is JArray array)
            {
                foreach (var token in array)
                {
                    if (texts.Count >= MaxPickTexts) break;
                    if (token.Type != JTokenType.String) continue;
                    var text = ((string)token).Trim();
                    if (text.Length >= 2 && text.Length <= MaxTextLength) texts.Add(text);
                }
            }
            var fallbackLabel = ((string)obj["label"] ?? "").Trim();
            if (texts.Count == 0)
            {
                SendPickResult("none", fallbackLabel);
                return;
            }

            var exclusions = m_Service?.CurrentExclusions ?? Exclusions.Empty;

            // 1. A window switched off before: its saved texts overlap what is shown now.
            var english = new Dictionary<string, string>(StringComparer.Ordinal);
            var owners = AutoTranslateSystem.FindOwners(texts, english);
            var compare = new HashSet<string>(texts, StringComparer.OrdinalIgnoreCase);
            foreach (var value in english.Values) compare.Add(value);

            PanelEntry bestPanel = null;
            var bestOverlap = 0;
            foreach (var panel in exclusions.Panels)
            {
                var overlap = 0;
                foreach (var text in panel.Texts)
                    if (compare.Contains(text)) overlap++;
                if (overlap == 0) continue;
                var smaller = Math.Min(compare.Count, panel.Texts.Count);
                if (overlap < 5 && overlap * 3 < smaller) continue;
                if (overlap > bestOverlap)
                {
                    bestOverlap = overlap;
                    bestPanel = panel;
                }
            }

            // The mod with the most texts in the window.
            ModEntry owner = null;
            var ownerVotes = 0;
            foreach (var pair in owners)
            {
                if (pair.Value > ownerVotes)
                {
                    owner = pair.Key;
                    ownerVotes = pair.Value;
                }
            }
            var ownerName = owner == null ? "" : !string.IsNullOrEmpty(owner.Title) ? owner.Title : !string.IsNullOrEmpty(owner.DisplayName) ? owner.DisplayName : owner.AssemblyName;

            if (bestPanel != null)
            {
                var ok = Exclusions.RemovePanel(bestPanel.Id);
                // A mod excluded both by this window and by a list: the click means "translate it again".
                if (!string.IsNullOrEmpty(bestPanel.Mod))
                {
                    Exclusions.RemoveFileMod(bestPanel.Mod);
                    RemoveFieldMod(bestPanel.Mod);
                }
                AutoTranslateSystem.RequestReloadExclusions();
                SendPickResult(ok ? "on" : "error", bestPanel.Label);
                return;
            }

            // 2. A recognised mod: excluded → translate it again; otherwise switch it off.
            if (owner != null)
            {
                if (exclusions.IsExcludedMod(owner.AssemblyName, owner.DisplayName, owner.Title))
                {
                    var names = new[] { owner.AssemblyName, owner.DisplayName, owner.Title };
                    var fileOk = Exclusions.RemoveFileMod(names);
                    RemoveFieldMod(names);
                    // Panels of this mod switched off earlier (with other texts) go too.
                    foreach (var panel in exclusions.Panels)
                        if (!string.IsNullOrEmpty(panel.Mod) && Exclusions.ModKey(panel.Mod) == Exclusions.ModKey(owner.AssemblyName))
                            Exclusions.RemovePanel(panel.Id);
                    AutoTranslateSystem.RequestReloadExclusions();
                    SendPickResult(fileOk ? "on" : "error", ownerName);
                    return;
                }

                var entry = NewPanel(ownerName, owner.AssemblyName);
                AddTexts(entry, texts, english, onlyCandidates: false);
                var added = Exclusions.AddPanel(entry);
                AutoTranslateSystem.RequestReloadExclusions();
                SendPickResult(added ? "off" : "error", ownerName);
                return;
            }

            // 3. No known mod: the window's own texts (the ones that would be translated).
            var own = NewPanel(fallbackLabel, "");
            AddTexts(own, texts, english, onlyCandidates: true);
            if (own.Texts.Count == 0)
            {
                // The game's own window, or nothing translatable in it.
                SendPickResult("none", fallbackLabel);
                return;
            }
            if (own.Label.Length == 0) own.Label = own.Texts[0];
            if (own.Label.Length > 40) own.Label = own.Label.Substring(0, 40) + "…";
            var saved = Exclusions.AddPanel(own);
            AutoTranslateSystem.RequestReloadExclusions();
            SendPickResult(saved ? "off" : "error", own.Label);
        }

        private static PanelEntry NewPanel(string label, string mod)
        {
            var label40 = (label ?? "").Trim();
            if (label40.Length > 40) label40 = label40.Substring(0, 40) + "…";
            return new PanelEntry
            {
                Id = DateTime.UtcNow.Ticks.ToString("x"),
                Label = label40,
                Mod = mod ?? "",
            };
        }

        private void AddTexts(PanelEntry entry, HashSet<string> texts, Dictionary<string, string> english, bool onlyCandidates)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var text in texts)
            {
                // The English text when the window shows our translation of a localization text.
                var value = english.TryGetValue(text, out var source) && !string.IsNullOrEmpty(source) ? source : text;
                if (onlyCandidates && (m_Service == null || !IsCandidate(value))) continue;
                if (seen.Add(value)) entry.Texts.Add(value);
            }
        }
    }
}
