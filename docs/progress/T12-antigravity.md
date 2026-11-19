# Task T12 — OCR for Scanned & CMap-Defective Pages (Python Sidecar `POST /ocr` + C# `OcrPdfExtractor` Decorator)

**Agent:** Antigravity  
**Branch:** `feature/T12-ocr`  
**Status:** Completed  

---

## 1. What changed

1. **Python Sidecar (`tools/extractor`):**
   - `tools/extractor/requirements.txt`: Added exact pinned dependencies `pytesseract==0.3.13` and `pillow==12.3.0` (matching `.venv` and Linux container wheels).
   - `tools/extractor/app/main.py`:
     - Added `POST /ocr` endpoint accepting multipart `file` (`UploadFile`), `pages` (comma-separated 1-based page numbers), `dpi` (default `300`, bounded `72..600`), and `lang` (default `"fas+eng"`).
     - Guarded binary availability: checks `shutil.which("tesseract")` and returns `503 Service Unavailable` if missing.
     - Validated input parameters: strictly validates `lang` against `^[a-zA-Z0-9_]+(\+[a-zA-Z0-9_]+)*$` and validates page numbers; returns `400 Bad Request` with detail on error.
     - Enforced `MAX_UPLOAD_MB` (413), `%PDF-` magic bytes check (400), and encrypted PDF detection (422).
     - Optimized concurrency: `def ocr(...)` endpoint runs in Starlette worker threadpool, avoiding blocking the FastAPI event loop (`GET /health` remains responsive).
     - Minimized lock contention: holds `_extraction_lock` strictly during PyMuPDF page rendering (`page.get_pixmap(dpi=dpi, alpha=False)`), releases the lock immediately, and runs `pytesseract.image_to_string` outside the lock.
     - Fixed RGBA image crash by forcing `alpha=False` on `get_pixmap`, guaranteeing clean 3-channel RGB for Pillow and Tesseract.
   - `tools/extractor/tests/test_extractor.py`:
     - Added 8 unit tests covering `POST /ocr` happy path (mocked `image_to_string`), missing Tesseract binary (503), invalid/out-of-bounds page numbers (400), invalid DPI/lang (400), encrypted PDF (422), non-PDF upload (400), payload too large (413), and real Tesseract execution (auto-skipped when tesseract binary is not on host PATH).

2. **C# Domain & Contracts (`DocSearch.Application`):**
   - `src/DocSearch.Application/Abstractions/Contracts.cs`:
     - Extended `PageText` record with `string ExtractionSource = "text_layer"`, `string? RawText = null`, and computed property `bool IsOcr => ExtractionSource == "ocr"`.
   - `src/DocSearch.Application/Options/AppOptions.cs`:
     - Added `OcrMode` enum (`Off`, `Defective`, `Always`).
     - Added `OcrMode? OcrMode { get; set; }` (nullable), `OcrLanguages` (default `"fas+eng"`), and `OcrDpi` (default `300`).
     - Added computed property `EffectiveOcrMode`: defaults to `OcrMode.Off`; maps legacy `OcrEnabled = true` to `OcrMode.Defective` when `OcrMode` is null; and strictly honors explicit `OcrMode` (e.g. `OcrMode.Off` overrides `OcrEnabled = true`).
     - Added validation rules for `OcrDpi` (72..600) and `OcrLanguages` (non-empty).
   - `src/DocSearch.Application/Chunking/HeaderFooterCleaner.cs`:
     - Fixed page reconstruction to use `cleanedPages.Add(originalPage with { Text = cleanedText });`, preserving `ExtractionSource`, `RawText`, and `LooksReversed` metadata through header/footer cleaning.

