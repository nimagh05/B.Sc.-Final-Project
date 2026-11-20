# ADR 0009: OCR Sidecar Architecture, Execution Modes (`Off | Defective | Always`), and Pre-Chunking Detection

- **Status:** Accepted
- **Date:** 2026-11-20
- **Task:** T12
- **Amends / Extends:** ADR 0002 (§1 PDF Extraction), ADR 0008 (§4 Evaluation Protocol), `docs/SPEC-PERSIAN-AND-RAG.md` §2

---

## Context

1. In Persian academic and regulatory PDFs, two distinct defects prevent text extraction and subsequent retrieval:
   - **Image-only scans:** Documents containing raster images with no embedded text layer (`NeedsOcr == true` via `PageQualityScorer`, e.g., `bein-almelali-scan.pdf`).
   - **CMap-corrupted fonts:** Documents with broken Unicode mapping tables where characters render visually as Persian ligatures (such as `"لا"`), but extract as inverted or isolated codepoints (such as `"ال"`), severely disrupting keyword indexing, full numbers, and embedding retrieval (e.g. `dastyar-pazhuheshi.pdf`, `bedoon-factor.pdf`).
2. Prior to Task T12, `HttpPdfExtractor` called only `POST /extract` on the Python sidecar (`tools/extractor`), relying solely on PyMuPDF's raw digital text layer. Defective documents suffered substantial recall drops (e.g. 0% keyword recall on scanned pages, 0% number recall across CMap-defective documents).
3. OCR on Persian text using Tesseract (`fas+eng`) is computationally expensive and can introduce minor recognition noise or character substitutions when applied to clean digital text layers. Furthermore, CPU-only environments with 12 GB RAM can suffer thread oversubscription if Tesseract spawns multi-threaded OpenMP workers across cores.
4. Conformance tests and automated GitHub Actions CI must run deterministically without requiring a host or containerized Tesseract engine installed.

---

## Decisions

### 1. Three-Mode Configuration (`Extraction:OcrMode = Off | Defective | Always`)

- `ExtractionOptions` introduces `OcrMode` (`Off`, `Defective`, `Always`), with `Off` as the safe default:
  - **`Off` (Default):** Zero OCR calls. Ingestion forwards PDF streams directly to `HttpPdfExtractor` without stream buffering or memory overhead. Keeps CI pipelines, unit tests, and all 24 conformance tests green without Tesseract dependencies.
  - **`Defective`:** Selective OCR. Detects scanned pages (`NeedsOcr == true`) and CMap/font-defective pages, dispatching OCR **only** for flagged pages while leaving clean digital pages completely untouched.
  - **`Always`:** Forced OCR. Dispatches every page of every uploaded document to the OCR endpoint.
- **Backward Compatibility:** `Extraction:OcrMode` is nullable (`OcrMode?`). If a deployment specifies legacy `Extraction:OcrEnabled = true` without setting `OcrMode`, `EffectiveOcrMode` maps to `OcrMode.Defective`. An explicit `OcrMode = Off` strictly overrides `OcrEnabled = true`.
- **Options Validation:** `OcrDpi` is bounded to `72..600` (default `300`), `OcrLanguages` defaults to `"fas+eng"` (must be non-empty), and `OcrBatchSize` is bounded to `1..50` (default `5`).

### 2. Pre-Chunking Detection & Document-Wide CMap Propagation

- In `OcrPdfExtractor`, extraction proceeds in two stages **before** text chunking:
  1. Base extraction runs via inner `HttpPdfExtractor`.
  2. In `Defective` mode, each page is evaluated for low quality (`PageQualityScorer.ScorePage`) and CMap ligature corruption (`TextDefectDetector.DetectPageDefects` on NFKC-normalized display text: `chars >= 300 && al >= 10 && (la == 0 || la/al < 0.04)`).
  3. **Document-Wide Propagation:** Embedded font CMap corruption is a document-wide font embedding artifact. If *any* page in the document exhibits `CorruptLigatureLamAlef`, all pages containing Persian text (`[\u0600-\u06FF\u0750-\u077F\uFB50-\uFDFF\uFE70-\uFEFF]`) in that document are flagged for OCR.
