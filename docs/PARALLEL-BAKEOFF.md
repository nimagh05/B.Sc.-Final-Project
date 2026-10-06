# PARALLEL-BAKEOFF — اجرای هم‌زمان چند ایجنت و انتخاب بهترین نتیجه

> این فایل برای **خودت (انسان)** است، نه برای ایجنت‌ها. ایجنت‌ها نباید این فایل را اجرا کنند مگر اینکه صریحاً بگویی.

## 0. جواب کوتاه

بله، می‌شود Claude Code و OpenCode و Antigravity را روی یک مشخصات (همین کیت) هم‌زمان اجرا کرد **به شرط ایزوله بودن کامل**. تداخل‌ها از این هشت جا می‌آیند و پروتکل پایین هر کدام را می‌بندد:

1. پوشه‌ی کاری مشترک (ایجنت‌ها فایل‌های هم را خراب می‌کنند) → **هر ایجنت یک clone جدا**
2. گیت مشترک (قفل index، commit روی branch اشتباه) → **هر ایجنت branch خودش در clone خودش**
3. پورت‌ها (5432، 5000 …) → **پورت جدا برای هر کپی (`.env`)**
4. نام کانتینر/volume داکر → **`COMPOSE_PROJECT_NAME` جدا**
5. دیتابیس مشترک / migration های متضاد → **هر کپی دیتابیس خودش**
6. `user-secrets` (چون `UserSecretsId` در همه‌ی clone ها یکی است، secrets بین‌شان مشترک می‌شود) → **connection string و کلیدها فقط از متغیر محیطی/`.env` هر کپی**
7. Ollama و CPU (یک پردازشگر ضعیف و ۱۲ گیگ رم) → **Ollama مشترک است ولی تست‌ها با Fake کار می‌کنند؛ کار سنگین embedding فقط یکی‌یکی**
8. سهمیه‌ی API (Gemini رایگان، محدودیت مصرف خود ابزارها) → **تست‌ها هرگز API واقعی نمی‌زنند**

## 1. توصیه‌ی صادقانه (قبل از شروع)

- **سه ساخت کامل = سه برابر کار review.** تو باید کدی را که تحویل می‌دهی در دفاع توضیح بدهی. بررسی ۳ کدبیس کامل خیلی سنگین است.
- **پیشنهاد من: رقابت مرحله‌ای.** هر سه ایجنت فقط تا «گیت ۱» و «گیت ۲» (پایین) با هم رقابت کنند؛ برنده انتخاب شود؛ بعد از آن برنده سازنده‌ی اصلی است و دو ایجنت دیگر **مرور‌کننده و تست‌نویس** می‌شوند (کار ارزان‌تر و مفیدتر).
- **ابزار کمتر از مدل پشتش اهمیت دارد.** اگر OpenCode یا Antigravity با مدل ضعیف‌تر/رایگان کار کنند، مقایسه‌ی «ابزارها» در واقع مقایسه‌ی «مدل‌ها»ست. در گزارش بنویس هر کدام با چه مدلی اجرا شد.
- **مدل‌های سطح Flash در کارهای طولانی:** معمولاً در جزئیات (قرارداد اینترفیس‌ها، edge case های فارسی، هماهنگی بین فایل‌ها) بیشتر لغزش می‌کنند و ممکن است توکن زیادی مصرف کنند. تسک‌ها را کوچک نگه دار، `[PLAN-FIRST]` را جدی بگیر، و پلن آن‌ها را قبل از کدنویسی به یک ایجنت دیگر (مثلاً Claude Code) بده تا مرور کند. این فرض را با گیت‌ها بسنج، نه با حدس.
- **رم ۱۲ گیگ:** حداکثر ۲ ایجنت هم‌زمان فعال؛ هر کدام فقط کانتینر `db` خودش را بالا نگه دارد (نه کل استک). Testcontainers را وقتی اجرا کن که کار سنگین دیگری باز نیست.
- **سیاست دانشگاه:** از استاد راهنما بپرس استفاده از ایجنت‌های هوش مصنوعی برای نوشتن کد مجاز است و چطور باید در پایان‌نامه ذکر شود (ابزار، مدل، روش بررسی). شفاف‌بودن لازم است.
- **تقلب نکردن با تست:** ایجنت‌ها می‌توانند `conformance/` را بخوانند و برایش بهینه بنویسند. یک مجموعه‌ی کوچک **تست پنهان** (۵–۸ تست) نگه دار که در ریپو نیست و فقط در گیت‌ها اجرا می‌کنی.

