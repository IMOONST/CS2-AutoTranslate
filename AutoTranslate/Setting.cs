using System;
using System.Collections.Generic;
using System.IO;
using AutoTranslate.Core;
using AutoTranslate.Providers;
using Colossal;
using Colossal.IO.AssetDatabase;
using Colossal.Json;
using Game.Modding;
using Game.SceneFlow;
using Game.Settings;
using Game.UI.Localization;
using Game.UI.Widgets;

namespace AutoTranslate
{
    /// <summary>Auto Translate's page in the game's Options (served in the interface language, see <see cref="OptionsLocalization"/>).</summary>
    [FileLocation(nameof(AutoTranslate))]
    [SettingsUIGroupOrder(kGeneralGroup, kEngineGroup, kExclusionsGroup, kMaintenanceGroup, kStatusGroup)]
    [SettingsUIShowGroupName(kGeneralGroup, kEngineGroup, kExclusionsGroup, kMaintenanceGroup, kStatusGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kGeneralGroup = "General";
        public const string kEngineGroup = "Engine";
        public const string kExclusionsGroup = "Exclusions";
        public const string kMaintenanceGroup = "Maintenance";
        public const string kStatusGroup = "Status";

        /// <summary>What is translated.</summary>
        public enum TranslateScope
        {
            /// <summary>Only the mods and windows the player picked (click mode); everything else keeps its text.</summary>
            ChosenOnly = 0,
            /// <summary>Every mod without the player's language (the mods and windows picked can still differ).</summary>
            Everything = 1,
        }

        /// <summary>How the target language is chosen.</summary>
        public enum TargetLanguageMode
        {
            /// <summary>The game's language when it is not English, else the Windows language when it is not English, else off.</summary>
            Auto = 0,
            /// <summary>Always the game's language (off when the game is in English).</summary>
            GameLanguage = 1,
            /// <summary>Always the Windows language (off when Windows is in English).</summary>
            SystemLanguage = 2,
            /// <summary>The language picked in <see cref="ManualLanguage"/>.</summary>
            Manual = 3,
        }

        public Setting(IMod mod) : base(mod)
        {
            SetDefaults();
        }

        // ------------------------------------------------------------------ General

        /// <summary>
        /// The master switch (first option of the page). Off: every injected translation is removed at once (the game
        /// shows the original texts), the engine stops and the interface hook restores the originals. On again: the
        /// translations come back from the saved translation memory, without translating anything twice.
        /// </summary>
        [SettingsUISection(kSection, kGeneralGroup)]
        public bool Enabled { get; set; }

        /// <summary>Only the mods picked with the click mode (default), or every mod.</summary>
        [SettingsUISection(kSection, kGeneralGroup)]
        public TranslateScope Scope { get; set; }

        [SettingsUISection(kSection, kGeneralGroup)]
        public TargetLanguageMode TargetMode { get; set; }

        /// <summary>Language code used when <see cref="TargetMode"/> is Manual ("ar", "de", "zh-Hans"…).</summary>
        [SettingsUIDropdown(typeof(Setting), nameof(GetLanguageItems))]
        [SettingsUIHideByCondition(typeof(Setting), nameof(IsManualLanguageHidden))]
        [SettingsUISection(kSection, kGeneralGroup)]
        public string ManualLanguage { get; set; }

        /// <summary>Also translate text that mod interfaces draw without the localization system (runtime UI hook).</summary>
        [SettingsUISection(kSection, kGeneralGroup)]
        public bool TranslateInterfaces { get; set; }

        /// <summary>The floating Auto Translate button while playing (drag it anywhere; click for its menu).</summary>
        [SettingsUISection(kSection, kGeneralGroup)]
        public bool ShowFloatingButton { get; set; }

        /// <summary>The language of this page and the floating button: "auto" (the game's), "windows", or a code of <see cref="UiLanguages.Codes"/>.</summary>
        [SettingsUIDropdown(typeof(Setting), nameof(GetInterfaceLanguageItems))]
        [SettingsUISection(kSection, kGeneralGroup)]
        public string InterfaceLanguage { get; set; }

        [SettingsUIButton]
        [SettingsUISection(kSection, kGeneralGroup)]
        public bool ResetButtonPosition
        {
            set
            {
                ButtonX = -1;
                ButtonY = -1;
                ShowFloatingButton = true;
                ApplyAndSave();
            }
        }

        /// <summary>Where the floating button was left, in screen pixels (-1: the default place).</summary>
        [SettingsUIHidden]
        public int ButtonX { get; set; }

        [SettingsUIHidden]
        public int ButtonY { get; set; }

        // ------------------------------------------------------------------ Engine

        [SettingsUISection(kSection, kEngineGroup)]
        public ProviderKind Provider { get; set; }

        /// <summary>Settings format, for one-time upgrades of saved settings (not shown).</summary>
        [SettingsUIHidden]
        public int SettingsRevision { get; set; }

        private const int kSettingsRevision = 2;

        /// <summary>
        /// Upgrades saved settings once: version 1 defaulted to Google's free endpoint, which blocks bulk use; players
        /// who kept that default move to the automatic free chain. A deliberate choice of another engine is kept.
        /// </summary>
        public void Migrate()
        {
            if (SettingsRevision >= kSettingsRevision) return;
            if (Provider == ProviderKind.GoogleFree) Provider = ProviderKind.FreeAuto;
            SettingsRevision = kSettingsRevision;
            ApplyAndSave();
        }

        [SettingsUITextInput]
        [SettingsUIHideByCondition(typeof(Setting), nameof(IsApiKeyHidden))]
        [SettingsUISection(kSection, kEngineGroup)]
        public string ApiKey { get; set; }

        [SettingsUITextInput]
        [SettingsUIHideByCondition(typeof(Setting), nameof(IsServiceUrlHidden))]
        [SettingsUISection(kSection, kEngineGroup)]
        public string ServiceUrl { get; set; }

        [SettingsUITextInput]
        [SettingsUIHideByCondition(typeof(Setting), nameof(IsRegionHidden))]
        [SettingsUISection(kSection, kEngineGroup)]
        public string Region { get; set; }

        // ------------------------------------------------------------------ Exclusions

        /// <summary>Words or phrases kept unchanged inside translated texts (comma separated; merged with exclusions.json "terms").</summary>
        [SettingsUITextInput]
        [SettingsUISection(kSection, kExclusionsGroup)]
        public string ExcludedTerms { get; set; }

        /// <summary>Mods never translated, by assembly name, file name or title (comma separated; merged with exclusions.json "mods").</summary>
        [SettingsUITextInput]
        [SettingsUISection(kSection, kExclusionsGroup)]
        public string ExcludedMods { get; set; }

        [SettingsUIButton]
        [SettingsUISection(kSection, kExclusionsGroup)]
        public bool OpenExclusionsFile
        {
            set => OpenExclusions();
        }

        [SettingsUIButton]
        [SettingsUISection(kSection, kExclusionsGroup)]
        public bool ExportModList
        {
            set => AutoTranslateSystem.RequestExportMods();
        }

        [Exclude]
        [SettingsUIValueVersion(typeof(Setting), nameof(GetStatusVersion))]
        [SettingsUISection(kSection, kExclusionsGroup)]
        public string StatusMods => DescribeMods();

        // ------------------------------------------------------------------ Maintenance

        [SettingsUIButton]
        [SettingsUIConfirmation]
        [SettingsUISection(kSection, kMaintenanceGroup)]
        public bool ClearCache
        {
            set => AutoTranslateSystem.RequestClearCache();
        }

        [SettingsUIButton]
        [SettingsUISection(kSection, kMaintenanceGroup)]
        public bool OpenOverridesFolder
        {
            set => OpenOverrides();
        }

        [SettingsUIButton]
        [SettingsUISection(kSection, kMaintenanceGroup)]
        public bool ExportUntranslated
        {
            set => AutoTranslateSystem.RequestExport();
        }

        [SettingsUIButton]
        [SettingsUISection(kSection, kMaintenanceGroup)]
        public bool RetranslateNow
        {
            set => AutoTranslateSystem.RequestRetranslate();
        }

        // ------------------------------------------------------------------ Status (read only)

        [Exclude]
        [SettingsUIValueVersion(typeof(Setting), nameof(GetStatusVersion))]
        [SettingsUISection(kSection, kStatusGroup)]
        public string StatusTarget => DescribeTarget();

        [Exclude]
        [SettingsUIValueVersion(typeof(Setting), nameof(GetStatusVersion))]
        [SettingsUISection(kSection, kStatusGroup)]
        public string StatusEngine => DescribeEngine();

        [Exclude]
        [SettingsUIValueVersion(typeof(Setting), nameof(GetStatusVersion))]
        [SettingsUISection(kSection, kStatusGroup)]
        public string StatusFound => AutoTranslateSystem.FoundCount.ToString();

        [Exclude]
        [SettingsUIValueVersion(typeof(Setting), nameof(GetStatusVersion))]
        [SettingsUISection(kSection, kStatusGroup)]
        public string StatusTranslated => AutoTranslateSystem.TranslatedCount.ToString();

        [Exclude]
        [SettingsUIValueVersion(typeof(Setting), nameof(GetStatusVersion))]
        [SettingsUISection(kSection, kStatusGroup)]
        public string StatusPending => AutoTranslateSystem.WaitingCount.ToString();

        [Exclude]
        [SettingsUIValueVersion(typeof(Setting), nameof(GetStatusVersion))]
        [SettingsUISection(kSection, kStatusGroup)]
        public string StatusFailed => AutoTranslateSystem.FailedCount.ToString();

        [Exclude]
        [SettingsUIValueVersion(typeof(Setting), nameof(GetStatusVersion))]
        [SettingsUISection(kSection, kStatusGroup)]
        public string StatusSaved => DescribeSaved();

        [Exclude]
        [SettingsUIValueVersion(typeof(Setting), nameof(GetStatusVersion))]
        [SettingsUISection(kSection, kStatusGroup)]
        public string StatusLastError
        {
            get
            {
                var error = TranslationService.Instance?.LastError;
                return string.IsNullOrEmpty(error) ? Text("None", "لا يوجد") : error;
            }
        }

        [Exclude]
        [SettingsUIValueVersion(typeof(Setting), nameof(GetStatusVersion))]
        [SettingsUISection(kSection, kStatusGroup)]
        public string StatusMessage
        {
            get
            {
                var message = AutoTranslateSystem.Message;
                return string.IsNullOrEmpty(message) ? "—" : message;
            }
        }

        /// <summary>The installed version (the ModVersion in PublishConfiguration.xml).</summary>
        [Exclude]
        [SettingsUISection(kSection, kStatusGroup)]
        public string Version => s_Version ?? (s_Version = typeof(Setting).Assembly.GetName().Version.ToString(3));

        private static string s_Version;

        // ------------------------------------------------------------------ option page helpers

        public int GetStatusVersion() => AutoTranslateSystem.StatusVersion * 16 + Array.IndexOf(UiLanguages.Codes, OptionsLocalization.Language()) + 1;

        public bool IsManualLanguageHidden() => TargetMode != TargetLanguageMode.Manual;

        private bool IsFreeEngine => Provider == ProviderKind.GoogleFree || Provider == ProviderKind.FreeAuto;
        public bool IsApiKeyHidden() => IsFreeEngine;

        public bool IsServiceUrlHidden() => IsFreeEngine;

        public bool IsRegionHidden() => Provider != ProviderKind.Microsoft;

        /// <summary>The languages of the Manual dropdown, by their own names.</summary>
        public DropdownItem<string>[] GetInterfaceLanguageItems()
        {
            var items = new List<DropdownItem<string>>(UiLanguages.Codes.Length + 2)
            {
                new DropdownItem<string> { value = "auto", displayName = LocalizedString.Value(Text("Auto (game language)", "تلقائي (لغة اللعبة)")) },
                new DropdownItem<string> { value = "windows", displayName = LocalizedString.Value(Text("Windows language", "لغة ويندوز")) },
            };
            for (int i = 0; i < UiLanguages.Codes.Length; i++)
                items.Add(new DropdownItem<string> { value = UiLanguages.Codes[i], displayName = LocalizedString.Value(UiLanguages.NativeNames[i]) });
            return items.ToArray();
        }

        public DropdownItem<string>[] GetLanguageItems()
        {
            var codes = LanguageCodes.Supported;
            var items = new List<DropdownItem<string>>(codes.Count + 1);
            bool hasCurrent = false;
            foreach (var code in codes)
            {
                if (string.Equals(code, ManualLanguage, StringComparison.OrdinalIgnoreCase)) hasCurrent = true;
                items.Add(new DropdownItem<string>
                {
                    value = code,
                    displayName = LocalizedString.Value(LanguageCodes.DisplayName(code) + " (" + code + ")"),
                });
            }
            if (!hasCurrent && !string.IsNullOrEmpty(ManualLanguage) && !LanguageCodes.IsBlocked(ManualLanguage))
            {
                // A code typed into the settings file by hand stays selectable.
                items.Add(new DropdownItem<string>
                {
                    value = ManualLanguage,
                    displayName = LocalizedString.Value(LanguageCodes.DisplayName(ManualLanguage) + " (" + ManualLanguage + ")"),
                });
            }
            return items.ToArray();
        }

        public override void SetDefaults()
        {
            Enabled = true;
            TargetMode = TargetLanguageMode.Auto;
            Scope = TranslateScope.ChosenOnly;
            ManualLanguage = "ar";
            InterfaceLanguage = "auto";
            TranslateInterfaces = true;
            ShowFloatingButton = true;
            ButtonX = -1;
            ButtonY = -1;
            Provider = ProviderKind.FreeAuto;
            SettingsRevision = kSettingsRevision;
            ApiKey = "";
            ServiceUrl = "";
            Region = "";
            ExcludedTerms = "";
            ExcludedMods = "";
        }

        public override void Apply()
        {
            base.Apply();
            AutoTranslateSystem.RequestApply();
        }

        /// <summary>Registers the options page text (Arabic or English) in every game locale.</summary>
        public static void RegisterLocales(Setting setting) => OptionsLocalization.Register(setting);

        /// <summary>
        /// <paramref name="en"/> in the interface language (<paramref name="ar"/> for Arabic, i18n/&lt;code&gt;.json for the
        /// others), with its {name} placeholders filled from <paramref name="pairs"/> (name, value, name, value…).
        /// </summary>
        internal static string Text(string en, string ar, params object[] pairs)
        {
            var language = OptionsLocalization.Language();
            var template = language == UiLanguages.Arabic ? ar : UiLanguages.Translate(language, en);
            return pairs.Length == 0 ? template : UiLanguages.Format(template, pairs);
        }

        public static void OpenFolder(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                UnityEngine.Application.OpenURL("file:///" + dir.Replace('\\', '/'));
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"Opening {dir} failed: {e.Message}");
            }
        }