3. **C# Infrastructure & Decorator (`DocSearch.Infrastructure` & `DocSearch.Api`):**
   - `src/DocSearch.Infrastructure/Extraction/OcrPdfExtractor.cs`:
     - Implemented `IPdfExtractor` decorator wrapping `innerExtractor` (`HttpPdfExtractor`).
     - Zero-copy stream optimization: when `EffectiveOcrMode == OcrMode.Off`, passes the stream directly to `innerExtractor` without buffering.
     - In `Defective` or `Always` modes, buffers non-seekable streams into a rewindable `MemoryStream` once.
     - Runs base extraction first via `innerExtractor`.
     - In `Defective` mode: detects low quality (`page.NeedsOcr`) and CMap/font corruption via `TextDefectDetector.DetectPageDefects` on normalized text (`PersianTextNormalizer.NormalizeForDisplay`).
     - Document-wide CMap propagation: if any page exhibits `CorruptLigatureLamAlef`, flags all pages with Persian text in the document.
     - Sends `POST /ocr` request using named `HttpClient` (`"OcrExtractor"`).
     - Merges OCR text using `PageQualityScorer.ScorePage(pageNumber, ocrText, options.MinPageQuality) with { ExtractionSource = "ocr", RawText = basePage.Text }`, updating `Quality`, `NeedsOcr`, and `LooksReversed`.
     - Graceful degradation: on HTTP error (503, 500, timeout), logs warning and returns base text layer without failing document ingestion.
     - Proper cancellation handling: only treats `OperationCanceledException` as fallback timeout when `!ct.IsCancellationRequested`; rethrows if host shutdown is requested.
   - `src/DocSearch.Infrastructure/Ingestion/IngestionService.cs`:
     - Populates `document.OcrPages` from `pages.Where(p => p.ExtractionSource == "ocr").Select(p => p.PageNumber).ToArray()`, persisted to PostgreSQL `documents.ocr_pages int[]`.
   - `src/DocSearch.Infrastructure/DependencyInjection.cs`:
     - Registered `HttpPdfExtractor` typed client and `"OcrExtractor"` named client (timeout 300s).
     - Registered `services.AddScoped<IPdfExtractor>(sp => new OcrPdfExtractor(sp.GetRequiredService<HttpPdfExtractor>(), ...))`.
   - `src/DocSearch.Api/appsettings.json`:
     - Set `"OcrMode": null`, `"OcrEnabled": false`, `"OcrDpi": 300`, `"OcrLanguages": "fas+eng"`, keeping all unit/conformance tests in zero-OCR mode by default while enabling clean environment variable overrides.

4. **Evaluation & Verification Harness:**
   - `eval/experiments/ocr_eval_check.py`:
     - Standalone audit & diagnostic benchmark script.
     - Part 1: Audits all 15 evaluation PDFs for `NeedsOcr` and `CMap` defect triggers; verified that all 6 CMap PDFs + 1 synthetic scan twin trigger, while clean PDFs trigger 0 pages.
     - Part 2: Executes OCR through Docker extractor sidecar (`--sidecar-url http://127.0.0.1:8000`).
     - Part 3: Evaluates Regime A pre-retrieval recall across Primary cohort (36 Qs), Secondary CMap union (27 Qs), Auto-CMap (27 Qs), Breakout unit-only (5 Qs), and Screenshot Qs (2 Qs).
     - Part 4: Clean-document regression guard on `aein-91.pdf`.
     - Part 5: Regime B isolated scan twin evaluation on `bein-almelali-scan.pdf` (8 gold questions).

---

## 2. How I verified

1. **Python Sidecar Unit Tests:**
   ```powershell
   .venv\Scripts\pytest tools/extractor/tests/test_extractor.py
   ```
   **Output:** `17 passed, 1 skipped in 12.50s` (real Tesseract test auto-skipped on host where binary is not installed).

2. **Docker Extractor Build & Real Tesseract Health Verification:**
   ```powershell
   docker compose build extractor
   docker compose up -d extractor
   .venv\Scripts\python -c "import urllib.request; print(urllib.request.urlopen('http://127.0.0.1:8000/health').read().decode())"
   ```
   **Output:** `{"status":"Healthy"}`