## 2. پیش‌نیاز: کارهایی که فقط یک بار و مشترک انجام می‌شوند

این‌ها را **یک‌بار** روی ریپوی اصلی انجام بده و به `main` merge کن، نه سه بار:

- T00 (ریپو + کیت)، T01 (بنچمارک و ADR انتخاب extractor/embedding)، T01B (conformance + eval، بعد **فریز**)
- فایل‌های مشترک: `eval/questions.json`، PDF های تست (`tests/TestData`, `conformance/data`), `docs/adr/0002`

از این نقطه همه‌ی ایجنت‌ها از **یک `main` یکسان** شروع می‌کنند → مقایسه منصفانه است.

## 3. ساخت سه کپی ایزوله (PowerShell)

```powershell
cd F:\projects
git clone https://github.com/<username>/docsearch.git docsearch-claude
git clone https://github.com/<username>/docsearch.git docsearch-opencode
git clone https://github.com/<username>/docsearch.git docsearch-antigravity

cd F:\projects\docsearch-claude;       git checkout -b agent/claude
cd F:\projects\docsearch-opencode;     git checkout -b agent/opencode
cd F:\projects\docsearch-antigravity;  git checkout -b agent/antigravity
```
(هر ایجنت **فقط** داخل پوشه‌ی خودش اجرا شود؛ هیچ‌وقت دو ایجنت در یک پوشه نباشند.)

فایل `.env` هر کپی (از `.env.example` کپی کن و این مقادیر را عوض کن):

| | `COMPOSE_PROJECT_NAME` | `DB_PORT` | `API_PORT` | `WEB_PORT` | `EXTRACTOR_PORT` |
|---|---|---|---|---|---|
| claude | `ds_claude` | 5441 | 5101 | 5201 | 8101 |
| opencode | `ds_opencode` | 5442 | 5102 | 5202 | 8102 |
| antigravity | `ds_antigravity` | 5443 | 5103 | 5203 | 8103 |

هر کپی رمز دیتابیس و `Jwt__SigningKey` متفاوت داشته باشد. کلید Gemini را فقط در `.env` هر کپی بگذار (نه user-secrets).
ایجنت باید در T02 اسکریپت `scripts/load-env.ps1` را بسازد که `.env` را در سشن PowerShell به متغیر محیطی تبدیل می‌کند (connection string از `DB_PORT` ساخته می‌شود و `ASPNETCORE_URLS=http://localhost:$API_PORT`). قبل از `dotnet run`: `. .\scripts\load-env.ps1`.

## 4. راه‌اندازی هر ایجنت

- **Claude Code:** `claude` در پوشه‌ی `docsearch-claude` (فایل `CLAUDE.md` خودش `AGENTS.md` را import می‌کند).
- **OpenCode:** `opencode` در پوشه‌ی `docsearch-opencode` (`AGENTS.md` را می‌خواند).
- **Antigravity** (با مدل `gemini-3.8-flash` طبق گفته‌ی خودت): پوشه‌ی `docsearch-antigravity` را باز کن. تنظیم اجرای ترمینال را روی «قبل از اجرا تأیید بگیر» بگذار (نه حالت خودکار)، چون قاعده‌ی ۱۲ در AGENTS.md (بدون دستور مخرب) فقط با نظارت تو واقعاً رعایت می‌شود. مطمئن نیستم دقیقاً کدام فایل قواعد را خودکار می‌خواند؛ `GEMINI.md` و `AGENTS.md` هر دو آماده‌اند، ولی **در سشن اول چک کن** و اگر نخواند «Session start prompt» (AGENTS.md §4) را دستی paste کن.

