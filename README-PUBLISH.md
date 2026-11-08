# نشر "Auto Translate: Any Mod" على Paradox Mods

> English summary at the end.

- المشروع: `AutoTranslate/AutoTranslate.csproj`.
- ملف النشر: `AutoTranslate/Properties/PublishConfiguration.xml`.
- الصورة المصغّرة: `AutoTranslate/Properties/Thumbnail.png` (512×512، الموجودة مسبقاً — أُبقيت كما هي).
- ملفات النشر (Publish Profiles) أُضيفت في `AutoTranslate/Properties/PublishProfiles/`.


## 1. لقطات الشاشة المطلوبة

| الملف | ماذا تُظهر |
|---|---|
| `AutoTranslate/Properties/Screenshot1.png` | إعدادات مود آخر (إنجليزي في الأصل) وقد ظهرت نصوصه بالعربية. يُفضّل صورة "قبل / بعد" جنباً إلى جنب. |
| `AutoTranslate/Properties/Screenshot2.png` | لوحة/واجهة مود آخر مترجمة (نصوص الواجهة)، مثل نافذة أداة من مود مشهور. |
| `AutoTranslate/Properties/Screenshot3.png` | صفحة إعدادات Auto Translate: زر التشغيل، اللغة، المحرك، ولوحة الحالة (عدد النصوص المترجمة من الذاكرة). |
| `AutoTranslate/Properties/Screenshot4.png` | الأيقونة العائمة وقائمتها مفتوحة، أو وضع التحكم بالضغط مع إطار برتقالي حول نافذة مود. |

نصائح:
- الدقة المقترحة **1920×1080** بصيغة PNG، وأقل من 2–3 ميجابايت تقريباً لكل صورة.
- اختر مودات معروفة لتظهر الفائدة، ولا تُظهر مفاتيح API في أي صورة.
- إن غيّرت عدد الصور فعدّل أسطر `<Screenshot>` في ملف النشر.

## 2. قبل النشر (قائمة التحقق)

- [ ] لا يوجد `ModId` في ملف النشر (أُزيل السطر الفارغ؛ المعرّف يُعطى عند أول نشر).
- [ ] `ModVersion` = `1.0.0` و `GameVersion` = `1.6.*`.
- [ ] `AccessLevel` = `Public` (أو `Unlisted` / `Private` / `Hidden` للتجربة).
- [ ] الميزات الجديدة تعمل: ملف `ModsData/AutoTranslate/cache/<lang>.json` يُنشأ ويُحمَّل عند التشغيل (جرّب بدون إنترنت)، زر الإيقاف يعيد النصوص الأصلية، والاستثناءات في `ModsData/AutoTranslate/exclusions.json` تعمل (Sign Builder: Saudi Edition و Arabic Street Names مستثناة افتراضياً).
- [ ] نص الخصوصية في الوصف مطابق للسلوك الفعلي (لا يُرسل إلا نصوص المودات).
- [ ] جرّب في **لعبة نظيفة** مع 2–3 مودات إنجليزية، وبلغة اللعبة العربية/غيرها، ثم مع محرك آخر.
- [ ] البناء بإعداد Release بدون أخطاء: `dotnet build AutoTranslate/AutoTranslate.csproj -c Release`.

## 3. تسجيل الدخول بحساب Paradox

أداة النشر تستخدم حساب Paradox المسجّل في اللعبة تلقائياً: شغّل اللعبة وسجّل الدخول مرة واحدة على الأقل ثم أغلقها. ويجب أن تكون أدوات التطوير مثبّتة من الإعدادات ← Modding.

## 4. أوامر النشر

```powershell
cd D:\CitiesSkylinesMegaMod\AutoTranslate\AutoTranslate

# أول نشر
dotnet publish AutoTranslate.csproj -c Release /p:PublishProfile=PublishNewMod

# إصدار جديد لاحقاً (ارفع ModVersion وحدّث ChangeLog)
dotnet publish AutoTranslate.csproj -c Release /p:PublishProfile=PublishNewVersion

# تحديث الوصف/الصور فقط
dotnet publish AutoTranslate.csproj -c Release /p:PublishProfile=UpdatePublishedConfiguration
```

بعد أول نشر ناجح تظهر رسالة `Mod published with Id=...` — تأكد أن `<ModId Value="الرقم" />` موجود في ملف النشر قبل الإصدارات اللاحقة.

---

## English summary

- Screenshots (1920×1080 PNG): `Properties/Screenshot1.png` another mod's options translated to Arabic (before/after); `Screenshot2.png` a translated mod panel; `Screenshot3.png` Auto Translate options with on/off, language, engine and status; `Screenshot4.png` the floating button with its menu open, or the click mode outlining a mod window. Never show API keys.
- The description includes the features being added now (permanent translation memory, on/off switch, exclusions) — publish only after they are finished and tested.
- Checklist: no ModId (the empty one was removed), ModVersion 1.0.0, GameVersion 1.6.*, AccessLevel, offline cache/exclusions verified, clean-game test.
- Log in to Paradox in the game once, then from `AutoTranslate/`: `dotnet publish AutoTranslate.csproj -c Release /p:PublishProfile=PublishNewMod` (first), `PublishNewVersion`, `UpdatePublishedConfiguration`.