- **Page Provenance & Quality Rescoring:**
  - `PageText` records store `ExtractionSource` (`"text_layer"` vs `"ocr"`), `RawText` (preserving the original PyMuPDF text layer for auditability), and computed `IsOcr`.
  - Merged OCR text is rescored with `PageQualityScorer.ScorePage(pageNumber, ocrText, minQuality)` so that `Quality`, `NeedsOcr`, and `LooksReversed` reflect the actual OCR output.
  - `HeaderFooterCleaner.Clean()` uses `page with { Text = cleanedText }` to preserve all `PageText` provenance metadata across header/footer removal.
  - `IngestionService` persists the list of recognized pages to PostgreSQL `documents.ocr_pages int[]`, exposed via `DocumentDetailResponse.ocr_pages`.

### 3. Python Sidecar Architecture (`POST /ocr`), Concurrency, and Resource Guard

- `tools/extractor/Dockerfile` installs Debian package `tesseract-ocr-fas` and `tesseract-ocr-eng`.
- `ENV OMP_THREAD_LIMIT=1` is configured in `Dockerfile` to prevent OpenMP thread oversubscription on CPU-only machines.
- **Model Specification:** Debian package `tesseract-ocr-fas 1:4.1.0-2` installs `/usr/share/tesseract-ocr/5/tessdata/fas.traineddata` (~422 KB), which is Debian's `tessdata_fast` integer-quantized LSTM model. External unpinned model downloads (e.g. `tessdata_best`) are excluded to maintain reproducible container builds.
- **Locking & Concurrency:**
  - `def ocr(...)` is a synchronous Starlette endpoint executed on the worker threadpool, ensuring long OCR jobs never block FastAPI's event loop (`GET /health` remains responsive).
  - PyMuPDF page rendering (`page.get_pixmap(dpi=dpi, alpha=False)`) is guarded by `_extraction_lock` to ensure PyMuPDF thread safety. `alpha=False` is strictly enforced to guarantee 3-channel RGB images, preventing Pillow/Tesseract RGBA crashes.
  - The lock is released **before** invoking `pytesseract.image_to_string`, allowing multiple OCR inference workers to proceed without blocking PDF parsing.

### 4. Page Batching & Graceful Degradation

- `OcrPdfExtractor` partitions flagged pages into chunks of `OcrBatchSize` (default `5`, range `1..50`).
- Batching prevents large defective documents (e.g., 10+ pages) from exceeding HTTP request timeouts (300s).
- **Fail-Fast Loop & Partial Failure Resilience:** If an OCR batch fails (e.g., HTTP timeout where `!ct.IsCancellationRequested`, HTTP 503/5xx, or network failure), the loop breaks immediately (fail-fast) to prevent hung sidecar cascading timeouts across subsequent batches. Completed batches are retained in memory. The service logs a structured `LogWarning` for the failed and skipped batches and falls back to base text for those pages.
- **Cancellation Safety:** `OperationCanceledException` is rethrown when `ct.IsCancellationRequested` is true (host shutdown); it is treated as a recoverable timeout only when `!ct.IsCancellationRequested`.

### 5. Empirical Verification & Anti-Leakage Parameter Selection

To strictly obey ADR 0008 anti-leakage rules, OCR hyperparameter selection (`--psm` 3 vs 4 vs 6, `dpi` 300 vs 400) was conducted **exclusively on `tuned` documents** (`bedoon-factor.pdf`, `dastyar-pazhuheshi.pdf`, `kod-akhlaghi.pdf`, `hamayesh.pdf`, and `bein-almelali-scan.pdf`), excluding heldout documents (`tarh-dakheli.pdf`, `forsat-motaleati.pdf`).