پیام شروع یکسان برای هر سه (فقط شماره‌ی تسک عوض می‌شود): همان «Session start prompt».

**قاعده‌ی عدالت:** هر سه ایجنت **همان ترتیب تسک‌ها، همان prompt، همان تعداد دور رفع ایراد** را دارند. اگر به یکی کمک اضافه کردی، به بقیه هم بده یا در جدول یادداشت کن.

## 5. گیت‌ها (نقاط مقایسه)

| گیت | تسک‌ها | چه چیزی مقایسه می‌شود |
|---|---|---|
| **G1 — پایه** | T02–T06 | اسکلت، دیتابیس، normalizer، extractor، chunker |
| **G2 — RAG سرتاسری** | T07–T10 | آپلود تا پرسش و پاسخ با منبع، degraded، notFound، conformance 1–12 + 17–18 |
| **G3 — کیفیت و امنیت** | T11–T15 | hybrid، OCR، auth/ACL، hardening، reindex، conformance کامل |
| **G4 — محصول** | T16–T20 | CI، UI، بسته‌بندی، مستندات |

بین گیت‌ها هیچ merge ای به `main` نکن. در هر گیت: امتیازدهی → (در رقابت مرحله‌ای) حذف/نگه‌داشتن.

### فرمان‌های امتیازدهی خودکار (در پوشه‌ی هر کپی)
```powershell
dotnet build -warnaserror 2>&1 | Select-String "Warn|Error|Build succeeded"
dotnet test --filter "Category!=RequiresOllama" 2>&1 | Select-String "Passed!|Failed!"
dotnet format --verify-no-changes
dotnet list package --vulnerable --include-transitive
git grep -nI -E "AIza[0-9A-Za-z_-]{35}|sk-[A-Za-z0-9]{20,}|password\s*=\s*['\"][^'\"]+['\"]"   # نشت secret
(Get-ChildItem src -Recurse -Include *.cs | Get-Content | Measure-Object -Line).Lines            # حجم کد
git log --oneline | Measure-Object | Select-Object -ExpandProperty Count
# conformance (API با Fake provider ها بالا باشد)
$env:BASE_URL="http://localhost:<API_PORT>"; pytest conformance -m contract -v
```

### جدول امتیاز (۱۰۰)
| معیار | وزن | چطور سنجیده می‌شود |
|---|---|---|
| **Conformance** | 25 | درصد تست‌های contract پاس‌شده (+ تست‌های پنهان) |
| تست‌های خود پروژه | 10 | همه سبز؟ پوشش منطق مهم؟ تست‌های بی‌ارزش؟ |
| متریک‌های کیفیت RAG | 15 | Recall@5 و MRR روی split تست (از G2 به بعد) |
| معماری و کد | 15 | رعایت لایه‌ها/اینترفیس‌ها، بدون منطق در controller، خطاها، سادگی |
| امنیت | 10 | چک‌لیست `docs/CONVENTIONS.md` §3، تست‌های ACL، نبود secret |
| مستندات و ADR | 5 | README قابل اجرا از صفر، ADR های واقعی |
| سادگی و نگهداشت | 5 | warning ها، تعداد وابستگی، حجم کد، format |
| **قابل‌توضیح بودن** | 10 | قضاوت خودت: هر فایل را در ۲ دقیقه می‌توانی توضیح بدهی؟ |
| رفتار فرایندی | 5 | گزارش صادقانه، رعایت قواعد، بدون دور زدن تست |

**شرط‌های حذفی (در هر گیت):** build قرمز، تست قرمز، secret در git، تست نشت ACL قرمز، ایجنت تست را حذف/ضعیف کرده باشد.

بعد از هر گیت یک جدول ساده بساز (ردیف = معیار، ستون = ایجنت) و دلیل کوتاه هر امتیاز را بنویس؛ برای پایان‌نامه ارزشمند است ("مقایسه‌ی ایجنت‌های کدنویسی").

