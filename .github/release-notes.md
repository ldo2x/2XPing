> ⚠️ **نسخة تجريبية (Beta)** — ممكن تواجه أخطاء. بلّغ عنها من [Issues](../../issues).
> **Beta version** — you may hit bugs. Please report them in [Issues](../../issues).

### الجديد | What's new
- **شاشة رئيسية أبسط:** بطاقة وحدة فوق تقولك بكلام واضح وش يصير (مثبّت ولا لا، في مباراة، على منطقتك ولا لا)، وتحتها 2-3 مناطق بالبنق والانتظار وعدد اللاعبين | **A simpler main screen:** one card at the top says in plain words what's happening (locked or not, in a match, on your region or not), with 2-3 regions under it showing ping, wait and players
- **ديد لوك: تثبيت صارم يعرف الطريق:** يخلّي مداخل شبكة Valve اللي توديك لمناطقك بس مفتوحة، ويقفل اللي ممكن يدخّلك منطقة ما اخترتها، والبطاقة تقولك البنق المتوقع قبل ما تضغط («بنقك المتوقع تقريباً 58 عن طريق دبي») | **Deadlock: a strict, route-aware lock:** it keeps open only the Valve entry points that lead to your regions and closes the rest; the card shows the ping to expect before you press ("about 58 ms via Dubai")
- **دبي:** صارت من سيرفرات جنوب شرق آسيا، وفيه إعداد جديد **«دبي كطريق لأوروبا»** (شغّال من الأساس) لبنق أقل من الخليج، وتقدر تطفيه عشان ما تجيك مباراة في دبي أبداً | **Dubai:** it now counts as an SE Asia datacenter, and the new **"Dubai as a way to Europe"** setting (on by default) gives lower ping from the Gulf; turn it off to never get a match there
- **«وين ألعب؟» بصدق:** إذا اللعبة داخلة عن طريق ريلاي يقولك إن السيرفر ما ينشاف بدل ما يخمّن، ومع `-condebug` يقرا سجل ديد لوك ويسمّي السيرفر الحقيقي («تلعب دحين في أوروبا (فرانكفورت) ✓») | **Honest "where am I playing":** behind a Valve relay the card says the server can't be seen instead of guessing; with `-condebug` it reads Deadlock's console log and names the real datacenter
- **تقارير المباريات تنرسل لحالها:** بعد دقيقة من بداية كل مباراة يرسل تقرير قصير (التثبيت، البنق المتوقع والحقيقي، وين كانت المباراة). ما عليك تسوي شي، وكل اللي ينرسل مكتوب في [SECURITY.md](../../blob/main/SECURITY.md) | **Automatic match reports:** about a minute into each match a short report is sent (the lock, expected vs real ping, where the match was). Nothing for you to do; everything sent is listed in [SECURITY.md](../../blob/main/SECURITY.md)
- **أعداد لاعبين واقعية:** حوالي 49 ألف بدل ألفين، وتتغيّر حسب وقت اليوم | **Realistic player numbers:** about 49,000 instead of 2,000, following the time of day
- **أخف على جهازك والنت:** ما فيه فحص بنق تلقائي واللعبة شغّالة، ونص استهلاك النت وهو مفتوح بدون شغل | **Lighter on your PC and internet:** no automatic ping test while the game is open, and half the data use while idle
- **أأمن:** القفل يتشال دايماً لما تسكّر البرنامج، والتحديث يرجّع النسخة القديمة إذا صار أي خطأ | **Safer:** closing 2X Ping always removes the lock, and an update puts the old version back if anything fails
- **أوفرواتش 2 وديد باي دايلايت «قيد التطوير»:** شغّالين بس التثبيت فيهم لسا نطوّره | **Overwatch 2 and Dead by Daylight are "in development"**: they work, but their lock is still being improved

> اللي عندهم 0.10.x أو 0.11.x: التحديث بضغطة من الشريط اللي فوق. اللي عندهم 0.9.x: نزّلوا هالنسخة بيدكم مرة وحدة. | On 0.10.x or 0.11.x? Update with one click from the bar at the top. On 0.9.x? Download this one by hand once.

### التحميل والتشغيل | Download & run
1. نزّل ملف `2XPing-v...zip` من الأسفل وفك الضغط. | Download the `.zip` below and extract it.
2. شغّل `2XPing.exe` (يطلب صلاحية Admin). | Run `2XPing.exe` (needs administrator).
3. إذا طلع "Windows protected your PC" اضغط **More info** ثم **Run anyway** — البرنامج غير موقّع رقمياً لأنه مجاني. | If SmartScreen appears, click **More info → Run anyway** (the app is not code-signed yet).

يحتاج Windows 10 أو 11. | Requires Windows 10 or 11.

للتأكد من سلامة الملف قارن بصمة SHA256 مع ملف `.sha256`. | Verify the download with the `.sha256` file.

**كيف تشيله | Uninstall:** اضغط «فك التثبيت» أو «رجّع كل شي طبيعي»، قفّل البرنامج، وامسح مجلده (ولو تبغى امسح `%APPDATA%\2XPing`). | Press **Unlock** or **Back to normal**, close the app and delete its folder (optionally also `%APPDATA%\2XPing`).

**الخصوصية | Privacy:** البرنامج يرسل إحصائيات البيتا لقاعدة بيانات المطوّر، وتقدر تطفّي «شارك إحصائيات البيتا» من الإعدادات عشان يوصلنا استخدام مجهول بس. | 2X Ping sends beta statistics to the developer's database; turn off **Share beta statistics** in Settings to send only anonymous usage. [Privacy policy](../../blob/main/SECURITY.md#usage-statistics-beta)

### [Code signing policy](https://ldo2x.github.io/2XPing/code-signing.html)
Free code signing provided by SignPath.io, certificate by SignPath Foundation
- Committers and reviewers: [lDo2x](https://github.com/ldo2x) · Approvers: [lDo2x](https://github.com/ldo2x)
- Built from this tag's source by GitHub Actions and approved by hand before signing. | تنبني من كود هالإصدار عن طريق GitHub Actions وتنوافق يدوياً قبل التوقيع.
- Privacy: [SECURITY.md](../../blob/main/SECURITY.md#usage-statistics-beta)

التغييرات: [CHANGELOG.md](../../blob/main/CHANGELOG.md) | Changes: see CHANGELOG.md
