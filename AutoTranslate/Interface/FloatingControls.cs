using System;
using System.Collections.Generic;
using AutoTranslate.Core;
using AutoTranslate.Providers;
using Colossal.UI.Binding;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoTranslate.Interface
{
    /// <summary>
    /// The floating button (UI/src/floating.tsx): its state, the target language, and the click mode that sets how one
    /// mod window is shown: with the main translation, in its original text, or translated into another language.
    /// <list type="bullet">
    /// <item>value <c>controls</c>: JSON <c>{ arabic, show, x, y, enabled, interfaces, mode, manual, target, targetName,
    /// translated, waiting, entries: [{ id, label, kind, language, languageName }] }</c>. Entries are every mod or window
    /// with its own setting, and the mods excluded in exclusions.json or the options field.</item>
    /// <item>value <c>languages</c>: JSON <c>[{ code, name }]</c>, every language that can be chosen (fixed).</item>
    /// <item>trigger <c>toggleEnabled</c>; <c>setTargetMode</c>(string: Auto, GameLanguage, SystemLanguage, Manual);
    /// <c>setManualLanguage</c>(string code); <c>moveButton</c>(string JSON { x, y }); <c>hideButton</c>.</item>
    /// <item>trigger <c>pick</c>(string JSON { texts, label }): the texts (originals) of the window clicked in click mode.
    /// Answered in <c>pickResult</c> with <c>{ nonce, action: "choose", token, label, kind, current }</c> (current:
    /// "default", "original" or a language code), or <c>action: "none"</c> for a game window.</item>
    /// <item>trigger <c>applyPick</c>(string JSON { token, choice }): choice "default", "original" or a language code.
    /// Answered with <c>{ action: "applied", label, choice }</c> or <c>{ action: "error" }</c>.</item>
    /// <item>trigger <c>restore</c>(string id): back to the main translation.</item>
    /// </list>
    /// A window is matched to its mod by its texts (<see cref="AutoTranslateSystem.FindOwners"/>): a recognised mod is
    /// set as a whole (its localization and its windows); a window of no known mod is set by its own texts. Saved in
    /// exclusions.json ("panels"; "language" "" means original), so it lasts across games.
    /// </summary>
    public partial class InterfaceTextSystem
    {
        private const int MaxPickTexts = 400;
        private static readonly TimeSpan ControlsInterval = TimeSpan.FromMilliseconds(400);

        private ValueBinding<string> m_Controls;
        private ValueBinding<string> m_PickResult;
        private DateTime m_LastControls = DateTime.MinValue;
        private int m_PickNonce;
        private PendingPick m_Pending;

        /// <summary>A window clicked in click mode, waiting for the player's choice.</summary>
        private sealed class PendingPick
        {
            public int Token;
            public string Label = "";
            public ModEntry Owner;
            public PanelEntry Panel;
            public readonly List<string> Texts = new List<string>();
        }

        private void CreateControls()
        {
            AddBinding(m_Controls = new ValueBinding<string>(Group, "controls", "{}"));
            AddBinding(m_PickResult = new ValueBinding<string>(Group, "pickResult", ""));
            AddBinding(new ValueBinding<string>(Group, "languages", BuildLanguages()));
            AddBinding(new TriggerBinding(Group, "toggleEnabled", OnToggleEnabled));
            AddBinding(new TriggerBinding<string>(Group, "setTargetMode", OnSetTargetMode));
            AddBinding(new TriggerBinding<string>(Group, "setManualLanguage", OnSetManualLanguage));
            AddBinding(new TriggerBinding<string>(Group, "pick", OnPick));
            AddBinding(new TriggerBinding<string>(Group, "applyPick", OnApplyPick));
            AddBinding(new TriggerBinding<string>(Group, "restore", OnRestore));
            AddBinding(new TriggerBinding<string>(Group, "moveButton", OnMoveButton));
            AddBinding(new TriggerBinding(Group, "hideButton", OnHideButton));
        }

        private static string BuildLanguages()
        {
            var list = new JArray();
            foreach (var code in LanguageCodes.Supported)
                list.Add(new JObject { ["code"] = code, ["name"] = LanguageCodes.DisplayName(code) });
            return list.ToString(Formatting.None);
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
                    ["language"] = panel.Language ?? "",
                    ["languageName"] = string.IsNullOrEmpty(panel.Language) ? "" : LanguageCodes.DisplayName(panel.Language),
                });
                if (!string.IsNullOrEmpty(panel.Mod)) listed.Add(Exclusions.ModKey(panel.Mod));
            }
            foreach (var mod in exclusions.FileMods)
            {
                if (!listed.Add(Exclusions.ModKey(mod))) continue;
                entries.Add(new JObject { ["id"] = "file:" + mod, ["label"] = mod, ["kind"] = "mod", ["language"] = "", ["languageName"] = "" });
            }
            foreach (var mod in exclusions.FieldMods)
            {
                if (!listed.Add(Exclusions.ModKey(mod))) continue;
                entries.Add(new JObject { ["id"] = "field:" + mod, ["label"] = mod, ["kind"] = "mod", ["language"] = "", ["languageName"] = "" });
            }

            var root = new JObject
            {
                ["arabic"] = OptionsLocalization.IsArabic(),
                ["lang"] = OptionsLocalization.Language(),
                ["show"] = settings == null || settings.ShowFloatingButton,
                ["x"] = settings?.ButtonX ?? -1,
                ["y"] = settings?.ButtonY ?? -1,
                ["enabled"] = settings != null && settings.Enabled,
                ["interfaces"] = settings != null && settings.TranslateInterfaces,
                ["mode"] = settings != null ? settings.TargetMode.ToString() : "Auto",
                ["manual"] = settings?.ManualLanguage ?? "",
                ["target"] = m_Target ?? "",
                ["targetName"] = string.IsNullOrEmpty(m_Target) ? "" : LanguageCodes.DisplayName(m_Target),
                ["translated"] = AutoTranslateSystem.TranslatedCount,
                ["waiting"] = AutoTranslateSystem.WaitingCount,
                ["entries"] = entries,
            };
            return root.ToString(Formatting.None);
        }

        // ------------------------------------------------------------------ settings from the menu

        private void OnToggleEnabled()
        {
            var settings = Mod.Settings;
            if (settings == null) return;
            settings.Enabled = !settings.Enabled;
            settings.ApplyAndSave();
            UpdateControls(force: true);
        }

        private void OnSetTargetMode(string mode)
        {
            var settings = Mod.Settings;
            if (settings == null || string.IsNullOrEmpty(mode)) return;
            if (!Enum.TryParse(mode, out Setting.TargetLanguageMode value) || !Enum.IsDefined(typeof(Setting.TargetLanguageMode), value)) return;
            if (settings.TargetMode == value) return;
            settings.TargetMode = value;
            settings.ApplyAndSave();
            UpdateControls(force: true);
        }

        private void OnSetManualLanguage(string code)
        {
            var settings = Mod.Settings;
            if (settings == null || string.IsNullOrEmpty(code) || !IsKnownLanguage(code)) return;
            settings.ManualLanguage = code;
            settings.TargetMode = Setting.TargetLanguageMode.Manual;
            settings.ApplyAndSave();
            UpdateControls(force: true);
        }

        private static bool IsKnownLanguage(string code)
        {
            foreach (var supported in LanguageCodes.Supported)
                if (string.Equals(supported, code, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
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

        // ------------------------------------------------------------------ restoring an entry

        private void OnRestore(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            var ok = true;
            if (id.StartsWith("panel:", StringComparison.Ordinal)) ok = Exclusions.RemovePanel(id.Substring(6));
            else if (id.StartsWith("file:", StringComparison.Ordinal)) ok = Exclusions.RemoveFileMod(id.Substring(5));
            else if (id.StartsWith("field:", StringComparison.Ordinal)) RemoveFieldMod(id.Substring(6));
            AutoTranslateSystem.RequestReloadExclusions();
            if (!ok) SendPickResult(new JObject { ["action"] = "error" });
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

        private void SendPickResult(JObject result)
        {
            m_PickNonce++;
            result["nonce"] = m_PickNonce;
            m_PickResult.Update(result.ToString(Formatting.None));
        }

        // ------------------------------------------------------------------ click mode

        private void OnPick(string json)
        {
            try
            {
                Identify(json);
            }
            catch (Exception e)
            {
                Mod.Log.Warn(e, "Auto Translate: the click mode failed");
                SendPickResult(new JObject { ["action"] = "error" });
            }
        }

        /// <summary>Finds what the clicked window is (a mod, a window set before, or neither) and asks the player.</summary>
        private void Identify(string json)
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
            var fallbackLabel = Shorten(((string)obj["label"] ?? "").Trim());
            if (texts.Count == 0)
            {
                SendPickResult(new JObject { ["action"] = "none", ["label"] = fallbackLabel });
                return;
            }

            var exclusions = m_Service?.CurrentExclusions ?? Exclusions.Empty;
            var english = new Dictionary<string, string>(StringComparer.Ordinal);
            var owners = AutoTranslateSystem.FindOwners(texts, english);
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
            // A mod's name is shown only when it clearly owns the window: two of its own texts at least, and a quarter
            // of the window's. A window drawn by a mod without localization (its texts are not in any mod's files) is
            // otherwise matched to whichever mod shares a word or two with it; it is handled as an unnamed window.
            if (owner != null && (ownerVotes < 2 || ownerVotes * 4 < texts.Count))
            {
                owner = null;
                english.Clear();
            }

            var pending = new PendingPick { Token = ++m_PickNonce, Owner = owner };
            // The texts to save: English when the window shows a translation of a localization text.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var text in texts)
            {
                var value = english.TryGetValue(text, out var source) && !string.IsNullOrEmpty(source) ? source : text;
                // A window of no known mod keeps only the texts that would be translated (not the game's own).
                if (owner == null && (m_Service == null || !IsCandidateOrSaved(value, exclusions))) continue;
                if (seen.Add(value)) pending.Texts.Add(value);
            }

            string current = "default";
            if (owner != null)
            {
                var ownerKey = Exclusions.ModKey(owner.AssemblyName);
                foreach (var panel in exclusions.Panels)
                {
                    if (!string.IsNullOrEmpty(panel.Mod) && Exclusions.ModKey(panel.Mod) == ownerKey)
                    {
                        pending.Panel = panel;
                        current = string.IsNullOrEmpty(panel.Language) ? "original" : panel.Language;
                        break;
                    }
                }
                if (pending.Panel == null && exclusions.IsExcludedMod(owner.AssemblyName, owner.DisplayName, owner.Title)) current = "original";
                pending.Label = !string.IsNullOrEmpty(owner.Title) ? owner.Title : !string.IsNullOrEmpty(owner.DisplayName) ? owner.DisplayName : owner.AssemblyName;
            }
            else
            {
                pending.Panel = BestPanel(exclusions, texts, english);
                if (pending.Panel != null)
                {
                    current = string.IsNullOrEmpty(pending.Panel.Language) ? "original" : pending.Panel.Language;
                    pending.Label = pending.Panel.Label;
                }
                else if (pending.Texts.Count == 0)
                {
                    // The game's own window, or nothing translatable in it.
                    SendPickResult(new JObject { ["action"] = "none", ["label"] = fallbackLabel });
                    return;
                }
                else
                {
                    pending.Label = fallbackLabel.Length > 0 ? fallbackLabel : Shorten(pending.Texts[0]);
                }
            }
            pending.Label = Shorten(pending.Label);

            m_Pending = pending;
            m_PickNonce--; // SendPickResult numbers the answer
            SendPickResult(new JObject
            {
                ["action"] = "choose",
                ["token"] = pending.Token,
                ["label"] = pending.Label,
                ["kind"] = owner != null ? "mod" : "panel",
                ["current"] = current,
            });
        }

        /// <summary>A text that would be translated, or one already saved for a window (it is excluded or set then).</summary>
        private bool IsCandidateOrSaved(string text, Exclusions exclusions)
        {
            if (exclusions.LanguageForText(text) != null || exclusions.IsExcludedText(text)) return true;
            return IsCandidate(text);
        }

        /// <summary>The saved window (of no known mod) whose texts overlap the clicked ones the most.</summary>
        private static PanelEntry BestPanel(Exclusions exclusions, HashSet<string> texts, Dictionary<string, string> english)
        {
            var compare = new HashSet<string>(texts, StringComparer.OrdinalIgnoreCase);
            foreach (var value in english.Values) compare.Add(value);
            PanelEntry best = null;
            var bestOverlap = 0;
            foreach (var panel in exclusions.Panels)
            {
                if (!string.IsNullOrEmpty(panel.Mod)) continue;
                var overlap = 0;
                foreach (var text in panel.Texts)
                    if (compare.Contains(text)) overlap++;
                if (overlap == 0) continue;
                var smaller = Math.Min(compare.Count, panel.Texts.Count);
                if (overlap < 5 && overlap * 3 < smaller) continue;
                if (overlap > bestOverlap)
                {
                    bestOverlap = overlap;
                    best = panel;
                }
            }
            return best;
        }

        private void OnApplyPick(string json)
        {
            try
            {
                var obj = JObject.Parse(json ?? "{}");
                var token = (int?)obj["token"] ?? -1;
                var choice = ((string)obj["choice"] ?? "").Trim();
                var pending = m_Pending;
                if (pending == null || pending.Token != token || choice.Length == 0) return;
                m_Pending = null;
                var ok = Apply(pending, choice);
                AutoTranslateSystem.RequestReloadExclusions();
                SendPickResult(ok
                    ? new JObject { ["action"] = "applied", ["label"] = pending.Label, ["choice"] = choice, ["languageName"] = ChoiceName(choice) }
                    : new JObject { ["action"] = "error" });
            }
            catch (Exception e)
            {
                Mod.Log.Warn(e, "Auto Translate: applying the click mode choice failed");
                SendPickResult(new JObject { ["action"] = "error" });
            }
            UpdateControls(force: true);
        }

        private static string ChoiceName(string choice) =>
            choice == "default" || choice == "original" ? "" : LanguageCodes.DisplayName(choice);

        /// <summary>Clears what was set for the window or its mod, then saves the new choice. False when the file could not be written.</summary>
        private bool Apply(PendingPick pending, string choice)
        {
            // The main target chosen as "another language" is the default.
            if (choice != "default" && choice != "original")
            {
                if (!IsKnownLanguage(choice)) return false;
                if (!string.IsNullOrEmpty(m_Target) && string.Equals(choice, m_Target, StringComparison.OrdinalIgnoreCase)) choice = "default";
            }

            var ok = true;
            var exclusions = m_Service?.CurrentExclusions ?? Exclusions.Empty;
            if (pending.Owner != null)
            {
                var owner = pending.Owner;
                var ownerKey = Exclusions.ModKey(owner.AssemblyName);
                foreach (var panel in exclusions.Panels)
                    if (!string.IsNullOrEmpty(panel.Mod) && Exclusions.ModKey(panel.Mod) == ownerKey)
                        ok &= Exclusions.RemovePanel(panel.Id);
                var names = new[] { owner.AssemblyName, owner.DisplayName, owner.Title };
                if (exclusions.IsExcludedMod(owner.AssemblyName, owner.DisplayName, owner.Title))
                {
                    Exclusions.RemoveFileMod(names);
                    RemoveFieldMod(names);
                }
            }
            else if (pending.Panel != null)
            {
                ok &= Exclusions.RemovePanel(pending.Panel.Id);
            }

            if (choice == "default") return ok;

            var entry = new PanelEntry
            {
                Id = DateTime.UtcNow.Ticks.ToString("x"),
                Label = pending.Label,
                Mod = pending.Owner?.AssemblyName ?? "",
                Language = choice == "original" ? "" : choice,
            };
            entry.Texts.AddRange(pending.Texts);
            if (entry.Texts.Count == 0 && entry.Mod.Length == 0) return ok;
            return Exclusions.AddPanel(entry) && ok;
        }

        private static string Shorten(string label)
        {
            label = (label ?? "").Trim();
            return label.Length > 40 ? label.Substring(0, 40) + "…" : label;
        }
    }
}