#### Tuned Ablation Results (N=17 Answerable Questions + Clean `aein-91.pdf` N=24):

| Configuration | Tuned CMap KW (N=9) | Tuned CMap Num (N=3) | Scan Twin KW (N=8) | Scan Twin Num (N=3) | Combined KW (N=17) | `aein-91.pdf` KW (N=24) |
|---|---|---|---|---|---|---|
| **OcrMode=Off** | 7/9 (77.8%) | 0/3 (0.0%) | 0/8 (0.0%) | 0/3 (0.0%) | 7/17 (41.2%) | 23/24 (95.8%) |
| **DPI 300, PSM 6** | **8/9 (88.9%)** | **2/3 (66.7%)** | **6/8 (75.0%)** | **2/3 (66.7%)** | **14/17 (82.4%)** | **21/24 (87.5%)** |
| **DPI 300, PSM 4** | 8/9 (88.9%) | 2/3 (66.7%) | 6/8 (75.0%) | 1/3 (33.3%) | 14/17 (82.4%) | 16/24 (66.7%) |
| **DPI 300, PSM 3** | 9/9 (100.0%) | 3/3 (100.0%) | 5/8 (62.5%) | 1/3 (33.3%) | 14/17 (82.4%) | 16/24 (66.7%) |
| **DPI 400, PSM 6** | 7/9 (77.8%) | 1/3 (33.3%) | 4/8 (50.0%) | 0/3 (0.0%) | 11/17 (64.7%) | 23/24 (95.8%) |
| **DPI 400, PSM 4** | 6/9 (66.7%) | 1/3 (33.3%) | 5/8 (62.5%) | 0/3 (0.0%) | 11/17 (64.7%) | 17/24 (70.8%) |
| **DPI 400, PSM 3** | 6/9 (66.7%) | 1/3 (33.3%) | 4/8 (50.0%) | 0/3 (0.0%) | 10/17 (58.8%) | 17/24 (70.8%) |

#### Analysis & Decision:
1. **DPI Selection:** DPI 300 clearly beat DPI 400 (Combined KW 14/17 vs 11/17). At 400 DPI, character over-segmentation in Tesseract's Persian LSTM model degraded recall across all segmentation modes. 300 DPI is locked as the baseline resolution.
2. **PSM Selection:** On `tuned` Combined KW, PSMs 3, 4, and 6 tied at 14/17 (82.4%), which is statistically indistinguishable at $N=8\dots9$. PSM 6 is retained as the pre-selected baseline default (`--psm 6`, assume a single uniform block of text). It also yielded 2/3 on scan twin numbers (vs 1/3 for PSMs 3 and 4) and avoids line-segmentation regressions observed on `aein-91.pdf` under PSM 3/4.
3. **Primary Empirical Benefit & Nuance:**
   - OCR's primary empirical benefit is recovering corrupted multi-digit numbers on CMap PDFs (improving from 0/3 to 2/3 on `tuned`, and 0/10 to 6/10 overall across all 6 CMap PDFs) and recovering text from image-only scans (0/8 to 6/8 KW, and 0/3 to 2/3 numbers on the scan twin).
   - In contrast, raw keyword recall on the 4 `tuned` CMap PDFs moved only modestly from 7/9 to 8/9, because Persian font CMap corruption primarily destroys isolated digits, ligatures, and punctuation codepoints rather than all stem vocabulary; furthermore, OCR introduces occasional character substitutions (e.g., `q032` regressed on a keyword match under OCR, compensated by gains on `q004` and `q012`).
