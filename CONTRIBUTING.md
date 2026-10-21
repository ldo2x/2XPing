# المساهمة | Contributing

شكراً لاهتمامك بـ 2X Ping! | Thanks for helping 2X Ping!

## كمختبر بيتا | As a beta tester
- **مشكلة؟** أسهل طريقة: زر **تواصل معنا** داخل البرنامج، اكتبها واضغط «أرسل». أو افتح [بلاغ](../../issues/new/choose) والصق فيه النص اللي يطلع لك من «وش ينرسل؟» في نفس النافذة.
- **فكرة أو لعبة جديدة؟** اكتبها في [Discussions](../../discussions).
- شاركنا نتائجك: لعبتك، منطقتك، البنق قبل وبعد.

- **Bug?** Easiest: the in-app **Contact us** button, write it and press Send. Or open an [issue](../../issues/new/choose) and paste the text from **What's sent?** in that window.
- **Idea or new game?** Post it in [Discussions](../../discussions).

## كمطوّر | As a developer
1. سوّ Fork وفرع جديد. | Fork and create a branch.
2. ابنِ البرنامج: `powershell -ExecutionPolicy Bypass -File build.ps1`. | Build it with the command above.
3. الكود متوافق مع C# 5 (المترجم المدمج في ويندوز)، فلا تستخدم ميزات أحدث مثل `$""` و`?.`. | Keep C# 5 compatible: no `$""` or `?.`.
4. أي نص يظهر للمستخدم يكون بالعربي والإنجليزي: `T("English", "عربي")`. | Every user-facing string is bilingual.
5. افتح Pull Request واشرح التغيير. | Open a pull request describing the change.

⚠️ لا ترفع أبداً ملفات `.conf` حق WireGuard، لأن فيها مفاتيح سرية. | Never commit WireGuard `.conf` files.

📦 نشر نسخة جديدة: اتبع [docs/RELEASING.md](docs/RELEASING.md). | Releasing a version: follow [docs/RELEASING.md](docs/RELEASING.md).
