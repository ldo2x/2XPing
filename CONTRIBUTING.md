<div dir="rtl">

# المساهمة

شكراً لاهتمامك بـ 2X Ping!

## كمختبر
- **مشكلة أو فكرة؟** اضغط **«تواصل معنا»** داخل البرنامج، اكتب رسالتك واضغط «أرسل». معلومات التشخيص تنرسل معها تلقائياً.
- أو افتح [بلاغ على GitHub](../../issues/new/choose).

## كمطوّر
1. سوّ Fork وفرع جديد.
2. ابنِ البرنامج: `powershell -ExecutionPolicy Bypass -File build.ps1`
3. الكود لازم يكون متوافق مع C# 5، فلا تستخدم `$""` أو `?.`.
4. أي نص يشوفه المستخدم يكون بالعربي والإنجليزي: `T("English", "عربي")`.
5. افتح Pull Request واشرح التغيير.

لا ترفع ملفات `.conf` حق WireGuard أبداً، لأن فيها مفاتيح سرية.

</div>

---

# Contributing

Thanks for helping 2X Ping!

## As a tester
- **Problem or idea?** Press **Contact us** inside the app, write your message and press Send. Diagnostic info is attached automatically.
- Or open an [issue on GitHub](../../issues/new/choose).

## As a developer
1. Fork and create a branch.
2. Build: `powershell -ExecutionPolicy Bypass -File build.ps1`
3. Keep the code C# 5 compatible: no `$""` or `?.`.
4. Every user-facing string is bilingual: `T("English", "عربي")`.
5. Open a pull request describing the change.

Never commit WireGuard `.conf` files: they contain private keys.

Releasing a version: follow [docs/RELEASING.md](docs/RELEASING.md).