## 6. مرور متقابل (Cross-review) — ارزان و بسیار مؤثر

وقتی یک ایجنت سازنده است، ایجنت دیگر مرور می‌کند. prompt مرور:

```
You are a strict code reviewer. Read AGENTS.md and docs/SPEC-*.md.
Review the diff of branch <branch> against main for task <Txx>.
Check: spec conformance, hidden bugs, concurrency/async issues, security checklist (docs/CONVENTIONS.md §3),
missing tests, error handling, Persian-text edge cases, performance on CPU-only/12GB machine.
Do NOT edit code. Output a numbered list: severity (blocker/major/minor), file:line, problem, suggested fix.
Be concrete; if you are unsure, say so.
```
تو تصمیم می‌گیری کدام ایرادها وارد شوند؛ ایجنت سازنده فقط ایرادهای تأییدشده‌ی تو را رفع کند.

## 7. انتخاب برنده و تحویل

1. شاخه‌ی هر ایجنت را push کن: `git push -u origin agent/<name>`
2. برنده را با Pull Request به `main` در ریپوی اصلی merge کن.
3. شاخه‌های بازنده را با تگ آرشیو کن (`git tag archive/agent-opencode agent/opencode`) — برای فصل «مقایسه» لازم می‌شوند.
4. ماژول خوب بازنده‌ها را فقط با PR جدا + ADR + تست منتقل کن (نه کپی بی‌صدا).
5. از این‌جا به بعد یک ایجنت سازنده + ایجنت‌های مرورگر.

## 8. تقسیم کار ایمن اگر هم‌زمانی کامل می‌خواهی

اگر اصرار داری سه ایجنت روی **یک** پروژه‌ی واحد (نه سه نسخه) کار کنند: هر کدام را روی تسک‌های **مستقل و بدون فایل مشترک** بگذار (مثلاً A: T04 normalizer، B: T06 chunker، C: T05 extractor) هر کدام در clone و branch خودش، و فقط از طریق PR به `main` ادغام شوند. تسک‌هایی که `Program.cs`، DI یا migration ها را لمس می‌کنند را هم‌زمان نده (conflict تضمینی).

## 9. چک‌لیست قبل از شروع رقابت

- [ ] T00, T01, T01B روی `main` merge شده و conformance فریز است
- [ ] سه clone + سه `.env` با پورت‌های متفاوت
- [ ] هر ایجنت یک‌بار `AGENTS.md` را خوانده و در یک جمله خلاصه کرده (تست درک)
- [ ] مدل مورد استفاده‌ی هر ابزار یادداشت شده
- [ ] جدول امتیاز و تست‌های پنهان آماده است
- [ ] از استاد درباره‌ی سیاست استفاده از ابزارهای هوش مصنوعی پرسیده‌ای

## 10. تنظیم برای مدل‌های مختلف (Claude / Gemini Flash در Antigravity / مدل‌های رایگان OpenCode)

مدل‌ها از نظر توان خیلی فرق دارند و این روی «عادلانه بودن» و «کیفیت نهایی» اثر مستقیم دارد. پس رقابت را از یک **دور کالیبراسیون ارزان** شروع کن.

### 10.1 دور کالیبراسیون (گیت G0)
قبل از هر کار سنگین، به هر سه ایجنت **همان دو تسک خالص و تست‌محور** را بده: **T04 (normalizer)** و **T06 (chunker)**. این دو بدون Docker، شبکه و Ollama هستند و کیفیت منطق را روشن نشان می‌دهند.

| معیار G0 | قبول |
|---|---|
| تست‌های جدول spec (T04: ۱۵ مورد اجباری + idempotency) | همه پاس، بدون ضعیف‌کردن تست |
| رعایت قراردادها (نام اینترفیس‌ها، لایه‌ها، `sealed`/`async`/nullable) | بدون انحراف |
| گزارش ۶ بخشی صادقانه | ادعای بدون اجرا ندارد |
| تعداد دورهای رفع ایراد تا سبز شدن | ≤ ۳ |
| خروجی `dotnet build -warnaserror` و `dotnet format` | تمیز |

