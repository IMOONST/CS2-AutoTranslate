# Auto Translate — exclusions and translation memory / الاستثناءات وذاكرة الترجمة

## English

### Exclusions: what is never translated
Two places, merged:
- Options → Auto Translate → **Exclusions**: "Protected words and names" and "Excluded mods" (comma separated;
  applied 3 seconds after you stop typing).
- The file `ModsData/AutoTranslate/exclusions.json` in the game's user data folder
  (`%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II`). "Open exclusions file" creates it with the
  defaults and opens it; changes are applied automatically when you save it (UTF-8).

```json
{
  "terms": [ "Al Qassim", "Sign Builder" ],
  "texts": [ "Exact text to leave alone" ],
  "mods": [ "SignBuilderSA", "SignBuilder", "ArabicStreetNames", "AutoTranslate" ]
}
```
- `terms`: words or phrases kept **unchanged inside** translated texts. "Welcome to Al Qassim region" becomes
  "مرحباً بك في منطقة Al Qassim". Matching ignores case and spacing and only takes whole words ("Al Qassim" does not
  match "Al Qassimi"). A text that is only a term (plus punctuation) is left as it is.
- `texts`: whole texts that are never translated (surrounding spaces and case are ignored).
- `mods`: mods whose texts are never translated. Use the assembly name, the file name or the options page title
  (case, spaces, `-`, `_` and `.` are ignored). "Export mod list" writes `ModsData/AutoTranslate/mods.json` with the
  names of every mod that has texts, their number of texts and whether they are excluded. Translations Auto Translate
  had added for an excluded mod are removed from the game right away.

Notes:
- Changing the protected terms forgets the saved translations whose source contains a term that was added or removed
  (they may have translated it, or kept it); those texts are translated again, everything else stays saved.
