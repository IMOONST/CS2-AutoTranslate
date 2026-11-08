# Auto Translate: Any Mod

A Cities: Skylines II code mod that translates your mods, and any untranslated text on screen, into your language automatically. 43 languages, a permanent translation memory (a text is never translated twice), exclusions, and a floating button to switch translation off or on per mod window.

[العربية](#مترجم-المودات-التلقائي)

## Features
- Translates other mods' localization and the text their panels draw directly: by default only the mods you pick (click mode on a mod's window or options page), or every mod without your language ("What to translate": Everything).
- Its own interface (options page and floating button) in 13 languages: Auto (the game's), Windows', or a chosen one. Texts live in `AutoTranslate/i18n/<code>.json`, keyed by the English text.
- Translates any English text on screen that has no translation; the game's own translated texts are never touched.
- Target language: the game's, otherwise Windows', or one of 43 chosen manually.
- Engines: Free automatic (two free Google endpoints with failover and back-off), Google, DeepL, LibreTranslate, Microsoft Translator.
- Permanent translation memory (`ModsData/AutoTranslate/cache/<language>.json`): loaded at startup, saved within about 2 seconds, never translates a text twice.
- Floating button: drag anywhere; on/off; target language; click mode to show one mod translated, original, or in another language.
- Exclusions: protected words, whole texts, whole mods (`ModsData/AutoTranslate/exclusions.json`, the options page, or the click mode).
- Override files for hand-made or community translations; export of untranslated texts.

## How it works
| Area | Files |
|---|---|
| Localization | `Core/AutoTranslateSystem.cs` reads every mod's localization sources (`LocalizationManager.m_UserSources`), attributes them to mods (`Core/ModCatalog.cs`), and injects translations as one extra dictionary source (`Core/TranslatedSource.cs`). |
| Interface text | `UI/src/hook.ts` watches the page with a MutationObserver and swaps text nodes in place; `Interface/InterfaceTextSystem.cs` answers from the memory and queues the rest. Game texts are filtered out on the C# side. |
| Floating button | `UI/src/floating.tsx` and `Interface/FloatingControls.cs`. |
| Translation | `Core/TranslationService.cs` (batching, priority, rate-limit back-off), `Core/TranslationCache.cs` (memory), `Providers/` (engines; placeholders and protected terms are masked). |
| Exclusions | `Core/Exclusions.cs`, format in `Resources/EXCLUSIONS.md`. |

Privacy: only the texts to translate are sent to the chosen engine, never city, save or personal data.

## Building
Requirements: Cities: Skylines II with the official modding toolchain installed (it sets the `CSII_*` environment variables), .NET SDK, Node.js 18+.

```
cd AutoTranslate/UI
npm install
cd ..
dotnet build AutoTranslate.csproj -c Release
```

The build compiles the C# and the UI and deploys the mod to the game's local `Mods/AutoTranslate` folder. Close the game first.

## Contributing translations
Override files (`ModsData/AutoTranslate/Overrides/<language>/*.json`) always win over machine translation; see `AutoTranslate/Resources/Overrides/README.md`. Pull requests with fixes are welcome.

## License
MIT, see [LICENSE](LICENSE).

---

# مترجم المودات التلقائي

مود للعبة Cities: Skylines II يترجم المودات، وأي نص غير مترجم على الشاشة، إلى لغتك تلقائياً. 43 لغة، وذاكرة ترجمة دائمة (النص ما ينترجم مرتين)، واستثناءات، وأيقونة عائمة توقف الترجمة أو تشغّلها لكل نافذة مود.

## الميزات
- يترجم نصوص المودات ونوافذها: افتراضياً بس المودات اللي تختارها (وضع الضغط على نافذة المود أو صفحة إعداداته)، أو كل مود ما فيه لغتك («وش يترجم»: كل شيء).
- واجهته نفسها (صفحة الإعدادات والأيقونة) بـ 13 لغة: تلقائي حسب اللعبة، أو ويندوز، أو اختيار.
- يترجم أي نص إنجليزي على الشاشة ما له ترجمة، وما يلمس ترجمات اللعبة نفسها.
- اللغة: لغة اللعبة، وإلا لغة ويندوز، أو تختار من 43 لغة.
- محركات: مجاني تلقائي، Google، DeepL، LibreTranslate، Microsoft.
- ذاكرة ترجمة دائمة: تتحمّل عند التشغيل، وتنحفظ خلال ثانيتين تقريباً، وما يترجم نص مرتين.
- أيقونة عائمة: تسحبها لأي مكان، تشغيل وإيقاف، اختيار اللغة، ووضع التحكم بالضغط لعرض كل مود مترجم أو بالأصل أو بلغة ثانية.
- استثناءات: كلمات محمية، نصوص كاملة، مودات كاملة.
- ملفات ترجمة يدوية، وتصدير النصوص غير المترجمة.

الخصوصية: يُرسل للمحرك النص المطلوب ترجمته فقط، وليس مدينتك أو ملفات الحفظ أو بياناتك.

## البناء
تحتاج اللعبة مع أدوات التعديل الرسمية، و .NET SDK، و Node.js 18 أو أحدث:

```
cd AutoTranslate/UI
npm install
cd ..
dotnet build AutoTranslate.csproj -c Release
```

## المساهمة بالترجمات
ملفات الترجمة اليدوية تتغلب دائماً على الترجمة الآلية، وطلبات التصحيح مرحّب فيها.

## الرخصة
MIT، انظر [LICENSE](LICENSE).
