# DocSearch — Defense Demo Script & Fallback Plan

This script guides a live 8–10 minute defense demonstration of **DocSearch** using real Persian university regulations and academic papers, including Selective OCR for font-corrupted PDFs, bilingual/RTL rendering, strict refusal on out-of-scope questions, and graceful Degraded Mode.

---

## 1. Pre-Demo Corpus Setup

Create **two separate collections** (never mix scanned twins and their clean digital originals in the same collection, per [ADR 0008](adr/0008-eval-dataset-v2-heldout-and-defect-tags.md)):

1. **`آیین‌نامه‌های دانشگاهی` (Primary Demo Collection):**
   - Clean digital PDFs: `aein-91.pdf`, `shiveh-karamoozi.pdf`, `rag-lewis-2020.pdf`
   - CMap/font-corrupted PDFs (requires `Extraction__OcrMode=Defective`): `dastyar-pazhuheshi.pdf`, `forsat-motaleati.pdf`
2. **`آزمون اسناد اسکن‌شده` (Optional Scan Demo Collection):**
   - `bein-almelali-scan.pdf` (8-page 150 DPI raster scan) — demonstrates `ocrPages` population on pure image PDFs.

Verify all uploaded documents reach status **`آماده` (`Ready`)** before the presentation (CPU `bge-m3` embedding + Tesseract OCR take ~4–6 s/page, so pre-ingest in advance and keep one small 1–2 page PDF ready if asked to show live upload).

---

## 2. Structured Demo Questions (10 Grounded Scenarios)

All questions below come directly from the frozen evaluation set (`eval/questions.json`, `eval-dataset-v2.2`).