نتیجه نقش‌ها را تعیین می‌کند:
- **قوی‌ترین مدل (معمولاً Claude):** سازنده‌ی اصلی، مخصوصاً تسک‌های حساس و وابسته به زیرساخت (T03، T07–T10، T11، T13).
- **مدلی که G0 را خوب رد کرد:** سازنده‌ی تسک‌های مستقل (T05 با سایدکار، T12، T17 UI) یا مرورگر.
- **مدلی که G0 را رد نکرد:** فقط مرورگر/تست‌نویس (خروجی‌اش باید توسط تو تأیید شود)، یا کنار گذاشته شود.

### 10.2 مدل‌های سبک‌تر/رایگان: تنظیم‌های لازم
1. **زمینه‌ی کوچک‌تر → فقط فایل‌های لازم را بده.** به‌جای «همه‌ی AGENTS.md را بخوان»، پیام شروع این باشد:
```
Read ONLY: AGENTS.md; docs/CONVENTIONS.md §1-§4; docs/SPEC-PERSIAN-AND-RAG.md §1;
docs/TASKS.md "Global task rules" + task T04. Do not read other files.
Task: T04. Follow the golden rules. End with the 6-heading report.
```
(بخش‌ها را برای هر تسک مطابق «جدول خواندن» AGENTS.md §0 انتخاب کن.)
2. **تسک‌های `[PLAN-FIRST]` را به ۲–۳ گام کوچک‌تر بشکن** و بعد از هر گام تأیید بگیر (مثلاً T09: اول storage+upload، بعد worker، بعد ingestion service).
3. **قبل از هر قطع شدن commit کن.** سهمیه/زمینه‌ی مدل‌های رایگان وسط کار تمام می‌شود. قاعده‌ی ۱۷ در AGENTS.md ایجنت را مجبور می‌کند `docs/progress/<task>-<agent>.md` بنویسد تا ایجنت یا سشن دیگری ادامه دهد.
4. **خروجی را بیشتر بررسی کن.** مدل‌های ضعیف‌تر بیشتر API می‌سازند (hallucination)، تست را ضعیف می‌کنند یا «TODO» می‌گذارند؛ `git diff` و جست‌وجوی `TODO|NotImplemented|Skip` را بعد از هر تسک بزن.

### 10.3 امنیت کلید و داده با مدل‌های سومِ‌شخص
- ایجنت‌ها فایل‌های پوشه را می‌خوانند و محتوایشان را به ارائه‌دهنده‌ی مدل می‌فرستند، **از جمله `.env`**. در کپی‌هایی که با مدل رایگان/سومِ‌شخص اجرا می‌شوند **هیچ کلید واقعی** نگذار؛ در `.env` آن کپی `Ai__Chat__Provider=Fake` و `Ai__Embedding__Provider=Fake` باشد. آزمایش با Gemini و Ollama واقعی را خودت دستی انجام بده.
- مدل‌های رایگان ممکن است prompt ها را ذخیره یا برای آموزش استفاده کنند. PDF های واقعی دانشگاه فقط در T01 لازم‌اند؛ اگر محرمانه‌اند، T01 را خودت (یا با ایجنت قابل‌اعتمادتر) انجام بده و `eval/pdfs/` را در کپی‌های دیگر نگذار.
- قبل از push هر شاخه: `git grep -nI -E "AIza|sk-|Password=" -- . ":!*.example"`.

### 10.4 برچسب‌گذاری درست نتایج
در گزارش بنویس: «Claude Code + <مدل>»، «Antigravity + Gemini Flash»، «OpenCode + <مدل رایگان>». اختلاف نتیجه را به «ترکیب ابزار+مدل» نسبت بده نه به ابزار تنها؛ نتیجه‌گیری درباره‌ی ابزارها بدون ثابت‌نگه‌داشتن مدل علمی نیست.