- Mod interfaces (text a mod draws without the game's localization) cannot be traced to a mod. For them, an excluded
  mod only covers texts equal to one of that mod's localization texts; excluded texts and protected terms work as usual.
- Some mods register their texts through a game class; Auto Translate then recognises the mod by the names in its keys.
  Sources that cannot be attributed are listed as "unattributed" under the name of their class's assembly.
- Exclusions win over override files for excluded mods (an excluded mod is not touched at all); for excluded texts, an
  override by key (`keys`) still applies, since it names that exact entry.

### Translation memory
Every machine translation is saved permanently in `ModsData/AutoTranslate/Cache/<language>.json`, a plain JSON object
`{ "source text": "translation" }`. A saved text is never sent to an engine again.
- At start the whole file is read in the background; nothing is sent to an engine before it is completely loaded.
- New translations reach the disk within about 2 seconds, and everything is written when the game closes.
- Saves are atomic (a temporary file replaces the old one); the previous file is kept as `.bak`. A damaged file is
  renamed `.broken` and the backup is used instead.
- "Clear cache" deletes the memory of every language (after a confirmation). The status shows "Saved translations".

---

## العربية

### الاستثناءات: ما لا يُترجم أبداً
مكانان يُدمجان معاً:
- الإعدادات ← مترجم المودات التلقائي ← **الاستثناءات**: «كلمات وأسماء محمية» و«مودات مستثناة» (تفصل بينها فواصل؛
  تُطبَّق بعد 3 ثوانٍ من توقفك عن الكتابة).
- الملف `ModsData/AutoTranslate/exclusions.json` داخل مجلد بيانات اللعبة
  (`%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II`). زر «فتح ملف الاستثناءات» ينشئه بالقيم
  الافتراضية ويفتحه؛ وتُطبَّق التعديلات تلقائياً عند حفظه (بترميز UTF-8).

```json
{
  "terms": [ "Al Qassim", "Sign Builder" ],
  "texts": [ "Exact text to leave alone" ],
  "mods": [ "SignBuilderSA", "SignBuilder", "ArabicStreetNames", "AutoTranslate" ]
}
```
- `terms`: كلمات أو عبارات تبقى **كما هي داخل** النصوص المترجمة. فالنص "Welcome to Al Qassim region" يصبح
  "مرحباً بك في منطقة Al Qassim". المطابقة لا تهتم بحجم الأحرف ولا بالمسافات، وتأخذ الكلمات الكاملة فقط
  ("Al Qassim" لا تطابق "Al Qassimi"). النص المكوَّن من مصطلح فقط (مع علامات ترقيم) يبقى كما هو.
- `texts`: نصوص كاملة لا تُترجم أبداً (المسافات في الطرفين وحجم الأحرف لا تُحتسب).
- `mods`: مودات لا تُترجم نصوصها أبداً. اكتب اسم المكتبة أو اسم الملف أو عنوان صفحة إعدادات المود
  (لا يهم حجم الأحرف ولا المسافات ولا `-` و`_` و`.`). زر «تصدير قائمة المودات» يكتب الملف
  `ModsData/AutoTranslate/mods.json` بأسماء كل المودات التي فيها نصوص وعدد نصوصها وهل هي مستثناة. والترجمات التي
  أضافها المترجم التلقائي لمود مستثنى تُزال من اللعبة فوراً.

ملاحظات:
- تغيير المصطلحات المحمية يحذف من الذاكرة الترجمات التي يحتوي نصها الأصلي على مصطلح أُضيف أو أُزيل (فقد تكون ترجمته
  أو أبقته)؛ فتُترجم تلك النصوص من جديد، ويبقى كل ما عداها محفوظاً.
- واجهات المودات (النصوص التي يرسمها المود دون نظام الترجمة في اللعبة) لا يمكن معرفة المود الذي رسمها. لذلك يشمل
  استثناء المود فيها النصوص المساوية لأحد نصوص ترجمة ذلك المود فقط؛ أما النصوص المستثناة والمصطلحات المحمية فتعمل كالمعتاد.
- بعض المودات تسجّل نصوصها عبر صنف من أصناف اللعبة؛ فيتعرّف المترجم التلقائي على المود من الأسماء الموجودة في مفاتيحه.
  والمصادر التي لا يمكن نسبتها إلى مود تظهر بوصف "unattributed" باسم مكتبة صنفها.
- الاستثناء يتغلب على ملفات الترجمة اليدوية في المودات المستثناة (فالمود المستثنى لا يُمس إطلاقاً)؛ أما في النصوص
  المستثناة فالترجمة اليدوية بالمفتاح (`keys`) تبقى مطبَّقة لأنها تحدد ذلك المدخل بعينه.

### ذاكرة الترجمة
كل ترجمة آلية تُحفظ بشكل دائم في `ModsData/AutoTranslate/Cache/<اللغة>.json`، وهو كائن JSON بسيط
`{ "النص الأصلي": "الترجمة" }`. النص المحفوظ لا يُرسل إلى أي محرك مرة أخرى أبداً.
- عند التشغيل يُقرأ الملف كاملاً في الخلفية، ولا يُرسل أي نص إلى المحرك قبل اكتمال تحميله.
- تصل الترجمات الجديدة إلى القرص خلال ثانيتين تقريباً، ويُكتب كل شيء عند إغلاق اللعبة.
- الحفظ آمن (ملف مؤقت يحل محل القديم)، ويُحتفظ بالملف السابق باسم `.bak`. والملف التالف يُعاد تسميته إلى
  `.broken` وتُستخدم النسخة الاحتياطية بدلاً منه.
- زر «مسح الذاكرة المؤقتة» يحذف ذاكرة كل اللغات (بعد التأكيد). وتعرض الحالة «الترجمات المحفوظة».

## "panels" — windows switched off with the floating button / النوافذ الموقفة من الأيقونة العائمة

- **English:** the click mode of the floating button writes here. Each entry is `{ "id", "label", "mod", "texts" }`. When
  the window belongs to a recognised mod, `mod` holds its assembly name and the whole mod is excluded; otherwise only
  the window's `texts` are left as they are. Remove an entry (or use "Translate" in the button's menu, or click the window
  again in click mode) to translate it again.
- **العربية:** وضع التحكم بالضغط يكتب هنا. كل عنصر فيه `{ "id", "label", "mod", "texts" }`. لو النافذة لمود معروف،
  يكون `mod` اسم مكتبته ويُستثنى المود كاملاً؛ وإلا تبقى نصوص النافذة `texts` فقط بدون ترجمة. احذف العنصر (أو اضغط
  «ترجمة» في قائمة الأيقونة، أو اضغط على النافذة مرة ثانية في وضع التحكم بالضغط) لترجع ترجمتها.

### "language" in a panel / حقل "language"
- **English:** `""` = shown in its original text (excluded). A language code (for example `"fr"`, `"en"`, `"ar"`) = that
  mod or window is translated into this language instead of the main target. Set from the click mode's choice.
- **العربية:** `""` = يظهر بنصه الأصلي (مستثنى). رمز لغة (مثل `"fr"` أو `"en"` أو `"ar"`) = المود أو النافذة تُترجم لهذي
  اللغة بدل اللغة العامة. يتحدد من اختيارك في وضع التحكم بالضغط.