| # | Category | Source PDF & Expected Pages | Prompt to Paste in UI | Expected System Behavior & Key Facts |
|---|---|---|---|---|
| **1** | **Clean Digital (Numeric)** | `aein-91.pdf` — p. 4 (`q002`) | `حداکثر واحد مجاز انتخابی در دوره تابستانی چند واحد است؟` | Answers **`۶ واحد`** with `[1]`. Click `[1]` to scroll to `SourceCard` and open the source PDF. |
| **2** | **Legal Article + Note (`ArticleKeys`)** | `aein-91.pdf` — p. 4 (`q015`) | `تبصره ۱ ماده ۷ آیین‌نامه آموزشی کارشناسی چه می‌گوید؟` | Showcases legal structure chunking + `ArticleKeys` + note ordering ([ADR 0005](adr/0005-hybrid-retrieval-and-article-keys.md)): معدل حداقل **`۱۷`** → نیمسال بعد حداکثر **`۲۴ واحد`**. |
| **3** | **Colloquial Persian Paraphrase** | `aein-91.pdf` — p. 7 (`q026`) | `اگه معدل یه ترمم زیر ۱۲ بشه چی می‌شه و ترم بعد چند واحد می‌تونم بردارم؟` | Bridges colloquial phrasing (`زیر ۱۲`) to formal text: **مشروط** می‌شود و ترم بعد حداکثر **`۱۴ واحد`** (جز آخرین نیمسال). |
| **4** | **Multi-Chunk Synthesis** | `aein-91.pdf` — pp. 5, 7 (`q111`) | `تفاوت حداکثر سنوات مجاز تحصیل و تعداد نیمسال‌های مشروطی مجاز بین کارشناسی پیوسته و کارشناسی ناپیوسته چقدره؟` | Combines two articles across pages 5 & 7 with multiple `[n]` citations: ناپیوسته **`۲/۵ سال` و `۲ نیمسال`** vs پیوسته **`۵ سال` و `۳ نیمسال`**. |
| **5** | **Held-out Regulation (Clean)** | `shiveh-karamoozi.pdf` — p. 4 (`q071`) | `نمره کارآموزی چند نمره‌اش دست سرپرست شرکته و چند نمره‌اش دست استاد دانشگاه؟` | Answers **`۳۰ نمره`** (سرپرست کارآموزی) and **`۷۰ نمره`** (استاد کارآموزی), citing `shiveh-karamoozi.pdf` p. 4. |
| **6** | **CMap Font Defect + Selective OCR** | `dastyar-pazhuheshi.pdf` — p. 5 (`q011`) | `حق‌الزحمه ساعتی دستیار پژوهشی که دانشجوی دکتری است چند ریال است؟` | Answers **`۳۰۰٬۰۰۰ ریال`** (and `۲۰۰٬۰۰۰ ریال` for master's graduates). *Note:* Raw PyMuPDF without OCR extracts corrupted `۳۱۱٬۱۱۱`; open Document Details in the UI to show detected `ocrPages`. |
| **7** | **Held-out CMap + Mixed Latin/Persian** | `forsat-motaleati.pdf` — p. 4 (`q096`) | `طبق تبصره ۱۳ ماده ۴ شیوه‌نامه فرصت مطالعاتی، دانشگاه مقصد و استاد پذیرنده برای فرصت مطالعاتی خارج از کشور باید چه رتبه‌ای داشته باشن؟` | Answers rank **`< 1000`** (تایمز یا شانگهای) and **`H-index > 20`** in **`Scopus`**, verifying OCR recovery of digits + English terms on unseen CMap PDFs. |
| **8** | **English / Bidi Query** | `rag-lewis-2020.pdf` — p. 1 (`q050`) | `What two kinds of memory do RAG models combine?` | Composer automatically switches `dir="ltr"`; `bge-m3` retrieves English chunk; answers **parametric memory** (seq2seq) and **non-parametric memory** (dense vector index of Wikipedia) with `[1]`. |
| **9** | **Near-Domain Refusal Trap #1** | *(none)* — `not-in-docs` (`q041`) | `مرخصی دانشجوی فوق لیسانس چند ترمه ؟` | `notFound: true` — displays neutral refusal banner (`پاسخی در اسناد یافت نشد`) without hallucinating bachelor's rules (`aein-91.pdf` covers کاردانی/کارشناسی only). |
| **10** | **Near-Domain Refusal Trap #2** | *(none)* — `not-in-docs` (`q042`) | `شهریه سالانه دانشجوی ایرانی دوره دکتری در دانشگاه شهید مدنی آذربایجان چقدر است؟` | `notFound: true` — refuses cleanly despite `bein-almelali.pdf` containing international student tuition tables. |

---

## 3. Step-by-Step Degraded Mode Demo

Demonstrates that an upstream Chat LLM outage (e.g. Gemini quota/VPN loss) never breaks document search:

1. **Simulate Chat LLM Unavailability** (pick one):
   - *Clean controlled method (recommended):* Set `Ai__Chat__Provider=Fake` and `Ai__Chat__Fake__Fail=true` (or disconnect VPN / set an unreachable `Ai__Chat__Endpoint`) and restart `api`:
     ```powershell
     docker compose --profile full up -d api
     ```
2. **Verify Readiness Remains Healthy:**
   ```powershell
   Invoke-WebRequest http://localhost:5173/health/ready | Select-Object StatusCode, Content
   ```
   `/health/ready` still returns **`200 Healthy`** (`database`, `extractor`, `embedding`), because external LLM availability is intentionally excluded from readiness ([SPEC-DATA-AND-API.md](SPEC-DATA-AND-API.md) §2.6).
3. **Submit Question #1 in the Chat UI:**
   - Send `حداکثر واحد مجاز انتخابی در دوره تابستانی چند واحد است؟`.
   - API returns **`200 OK`** with `degraded: true`, `answer: null`, and populated `sources`.
   - Web UI displays the warning **`DegradedBanner`** (*«مدل تولید پاسخ موقتاً در دسترس نیست (حالت بازیابی اسناد)»*) followed by the ranked `SourceCard` excerpts and working **مشاهده PDF** buttons.
4. **Restore Normal Chat Mode:** Restore `Ai__Chat__Provider=OpenAICompatible` (and `Ai__Chat__Fake__Fail=false`), then run `docker compose --profile full up -d api`.

---

## 4. Defense-Day Fallback Plan

### Pre-Flight Checklist (T - 30 Minutes)

- [ ] Close heavy background apps (extra IDEs/browsers) to leave comfortable RAM inside the **12 GB** budget.
- [ ] Confirm Docker Desktop is **Engine running** and containers are healthy: `docker compose --profile full ps`.
- [ ] Confirm host Ollama is running CPU-only (`CUDA_VISIBLE_DEVICES=-1`) with `bge-m3` (`ollama list`).
- [ ] Confirm VPN is active (if using live Gemini) and verify `http://localhost:5173/health/ready` returns `200`.
- [ ] Send **one warm-up query** (e.g., Question #1) so `bge-m3` is already loaded in memory (`~1.5–2.5 GB`) before the committee enters.
- [ ] Confirm pre-ingested collections show `Ready`, plus have one small 1–2 page test PDF on Desktop if asked to demonstrate upload + polling live.

### Failure Contingencies

1. **VPN or Gemini Drops Mid-Defense:**
   - First, treat the moment as an intentional **Degraded Mode** demonstration (show `DegradedBanner` + retrieved Persian snippets + PDF open).
   - If a full LLM answer flow is needed offline, switch `.env` to `Ai__Chat__Provider=Fake` (`Ai__Chat__Fake__Fail=false`) and restart `api` (`docker compose --profile full up -d api`) — or use a locally pulled small Ollama chat model (`Ai__Chat__Provider=Ollama`, `Ai__Chat__Endpoint=http://host.docker.internal:11434`) if RAM allows.
2. **Container / Hardware / Projector Failure — Backup Demo Video `[H]`:**
   - **Human action (`[H]`):** Before defense day, record a 3–5 minute offline **MP4 backup demo video** showing:
     1. Login → Collection creation → PDF upload (`202` + status polling `Uploaded -> Processing -> Ready`) → Document Details (`ocrPages`, `chunkCount`).
     2. Asking Persian numeric, legal-reference (`ArticleKeys`), CMap-OCR (`q011`), English (`q050`), and `notFound` refusal (`q041`) questions.
     3. Clicking `[n]` citation → highlighting `SourceCard` → opening PDF.
     4. Triggering Degraded Mode (`DegradedBanner` + sources).
   - Keep the MP4 on local disk and USB flash drive alongside the thesis slides.