4. **Clean-Document Regression Guard (`aein-91.pdf`):** Under forced `OcrMode=Always`, recognition noise reduced keyword recall from 23/24 (95.8%) to 21/24 (87.5%) and number recall from 14/14 (100.0%) to 11/14 (78.6%). This provides empirical evidence confirming that `OcrMode=Defective` (which triggers 0 OCR pages on clean documents) must be used in production to preserve 100% of pristine digital text quality.
5. **Unbiased Held-Out CMap Evaluation (`tarh-dakheli.pdf` + `forsat-motaleati.pdf`, N=27):**
   - After hyperparameter locking, pre-retrieval recall was evaluated on the pure held-out CMap cohort ($N=27$ answerable questions):
     - **Keyword Recall:** `20/27 (74.1%) -> 21/27 (77.8%)`
     - **Number Recall:** `0/7 (0.0%) -> 4/7 (57.1%)`
     - **Unit Recall:** `0/10 (0.0%) -> 2/10 (20.0%)`
   - This validates generalization: selective OCR on defective pages recovers corrupted numeric and unit phrases on unseen documents without leaking test data into model tuning.

#### Leak-Free Model Comparison (`tessdata_fast` vs `tessdata_best`):

To empirically test whether an unquantized model improves accuracy, a leak-free benchmark was executed on `tuned` documents + `bein-almelali-scan.pdf` (19 pages, DPI 300, PSM 6) comparing Debian's packaged `tesseract-ocr-fas` (`tessdata_fast`, ~422 KB) with pinned upstream `tesseract-ocr/tessdata_best` (commit `e12c65a915945e4c28e237a9b52bc4a8f39a0cec`, `fas.traineddata` 3.17 MB, SHA-256 `99e420969b5ddd2cb135b416316a7ed417c59c4faf9e0d28941348f6448114df`, mounted read-only while `eng.traineddata` remained Debian's `tessdata_fast`):
- **Latency:** `tessdata_best` took `132.4s` for 19 pages (~7.0s/page on CPU), an 8.8x slowdown compared to `tessdata_fast` (~15s total, ~0.8s/page).
- **Recall Degradation:** Rather than improving accuracy, `tessdata_best` significantly underperformed Debian's `tessdata_fast` on both Combined KW (`11/17 = 64.7%` vs `14/17 = 82.4%`) and Combined Num (`1/6 = 16.7%` vs `4/6 = 66.7%`). On the Scan Twin (`bein-almelali-scan.pdf`), KW dropped from `6/8 -> 3/8` and Num dropped from `2/3 -> 0/3`.
- **Decision:** Retain Debian's packaged `tesseract-ocr-fas` (`tessdata_fast`) with zero external model downloads in `Dockerfile`.

### 6. Scope & Known Limitations

- **Embedded Raster Screenshots:** In documents like `ziro-guide.pdf` (`q034, q039`), UI button labels and menu paths are embedded within raster image crops placed inside otherwise clean digital pages. Because the parent pages contain valid vector text, they do not trigger `NeedsOcr` or `CMap` defects. Extracting text from embedded screenshot figures is out of scope for page-level OCR (would require image-object extraction / layout analysis) and is tracked under defect tag `"defect_mechanism": "screenshot"`.
- **Partial OCR Batch Failures & Document State:** When an OCR batch fails or times out, the fail-fast mechanism terminates remaining batches and falls back to base text, logging a structured warning. The ingestion process still transitions the document to `Ready` status (graceful degradation) rather than `Failed`. The persisted `document.OcrPages` lists only the pages that successfully completed OCR from earlier batches, not the failed or skipped pages.
- **Payload Re-Upload per Batch:** Each OCR batch currently re-transmits the full PDF payload (`MultipartFormDataContent`) with a specific page list to the local sidecar. While network overhead is negligible on localhost / local container networks, future enhancements could transmit pre-extracted page images or cached document sessions for remote sidecars.
- **OCR Model Retained (`tessdata_fast`):** As established by the empirical benchmark above, Debian's `tessdata_fast` outperforms `tessdata_best` in both CPU latency and Persian retrieval accuracy, completely eliminating the need for unquantized model variants.
