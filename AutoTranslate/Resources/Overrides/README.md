# Auto Translate — override dictionaries / ملفات الترجمة اليدوية

## English

Override dictionaries are hand-made translations. They **always win over machine translation** and their texts are
**never sent to a translation engine**.

### Where
- `Resources/Overrides/<language>/*.json` in the mod folder: community dictionaries shipped with the mod.
- `ModsData/AutoTranslate/Overrides/<language>/*.json` in the game's user data folder
  (`%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II`): **your own files**. They win over the shipped ones.
  Options → Auto Translate → "Open overrides folder" opens it.

`<language>` is the target language code: `ar`, `de`, `fr`, `es`, `tr`, `ru`, `zh-Hans`, `zh-Hant`, `pt-BR`, …
Files in a folder are read in name order; a later file wins over an earlier one.

### Format
```json
{
  "keys": {
    "Options.SECTION[SomeMod.SomeMod.Mod]": "Translation of that exact localization entry"
  },
  "texts": {
    "Reset to defaults": "Translation of this English text, wherever it appears"
  }
}
```
- `keys`: localization ids (the exact entry of a mod, or of the game itself). Most precise.
- `texts`: an English text → its translation, for every mod entry and mod interface text equal to it
  (surrounding spaces are ignored).
- Keep placeholders such as `{0}`, `{NAME}` and tags such as `<b>` exactly as they are.
- Save the files as UTF-8.

Changes take effect with Options → Auto Translate → "Retranslate now" (or at the next start).

### Helping translate
"Export untranslated texts" writes `ModsData/AutoTranslate/Export/untranslated-<language>-<date>.json` in this format,
with the English text as every value. Replace the values with translations, delete the lines you do not translate,
and put the file in your overrides folder — or share it so it can be shipped with the mod.

### Online engines and privacy
Texts without an override or a cached translation are sent to the engine chosen in the options (Google Translate,
DeepL, Microsoft Translator or a LibreTranslate server). Only mod texts are sent — never your city, saves or personal
data — and each text only once, thanks to the cache in `ModsData/AutoTranslate/Cache`. Machine translation can be
wrong; override files are how to fix it.

### Leaving things untranslated
Names that must stay as they are (inside translated texts), whole texts and whole mods can be excluded:
see `EXCLUSIONS.md` (in the mod's `Resources` folder, copied next to `exclusions.json` by "Open exclusions file").

---

## العربية

ملفات الترجمة اليدوية هي ترجمات يكتبها البشر. **تتغلب دائماً على الترجمة الآلية** ونصوصها **لا تُرسل أبداً إلى أي محرك ترجمة**.

### المكان
- `Resources/Overrides/<اللغة>/*.json` داخل مجلد المود: ملفات المجتمع المرفقة مع المود.
- `ModsData/AutoTranslate/Overrides/<اللغة>/*.json` داخل مجلد بيانات اللعبة
  (`%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II`): **ملفاتك الخاصة**، وهي تتغلب على الملفات المرفقة.
  زر «فتح مجلد الترجمات اليدوية» في الإعدادات ← مترجم المودات التلقائي يفتحه.

`<اللغة>` هو رمز اللغة الهدف: `ar` للعربية، و`de` و`fr` و`es` و`tr` و`ru` و`zh-Hans` و`zh-Hant` و`pt-BR` …
تُقرأ الملفات داخل المجلد بترتيب أسمائها؛ والملف اللاحق يتغلب على السابق.

### الصيغة
```json
{
  "keys": {
    "Options.SECTION[SomeMod.SomeMod.Mod]": "ترجمة هذا المدخل بالتحديد"
  },
  "texts": {
    "Reset to defaults": "استعادة الافتراضي"
  }
}
```
- `keys`: معرّفات الترجمة (مدخل محدد لمود، أو للعبة نفسها). وهي الأدق.
- `texts`: نص إنجليزي ← ترجمته، لكل مدخل مود أو نص واجهة مود يساويه (المسافات في الطرفين لا تُحتسب).
- احتفظ بالعناصر مثل `{0}` و`{NAME}` والوسوم مثل `<b>` كما هي تماماً.
- احفظ الملفات بترميز UTF-8.

تُطبَّق التغييرات بزر «إعادة الترجمة الآن» في الإعدادات (أو عند التشغيل القادم).

### المساهمة في الترجمة
زر «تصدير النصوص غير المترجمة» يكتب الملف `ModsData/AutoTranslate/Export/untranslated-<اللغة>-<التاريخ>.json`
بهذه الصيغة، وكل قيمة فيه هي النص الإنجليزي. استبدل القيم بالترجمة، واحذف الأسطر التي لا تترجمها، ثم ضع الملف في
مجلد ترجماتك اليدوية — أو شاركه ليُرفق مع المود.

### المحركات عبر الإنترنت والخصوصية
النصوص التي لا توجد لها ترجمة يدوية أو محفوظة تُرسل إلى المحرك المختار في الإعدادات (ترجمة Google أو DeepL أو
Microsoft Translator أو خادم LibreTranslate). تُرسل نصوص المودات فقط — وليس مدينتك أو ملفات الحفظ أو بياناتك الشخصية أبداً —
وكل نص مرة واحدة فقط بفضل الذاكرة المؤقتة في `ModsData/AutoTranslate/Cache`. الترجمة الآلية قد تخطئ، وملفات الترجمة
اليدوية هي طريقة تصحيحها.

### ترك أشياء دون ترجمة
يمكن استثناء أسماء تبقى كما هي (داخل النصوص المترجمة)، ونصوص كاملة، ومودات كاملة:
انظر `EXCLUSIONS.md` (في مجلد `Resources` الخاص بالمود، ويُنسخ بجوار `exclusions.json` عند الضغط على «فتح ملف الاستثناءات»).