3. **Pre-Retrieval OCR Diagnostic Benchmark (`ocr_eval_check.py` against Docker Sidecar):**
   ```powershell
   .venv\Scripts\python eval/experiments/ocr_eval_check.py --sidecar-url http://127.0.0.1:8000
   ```
   **Output:**
   ```text
   === DocSearch Pre-Retrieval OCR Diagnostic & Evaluation ===
   Total PDFs found: 15 (expected 15)

   --- Part 1: 15-PDF Trigger Audit ---
   PDF File                     Pages  NeedsOcr   CMap Pages     Effective OCR Pages (Defective Mode)
   ------------------------------------------------------------------------------------------
   aein-91.pdf                  10     none       none           none
   bedoon-factor.pdf            2      none       1              1,2
   bein-almelali-scan.pdf       8      1,2,3,4,5,6,7,8 none           1,2,3,4,5,6,7,8
   bein-almelali.pdf            8      none       none           none
   bge-m3-paper.pdf             18     none       none           none
   dastyar-amoozeshi.pdf        3      none       none           none
   dastyar-pazhuheshi.pdf       6      none       1,2,3,4,5      1,2,3,4,5,6
   forsat-motaleati.pdf         8      none       1,2,3,4,5,6,7,8 1,2,3,4,5,6,7,8
   hamayesh.pdf                 1      none       1              1
   kod-akhlaghi.pdf             2      none       1              1,2
   rag-lewis-2020.pdf           19     none       none           none
   shiveh-karamoozi.pdf         13     none       none           none
   shoraha-pazhuheshi.pdf       5      none       none           none
   tarh-dakheli.pdf             10     none       1,2,3,4,5,6,7,8,10 1,2,3,4,5,6,7,8,9,10
   ziro-guide.pdf               48     none       none           none

   [OK] 15-PDF Trigger Audit strictly verified (6 CMap PDFs + 1 Scan twin triggered; clean PDFs intact).

   --- Part 2: OCR Execution via Sidecar (http://127.0.0.1:8000) ---
   ...

   --- Part 3: Regime A Pre-Retrieval Text Recall Evaluation ---
   Cohort                                 N     Mode        KW Recall    Num Recall   Unit Recall
   ------------------------------------------------------------------------------------------
   Primary (6 CMap PDFs, all answerable)  36    Off           75.0%        72.2%        61.1%
                                                Defective     80.6%        88.9%        72.2%
   ..........................................................................................
   Secondary (defect_mechanism == cmap, union) 27    Off           66.7%        63.0%        48.1%
                                                Defective     74.1%        85.2%        63.0%
   ..........................................................................................
   Secondary (auto_cmap subset)           27    Off           66.7%        63.0%        48.1%
                                                Defective     74.1%        85.2%        63.0%
   ..........................................................................................
   Breakout (5 unit-only questions)       5     Off          100.0%       100.0%         0.0%
                                                Defective     80.0%       100.0%        40.0%
   ..........................................................................................
   Separate (2 screenshot Qs, ziro-guide) 2     Off            0.0%       100.0%       100.0%
                                                Defective      0.0%       100.0%       100.0%
   ..........................................................................................

   --- Part 4: Clean-Document Regression Guard (aein-91.pdf) ---
   aein-91.pdf answerable questions (N=24):
     Raw PyMuPDF (OcrMode=Off):    KW Recall = 23/24 (95.8%), Num Recall = 24/24 (100.0%)
     Post-OCR (OcrMode=Always):     KW Recall = 21/24 (87.5%), Num Recall = 21/24 (87.5%)
     [Insight] Under OcrMode=Defective, aein-91.pdf triggers 0 OCR pages, preserving 100% of raw digital quality (23/24).
   [OK] Clean document regression guard satisfied: OCR retains >=85% recall even under forced Always mode.

   --- Part 5: Regime B Isolated Scan Twin (bein-almelali-scan.pdf) ---
   Evaluating 8 gold questions on synthetic 150 DPI scan twin (N=8)...
     OcrMode=Off (0 text):          KW Recall = 0/8 (0.0%), Num Recall = 5/8 (62.5%)
     OcrMode=Defective (Tesseract): KW Recall = 6/8 (75.0%), Num Recall = 7/8 (87.5%)
     Digital Reference:             KW Recall = 8/8 (100.0%), Num Recall = 8/8 (100.0%)

   [OK] Regime B evaluation completed successfully: OCR recovers text from image-only scan.
   === All Diagnostic and Evaluation Checks Complete ===
   ```

4. **Container Cleanup:**
   ```powershell
   docker compose stop extractor
   ```

5. **Application & Infrastructure Unit & Integration Tests:**
   ```powershell
   dotnet test --filter "Category!=RequiresOllama"
   ```
   **Output:**
   - `DocSearch.Application.Tests`: Passed: 189, Failed: 0.
   - `DocSearch.Infrastructure.Tests`: Passed: 139, Failed: 0.
   - `DocSearch.Api.Tests`: Passed: 62, Failed: 0.
   - **Total:** 390 passed, 0 failed.