        private void OpenOverrides()
        {
            var language = TranslationService.Instance?.TargetLanguage;
            if (string.IsNullOrEmpty(language)) language = string.IsNullOrEmpty(ManualLanguage) ? "ar" : ManualLanguage;
            try
            {
                Directory.CreateDirectory(Path.Combine(Paths.OverridesDir, Paths.SafeName(language)));
                // The format description, next to the player's files.
                var readme = Path.Combine(Paths.BuiltInOverridesDir, "README.md");
                var copy = Path.Combine(Paths.OverridesDir, "README.md");
                if (File.Exists(readme) && !File.Exists(copy)) File.Copy(readme, copy);
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"Preparing the overrides folder failed: {e.Message}");
            }
            OpenFolder(Paths.OverridesDir);
        }

        /// <summary>Creates exclusions.json with the defaults when missing, puts EXCLUSIONS.md next to it, and opens it.</summary>
        private static void OpenExclusions()
        {
            Exclusions.EnsureFile();
            try
            {
                var readme = Path.Combine(Paths.ModDir, "Resources", "EXCLUSIONS.md");
                var copy = Path.Combine(Paths.DataDir, "EXCLUSIONS.md");
                if (File.Exists(readme) && (!File.Exists(copy) || File.GetLastWriteTimeUtc(readme) > File.GetLastWriteTimeUtc(copy))) File.Copy(readme, copy, true);
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"Copying EXCLUSIONS.md failed: {e.Message}");
            }
            try
            {
                var path = Exclusions.FilePath;
                if (File.Exists(path)) UnityEngine.Application.OpenURL("file:///" + path.Replace('\\', '/'));
                else OpenFolder(Paths.DataDir);
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"Opening {Exclusions.FilePath} failed: {e.Message}");
            }
            // Changes made in the editor are picked up automatically (the file's time is watched); this reads it now.
            AutoTranslateSystem.RequestReloadExclusions();
        }

        private static string DescribeMods()
        {
            int mods = AutoTranslateSystem.ModsWithTexts;
            int excluded = AutoTranslateSystem.ExcludedMods;
            var exclusions = TranslationService.Instance?.CurrentExclusions;
            int terms = exclusions?.Terms.Count ?? 0;
            int texts = exclusions?.Texts.Count ?? 0;
            return Text(
                "Mods with translatable texts: {mods} ({excluded} excluded) — \"Export mod list\" shows their names. Protected terms: {terms}; excluded texts: {texts}.",
                "مودات فيها نصوص قابلة للترجمة: {mods} (المستثنى منها {excluded}) — «تصدير قائمة المودات» يعرض أسماءها. مصطلحات محمية: {terms}؛ نصوص مستثناة: {texts}.",
                "mods", mods, "excluded", excluded, "terms", terms, "texts", texts);
        }

        private static string DescribeSaved()
        {
            var service = TranslationService.Instance;
            if (service == null || !service.Enabled) return Text("— (translation is off)", "— (الترجمة متوقفة)");
            int count = service.SavedCount;
            if (count < 0) return Text("loading…", "جارٍ التحميل…");
            long bytes = service.SavedBytes;
            return Text("Saved translations: {count} ({size})", "الترجمات المحفوظة: {count} ({size})", "count", count, "size", FormatSize(bytes, OptionsLocalization.Language() == UiLanguages.Arabic));
        }

        private static string FormatSize(long bytes, bool arabic)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            if (bytes < 1024) return bytes.ToString(culture) + (arabic ? " بايت" : " B");
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#", culture) + (arabic ? " كيلوبايت" : " KB");
            return (bytes / (1024.0 * 1024.0)).ToString("0.##", culture) + (arabic ? " ميغابايت" : " MB");
        }

        private static string DescribeTarget()
        {
            var service = TranslationService.Instance;
            if (service != null && service.Enabled)
            {
                var code = service.TargetLanguage;
                var mode = Mod.Settings?.TargetMode ?? TargetLanguageMode.Auto;
                string how;
                switch (mode)
                {
                    case TargetLanguageMode.GameLanguage: how = Text("game language", "لغة اللعبة"); break;
                    case TargetLanguageMode.SystemLanguage: how = Text("Windows language", "لغة ويندوز"); break;
                    case TargetLanguageMode.Manual: how = Text("chosen", "مختارة"); break;
                    default:
                        how = TextRules.SameLanguage(TargetResolver.GameLanguage(), code)
                            ? Text("automatic: game language", "تلقائي: لغة اللعبة")
                            : Text("automatic: Windows language", "تلقائي: لغة ويندوز");
                        break;
                }
                return LanguageCodes.DisplayName(code) + " (" + code + ") — " + how;
            }
            switch (AutoTranslateSystem.CurrentOffReason)
            {
                case OffReason.AllEnglish:
                    return Text("Off: the game and Windows are in English, so there is nothing to translate. Choose a language under \"Target language\" to translate anyway.",
                        "متوقف: اللعبة وويندوز بالإنجليزية فلا يوجد ما يُترجم. اختر لغة من «اللغة الهدف» للترجمة على أي حال.");
                case OffReason.GameEnglish:
                    return Text("Off: the game is in English.", "متوقف: اللعبة بالإنجليزية.");
                case OffReason.SystemEnglish:
                    return Text("Off: Windows is in English (or its language is unknown).", "متوقف: ويندوز بالإنجليزية (أو لغته غير معروفة).");
                case OffReason.NoLanguage:
                    return Text("Off: choose a language other than English.", "متوقف: اختر لغة غير الإنجليزية.");
                default:
                    return Text("Off.", "متوقف.");
            }
        }

        private static string DescribeEngine()
        {
            var service = TranslationService.Instance;
            var kind = Mod.Settings?.Provider ?? ProviderKind.FreeAuto;
            var name = ProviderName(kind);
            if (service == null || !service.Enabled) return name;
            if (service.EngineUnavailable)
                return name + " — " + Text("not available (see last error); only override files and the cache are used",
                    "غير متاح (انظر آخر خطأ)؛ تُستخدم ملفات الترجمة اليدوية والذاكرة المؤقتة فقط");
            if (service.Paused)
                return name + " — " + Text("paused (the service refused requests; retrying with growing waits)", "متوقف مؤقتاً (الخدمة رفضت الطلبات؛ يعيد المحاولة بفترات انتظار متزايدة)");
            int pending = service.PendingCount;
            if (pending > 0)
                return name + " — " + Text("translating ({pending} in queue)", "يترجم ({pending} في الانتظار)", "pending", pending);
            return name + " — " + Text("ready ({count} translated this session)", "جاهز ({count} تُرجمت في هذه الجلسة)", "count", service.TranslatedThisSession);
        }

        private static string ProviderName(ProviderKind kind)
        {
            switch (kind)
            {
                case ProviderKind.DeepL: return "DeepL";
                case ProviderKind.LibreTranslate: return "LibreTranslate";
                case ProviderKind.Microsoft: return "Microsoft Translator";
                case ProviderKind.FreeAuto: return Text("Free, automatic (two Google endpoints)", "مجاني تلقائي (منفذان من Google)");
                default: return Text("Google Translate (free)", "ترجمة Google (مجانية)");
            }
        }
    }

    /// <summary>The options page text: (id, English, Arabic).</summary>
    internal static class OptionsText
    {
        public static Dictionary<string, string> Build(Setting s, string language)
        {
            var d = new Dictionary<string, string>();
            bool arabic = language == UiLanguages.Arabic;
            void Add(string id, string en, string ar) => d[id] = arabic ? ar : UiLanguages.Translate(language, en);
            void Option(string name, string labelEn, string labelAr, string descEn, string descAr)
            {
                Add(s.GetOptionLabelLocaleID(name), labelEn, labelAr);
                Add(s.GetOptionDescLocaleID(name), descEn, descAr);
            }

            Add(s.GetSettingsLocaleID(), "Auto Translate: Any Mod", "مترجم المودات التلقائي");
            Add(s.GetOptionTabLocaleID(Setting.kSection), "Main", "الرئيسية");
            Add(s.GetOptionGroupLocaleID(Setting.kGeneralGroup), "General", "عام");
            Add(s.GetOptionGroupLocaleID(Setting.kEngineGroup), "Translation engine", "محرك الترجمة");
            Add(s.GetOptionGroupLocaleID(Setting.kMaintenanceGroup), "Maintenance", "الصيانة");
            Add(s.GetOptionGroupLocaleID(Setting.kStatusGroup), "Status", "الحالة");

            Add(s.GetOptionGroupLocaleID(Setting.kExclusionsGroup), "Exclusions", "الاستثناءات");

            Option(nameof(Setting.Enabled), "Enable Auto Translate", "تفعيل المترجم التلقائي",
                "Translates the texts of other mods that do not support your language. Turning it off restores every original text at once and stops all network work; " +
                "turning it on again brings the translations back from the saved translation memory, without translating anything again.",
                "يترجم نصوص المودات الأخرى التي لا تدعم لغتك. إيقافه يعيد كل النصوص الأصلية فوراً ويوقف أي اتصال بالإنترنت؛ " +
                "وتشغيله من جديد يعيد الترجمات من ذاكرة الترجمة المحفوظة دون ترجمة أي شيء مرة أخرى.");

            Option(nameof(Setting.ExcludedTerms), "Protected words and names", "كلمات وأسماء محمية",
                "Words or phrases that are never translated, separated by commas (for example: Al Qassim, Sign Builder). They stay exactly as written inside translated texts, " +
                "and a text made only of one of them is left as it is. Applied 3 seconds after you stop typing. More terms, whole texts to leave alone and mods can be listed in exclusions.json.",
                "كلمات أو عبارات لا تُترجم أبداً، تفصل بينها فواصل (مثال: Al Qassim, Sign Builder). تبقى كما كُتبت تماماً داخل النصوص المترجمة، " +
                "والنص المكوَّن من إحداها فقط يبقى كما هو. يُطبَّق بعد 3 ثوانٍ من توقفك عن الكتابة. يمكن إضافة مصطلحات أخرى ونصوص كاملة لا تُترجم ومودات في الملف exclusions.json.");
            Option(nameof(Setting.ExcludedMods), "Excluded mods", "مودات مستثناة",
                "Mods whose texts are never translated, separated by commas: their assembly name, file name or options page title (see \"Export mod list\"); case and spaces do not matter. " +
                "Their translations are removed from the game at once. Merged with the \"mods\" list of exclusions.json (which excludes SignBuilderSA, SignBuilder and ArabicStreetNames by default).",
                "مودات لا تُترجم نصوصها أبداً، تفصل بينها فواصل: اسم المكتبة أو اسم الملف أو عنوان صفحة إعداداته (انظر «تصدير قائمة المودات»)؛ لا يهم حجم الأحرف ولا المسافات. " +
                "تُزال ترجماتها من اللعبة فوراً. تُدمج مع قائمة \"mods\" في exclusions.json (التي تستثني SignBuilderSA و SignBuilder و ArabicStreetNames افتراضياً).");
            Option(nameof(Setting.OpenExclusionsFile), "Open exclusions file", "فتح ملف الاستثناءات",
                "Opens ModsData/AutoTranslate/exclusions.json: \"terms\" (kept unchanged inside texts), \"texts\" (whole texts never translated) and \"mods\" (mods never translated). " +
                "Changes are applied automatically when the file is saved. EXCLUSIONS.md next to it explains the format.",
                "يفتح الملف ModsData/AutoTranslate/exclusions.json: \"terms\" (تبقى كما هي داخل النصوص)، و\"texts\" (نصوص كاملة لا تُترجم أبداً)، و\"mods\" (مودات لا تُترجم أبداً). " +
                "تُطبَّق التعديلات تلقائياً عند حفظ الملف. يشرح الملف EXCLUSIONS.md المجاور الصيغة.");
            Option(nameof(Setting.ExportModList), "Export mod list", "تصدير قائمة المودات",
                "Writes ModsData/AutoTranslate/mods.json: every mod with localization texts, with its assembly name, file name, title, number of texts and whether it is excluded, so you know the names to type.",
                "يكتب الملف ModsData/AutoTranslate/mods.json: كل مود فيه نصوص ترجمة، مع اسم مكتبته واسم ملفه وعنوانه وعدد نصوصه وهل هو مستثنى، لتعرف الأسماء التي تكتبها.");
            Option(nameof(Setting.StatusMods), "Mods found", "المودات المكتشفة",
                "Mods whose localization was found in the last scan, and how many are excluded.",
                "المودات التي وُجدت نصوص ترجمتها في آخر فحص، وعدد المستثنى منها.");
            Option(nameof(Setting.StatusSaved), "Translation memory", "ذاكرة الترجمة",
                "Machine translations saved permanently for the current language (and the size of the file). A saved text is never sent to the engine again; new translations are written to disk within about 2 seconds.",
                "الترجمات الآلية المحفوظة بشكل دائم للغة الحالية (وحجم الملف). النص المحفوظ لا يُرسل إلى المحرك مرة أخرى أبداً؛ وتُكتب الترجمات الجديدة على القرص خلال ثانيتين تقريباً.");

            Option(nameof(Setting.Scope), "What to translate", "وش يترجم",
                "Only the mods I choose: nothing is translated until you pick a mod with the floating button's click mode (click its window or its options page and choose a language); then all of that mod's texts are translated, its windows and its options. " +
                "Everything: every mod without your language is translated (the mods you pick can still be kept original or set to another language).",
                "بس المودات اللي أختارها: ما يُترجم شيء حتى تختار مود بوضع التحكم بالضغط في الأيقونة العائمة (اضغط على نافذته أو صفحة إعداداته واختر لغة)، وبعدها تُترجم كل نصوص هذا المود: نوافذه وإعداداته. " +
                "كل شيء: يُترجم كل مود ما فيه لغتك (والمودات اللي تختارها تقدر تبقيها بالأصل أو بلغة ثانية).");
            Add(s.GetEnumValueLocaleID(Setting.TranslateScope.ChosenOnly), "Only the mods I choose", "بس المودات اللي أختارها");
            Add(s.GetEnumValueLocaleID(Setting.TranslateScope.Everything), "Everything", "كل شيء");

            Option(nameof(Setting.TargetMode), "Target language", "اللغة الهدف",
                "Automatic: the game's language when it is not English; otherwise the Windows language when it is not English; otherwise nothing is translated. " +
                "You can also always follow the game, always follow Windows, or choose a language.",
                "تلقائي: لغة اللعبة إن لم تكن الإنجليزية، وإلا لغة ويندوز إن لم تكن الإنجليزية، وإلا لا يُترجم شيء. " +
                "يمكنك أيضاً اتباع لغة اللعبة دائماً، أو لغة ويندوز دائماً، أو اختيار لغة بنفسك.");
            Add(s.GetEnumValueLocaleID(Setting.TargetLanguageMode.Auto), "Automatic", "تلقائي");
            Add(s.GetEnumValueLocaleID(Setting.TargetLanguageMode.GameLanguage), "Game language", "لغة اللعبة");
            Add(s.GetEnumValueLocaleID(Setting.TargetLanguageMode.SystemLanguage), "Windows language", "لغة ويندوز");
            Add(s.GetEnumValueLocaleID(Setting.TargetLanguageMode.Manual), "Choose a language", "اختيار لغة");

            Option(nameof(Setting.ManualLanguage), "Language", "اللغة",
                "The language mod texts are translated into.",
                "اللغة التي تُترجم إليها نصوص المودات.");

            Option(nameof(Setting.TranslateInterfaces), "Translate mod interfaces", "ترجمة واجهات المودات",
                "Also translates text that mod panels and windows show directly, without the game's localization system. The game's own interface is never changed.",
                "يترجم أيضاً النصوص التي تعرضها لوحات ونوافذ المودات مباشرة دون نظام الترجمة في اللعبة. واجهة اللعبة نفسها لا تتغير أبداً.");

            Option(nameof(Setting.ShowFloatingButton), "Floating button", "الأيقونة العائمة",
                "Shows the Auto Translate button while you play. Drag it anywhere; click it for its menu: turn translation on or off, choose the language, " +
                "and the click mode, where clicking a mod's window lets you show it translated, in its original text, or in another language.",
                "يُظهر أيقونة المترجم أثناء اللعب. اسحبها لأي مكان، واضغط عليها لتظهر قائمتها: تشغيل الترجمة أو إيقافها، واختيار اللغة، " +
                "ووضع التحكم بالضغط: تضغط على نافذة أي مود وتختار كيف تظهر: مترجمة، أو بالأصل، أو بلغة ثانية.");
            Option(nameof(Setting.InterfaceLanguage), "Interface language", "لغة الواجهة",
                "The language of this page and the floating button (not of the translations). Auto follows the game's language, Windows language follows your computer, or pick one. A language the mod does not have shows in English.",
                "لغة هذه الصفحة والأيقونة العائمة (وليست لغة الترجمة). «تلقائي» يتبع لغة اللعبة، و«لغة ويندوز» يتبع لغة جهازك، أو اختر لغة بعينها. إذا لم تكن اللغة من لغات المود تظهر الإنجليزية.");
            Option(nameof(Setting.ResetButtonPosition), "Reset button position", "إرجاع الأيقونة لمكانها",
                "Puts the floating button back in its default place (and shows it if it was hidden).",
                "يعيد الأيقونة العائمة إلى مكانها الافتراضي (ويُظهرها إن كانت مخفية).");

            Option(nameof(Setting.Provider), "Engine", "المحرك",
                "Which service translates texts that are not in the cache or an override file. All engines are online services: the mod texts to translate " +
                "(never your city or personal data) are sent to the chosen service, each text only once thanks to the cache. " +
                "Google Translate (free) needs no key but is an unofficial endpoint that may limit or stop requests; DeepL and Microsoft need your own API key; " +
                "LibreTranslate can run on your own computer.",
                "الخدمة التي تترجم النصوص غير الموجودة في الذاكرة المؤقتة أو ملفات الترجمة اليدوية. كل المحركات خدمات عبر الإنترنت: تُرسل نصوص المودات المطلوب ترجمتها " +
                "(وليس مدينتك أو بياناتك الشخصية أبداً) إلى الخدمة المختارة، وكل نص مرة واحدة فقط بفضل الذاكرة المؤقتة. " +
                "ترجمة Google المجانية لا تحتاج مفتاحاً لكنها واجهة غير رسمية قد تحدّ الطلبات أو توقفها؛ DeepL و Microsoft تحتاجان مفتاح API خاصاً بك؛ " +
                "ويمكن تشغيل LibreTranslate على جهازك.");
            Add(s.GetEnumValueLocaleID(ProviderKind.FreeAuto), "Free, automatic (two Google endpoints) — recommended", "مجاني تلقائي (منفذان من Google) — موصى به");
            Add(s.GetEnumValueLocaleID(ProviderKind.GoogleFree), "Google Translate (free, no key)", "ترجمة Google (مجانية، بلا مفتاح)");
            Add(s.GetEnumValueLocaleID(ProviderKind.DeepL), "DeepL (API key)", "DeepL (مفتاح API)");
            Add(s.GetEnumValueLocaleID(ProviderKind.LibreTranslate), "LibreTranslate (server URL)", "LibreTranslate (عنوان الخادم)");
            Add(s.GetEnumValueLocaleID(ProviderKind.Microsoft), "Microsoft Translator (key and region)", "Microsoft Translator (مفتاح ومنطقة)");

            Option(nameof(Setting.ApiKey), "API key", "مفتاح API",
                "Your key for the chosen engine (DeepL: free keys end in \":fx\"; LibreTranslate: only if the server needs one). It is stored in plain text in the game's settings file.",
                "مفتاحك للمحرك المختار (DeepL: المفاتيح المجانية تنتهي بـ \":fx\"؛ LibreTranslate: فقط إن طلبه الخادم). يُحفظ كنص عادي في ملف إعدادات اللعبة.");
            Option(nameof(Setting.ServiceUrl), "Service URL", "عنوان الخدمة",
                "LibreTranslate: the server address (for example http://localhost:5000). Other engines: leave empty to use the official address.",
                "LibreTranslate: عنوان الخادم (مثل http://localhost:5000). المحركات الأخرى: اتركه فارغاً لاستخدام العنوان الرسمي.");
            Option(nameof(Setting.Region), "Azure region", "منطقة Azure",
                "The region of your Azure Translator resource (for example westeurope). Leave empty for a global resource.",
                "منطقة مورد Azure Translator الخاص بك (مثل westeurope). اتركه فارغاً للمورد العام.");

            Option(nameof(Setting.ClearCache), "Clear cache", "مسح الذاكرة المؤقتة",
                "Deletes every saved machine translation (all languages); mod texts are translated again by the engine.",
                "يحذف كل الترجمات الآلية المحفوظة (لكل اللغات)؛ وتُترجم نصوص المودات من جديد بالمحرك.");
            Add(s.GetOptionWarningLocaleID(nameof(Setting.ClearCache)),
                "Delete every saved translation? All texts will be sent to the engine again.",
                "حذف كل الترجمات المحفوظة؟ ستُرسل كل النصوص إلى المحرك من جديد.");
            Option(nameof(Setting.OpenOverridesFolder), "Open overrides folder", "فتح مجلد الترجمات اليدوية",
                "Your own translations, one folder per language (for example \"ar\"), JSON files that always win over machine translation. README.md explains the format.",
                "ترجماتك الخاصة، مجلد لكل لغة (مثل \"ar\")، ملفات JSON تتغلب دائماً على الترجمة الآلية. يشرح README.md الصيغة.");
            Option(nameof(Setting.ExportUntranslated), "Export untranslated texts", "تصدير النصوص غير المترجمة",
                "Writes the mod texts without a human translation to a JSON file in the override format (English as values), for community translators.",
                "يكتب نصوص المودات التي لا توجد لها ترجمة بشرية في ملف JSON بصيغة ملفات الترجمة اليدوية (القيم بالإنجليزية)، لمساعدة المترجمين من المجتمع.");
            Option(nameof(Setting.RetranslateNow), "Retranslate now", "إعادة الترجمة الآن",
                "Re-reads the override files, retries texts that failed and scans every mod again.",
                "يعيد قراءة ملفات الترجمة اليدوية، ويعيد محاولة النصوص التي فشلت، ويفحص كل المودات من جديد.");

            Option(nameof(Setting.StatusTarget), "Target language", "اللغة الهدف",
                "The language texts are translated into now, and why.", "اللغة التي تُترجم إليها النصوص الآن، وسبب اختيارها.");
            Option(nameof(Setting.StatusEngine), "Engine", "المحرك",
                "The engine in use and what it is doing.", "المحرك المستخدم وما يفعله.");
            Option(nameof(Setting.StatusFound), "Mod texts found", "نصوص المودات المكتشفة",
                "Texts found in the localization of your other mods.", "النصوص الموجودة في ملفات ترجمة موداتك الأخرى.");
            Option(nameof(Setting.StatusTranslated), "Translated", "المترجمة",
                "Mod texts shown in the target language (override files, the mod's own translation, or the engine).",
                "نصوص المودات المعروضة باللغة الهدف (ملفات الترجمة اليدوية، أو ترجمة المود نفسه، أو المحرك).");
            Option(nameof(Setting.StatusPending), "Waiting", "في الانتظار",
                "Mod texts waiting for the engine.", "نصوص المودات التي تنتظر المحرك.");
            Option(nameof(Setting.StatusFailed), "Failed", "فشلت",
                "Mod texts the engine could not translate after 3 attempts (retried with \"Retranslate now\" or at the next start).",
                "نصوص المودات التي لم يستطع المحرك ترجمتها بعد 3 محاولات (تُعاد مع «إعادة الترجمة الآن» أو عند التشغيل القادم).");
            Option(nameof(Setting.StatusLastError), "Last error", "آخر خطأ",
                "The last error reported by the engine.", "آخر خطأ أبلغ عنه المحرك.");
            Option(nameof(Setting.StatusMessage), "Last action", "آخر إجراء",
                "The result of the last maintenance action.", "نتيجة آخر إجراء صيانة.");
            Option(nameof(Setting.Version), "Version", "الإصدار",
                "The installed version of Auto Translate.", "الإصدار المثبت من مترجم المودات التلقائي.");
            return d;
        }
    }

    /// <summary>One source per game locale that serves the options page in the interface language, whatever the game's language.</summary>
    internal sealed class OptionsLocaleSource : IDictionarySource
    {
        private readonly Setting m_Setting;

        public OptionsLocaleSource(string localeId, Setting setting)
        {
            LocaleId = localeId;
            m_Setting = setting;
        }

        public string LocaleId { get; }

        /// <summary>The interface language of the last read; null before the first read.</summary>
        public string ServedLanguage { get; private set; }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            var language = OptionsLocalization.Language();
            ServedLanguage = language;
            return OptionsText.Build(m_Setting, language);
        }

        public void Unload()
        {
        }

        public override string ToString() => "AutoTranslate options (" + LocaleId + ")";
    }

    /// <summary>
    /// Registers the options page text and keeps it in the interface language (see <see cref="Language"/>).
    /// </summary>
    internal static class OptionsLocalization
    {
        private static readonly Dictionary<string, OptionsLocaleSource> s_Sources = new Dictionary<string, OptionsLocaleSource>();
        private static Setting s_Setting;
        private static string s_LastReloadAttempt;
        private static bool s_Reloading;

        /// <summary>The interface language now (a code of <see cref="UiLanguages.Codes"/>): the player's choice, Windows', or the game's.</summary>
        public static string Language()
        {
            var choice = (Mod.Settings?.InterfaceLanguage ?? "auto").Trim();
            if (string.Equals(choice, "windows", StringComparison.OrdinalIgnoreCase)) return UiLanguages.Windows();
            if (choice.Length > 0 && !string.Equals(choice, "auto", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var code in UiLanguages.Codes)
                    if (string.Equals(code, choice, StringComparison.OrdinalIgnoreCase)) return code;
            }
            return UiLanguages.Game();
        }

        public static bool IsArabic() => Language() == UiLanguages.Arabic;

        public static void Register(Setting setting)
        {
            s_Setting = setting;
            EnsureAllLocales();
        }

        /// <summary>Adds a source to locales that have none yet (locales added by other mods after startup).</summary>
        public static void EnsureAllLocales()
        {
            if (s_Setting == null) return;
            var locales = GameManager.instance?.localizationManager;
            if (locales == null) return;
            foreach (var locale in locales.GetSupportedLocales())
            {
                if (s_Sources.ContainsKey(locale)) continue;
                var source = new OptionsLocaleSource(locale, s_Setting);
                s_Sources[locale] = source;
                locales.AddSource(locale, source);
            }
        }

        /// <summary>Reloads the active locale when the options text there is in the other language (once per locale and language).</summary>
        public static void ReloadIfStale()
        {
            if (s_Setting == null || s_Reloading) return;
            var locales = GameManager.instance?.localizationManager;
            if (locales == null) return;
            string active;
            try { active = locales.activeLocaleId; }
            catch (Exception) { return; }
            if (string.IsNullOrEmpty(active)) return;
            if (!s_Sources.TryGetValue(active, out var source))
            {
                EnsureAllLocales();
                return;
            }
            var language = Language();
            if (source.ServedLanguage == language)
            {
                s_LastReloadAttempt = null;
                return;
            }
            var attempt = active + "|" + language;
            if (attempt == s_LastReloadAttempt) return;
            s_LastReloadAttempt = attempt;
            s_Reloading = true;
            try { locales.ReloadActiveLocale(); }
            catch (Exception e) { Mod.Log.Warn(e, "Reloading the options text failed"); }
            finally { s_Reloading = false; }
        }
    }
}
