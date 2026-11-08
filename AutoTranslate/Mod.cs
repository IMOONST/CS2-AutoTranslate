using System;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;

namespace AutoTranslate
{
    /// <summary>
    /// Auto Translate: translates the texts of other mods that do not support the player's language — their
    /// localization entries (Core/) and text drawn directly by their interfaces (Interface/ + UI/) — with a local
    /// cache, override dictionaries and a choice of translation engines (Providers/).
    /// </summary>
    public sealed class Mod : IMod
    {
        public static readonly ILog Log = LogManager.GetLogger(nameof(AutoTranslate)).SetShowsErrorsInUI(false);

        public static Setting Settings { get; private set; }

        private static bool s_ExitHooked;

        public void OnLoad(UpdateSystem updateSystem)
        {
            Log.Info(nameof(OnLoad));
            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                Core.Paths.Initialize(System.IO.Path.GetDirectoryName(asset.path));
            }

            Settings = new Setting(this);
            Settings.RegisterInOptionsUI();
            Setting.RegisterLocales(Settings);
            AssetDatabase.global.LoadSettings(nameof(AutoTranslate), Settings, new Setting(this));
            Settings.Migrate();

            // The translation memory must reach the disk however the game ends. The game disposes mods when it quits
            // normally (GameManager → ModManager.Dispose → OnDispose); Application.quitting and ProcessExit cover the
            // paths where that does not happen. Saving twice is harmless (nothing is written when nothing changed).
            if (!s_ExitHooked)
            {
                s_ExitHooked = true;
                UnityEngine.Application.quitting += FlushTranslations;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            }

            // Scans mod localization, translates what the target language lacks and injects it.
            updateSystem.UpdateAt<Core.AutoTranslateSystem>(SystemUpdatePhase.MainLoop);
            // Translates text that mod interfaces draw without the localization system.
            updateSystem.UpdateAt<Interface.InterfaceTextSystem>(SystemUpdatePhase.UIUpdate);
        }

        public void OnDispose()
        {
            Log.Info(nameof(OnDispose));
            if (s_ExitHooked)
            {
                s_ExitHooked = false;
                try
                {
                    UnityEngine.Application.quitting -= FlushTranslations;
                    AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                }
                catch (Exception e)
                {
                    Log.Warn($"Removing the exit handlers failed: {e.Message}");
                }
            }
            // Saves every pending translation (blocking) and stops the engine.
            Core.TranslationService.Instance?.Dispose();
            if (Settings != null)
            {
                Settings.UnregisterInOptionsUI();
                Settings = null;
            }
        }

        private static void FlushTranslations()
        {
            try
            {
                Core.TranslationService.Instance?.FlushCaches();
            }
            catch (Exception e)
            {
                Log.Warn($"Saving the translations at exit failed: {e.Message}");
            }
        }

        private static void OnProcessExit(object sender, EventArgs e) => FlushTranslations();
    }
}