6. **Code Formatting Verification:**
   ```powershell
   dotnet format --verify-no-changes
   ```
   **Output:** Passed (0 changes).

---

## 3. Tests added/changed

1. `tests/DocSearch.Application.Tests/OptionsValidationTests.cs`:
   - `Validate_ExtractionOptions_OcrDpi_OutOfRange_FailsValidation`
   - `Validate_ExtractionOptions_OcrLanguages_Empty_FailsValidation`
   - `Validate_ExtractionOptions_EffectiveOcrMode_Precedence_Succeeds`
2. `tests/DocSearch.Infrastructure.Tests/Extraction/OcrPdfExtractorTests.cs`:
   - `ExtractAsync_WhenOcrModeIsDefective_OnlySendsDefectivePagesToOcr`
   - `ExtractAsync_WhenAnyPageHasCMapDefect_PropagatesToAllPersianPages`
   - `ExtractAsync_WhenOcrModeIsOff_DoesNotCallOcrEndpoint`
   - `ExtractAsync_WhenOcrModeIsDefective_AndNoPagesAreDefective_DoesNotCallOcr`
   - `ExtractAsync_WhenOcrServiceReturns503_FallsBackToBasePagesGracefully`
   - `ExtractAsync_WhenCancellationRequested_RethrowsOperationCanceledException`
   - `ExtractAsync_WithNonSeekableStream_BuffersAndRewindsSuccessfully`
3. `tests/DocSearch.Infrastructure.Tests/Integration/IngestionServiceIntegrationTests.cs`:
   - `IngestAsync_WithOcrPages_HeaderFooterCleanerPreservesOcrPages_AndPersistsToDatabase`: 4-page integration test asserting `HeaderFooterCleaner` executes, preserves `ExtractionSource = "ocr"`, and persists `document.OcrPages = [2, 4]` mapped to `DocumentDetailResponse`.
4. `tools/extractor/tests/test_extractor.py`:
   - `test_ocr_endpoint_success_mocked`
   - `test_ocr_endpoint_missing_tesseract_503`
   - `test_ocr_endpoint_invalid_pages`
   - `test_ocr_endpoint_invalid_dpi`
   - `test_ocr_endpoint_invalid_lang`
   - `test_ocr_endpoint_encrypted_pdf`
   - `test_ocr_endpoint_non_pdf`
   - `test_ocr_endpoint_payload_too_large`
   - `test_ocr_endpoint_real_tesseract` (auto-skipped when binary absent)

---

## 4. What the human must check manually

1. **Docker Extractor Sidecar Execution with GPU/Host Tesseract (Optional):**
   - Run `docker compose up -d extractor`
   - Upload a scanned Persian PDF via Swagger / `POST /api/v1/documents` with `Extraction__OcrMode=Defective` in environment, and inspect `ocr_pages` in the response to confirm pages were recognized.
2. **Reviewing Pre-Retrieval Recall Table:**
   - Verify that CMap PDFs show numeric recall jumping from 72.2% to 88.9%, and keyword recall from 75.0% to 80.6%.

---

## 5. Known limitations / deviations / ADRs

- **No ADR required:** The solution adheres directly to the design laid out in `docs/SPEC-PERSIAN-AND-RAG.md` §2 and ADR 0002 / ADR 0008.
- **Tesseract Host Dependency:** Host Windows machine does not have `tesseract` installed on PATH; all OCR integration tests in Python and C# mock or degrade gracefully, while Docker containers and evaluation scripts run against the containerized Tesseract engine.
- **Embedded Screenshot Limitations:** Screenshot crops embedded within clean PDF vector pages (e.g. `ziro-guide.pdf`) are not flagged under `OcrMode.Defective` because the host page contains valid digital text; full document OCR via `OcrMode.Always` or dedicated image-block extraction would be required for those specific figures.

---

## 6. Recommended next task

- **T17 / Evaluation Milestone:** Re-run the full end-to-end evaluation pipeline with `Extraction:OcrMode=Defective` on PostgreSQL + Ollama embeddings, comparing RAG citation accuracy and answer generation against baseline.
