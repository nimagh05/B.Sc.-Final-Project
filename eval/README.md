# DocSearch Evaluation Harness & Multi-Arm Benchmark (T19)

This directory contains the scientific evaluation harness and multi-arm benchmark suite for DocSearch.
Dataset is frozen at tag `eval-dataset-v2.2` (`eval/questions.json`: 138 questions across 14 digital PDFs + 1 synthetic scan twin).

---

## 1. Prerequisites & Environment Setup

### 1.1 Python Dependencies
Install pinned evaluation dependencies into the local Python environment (`.venv`):
```powershell
.venv\Scripts\pip install -r eval/requirements.txt
```

### 1.2 Authentication Credentials
Credentials must **never** be hardcoded or read from local `.env`. Supply them via environment variables or CLI flags:
```powershell
$env:EVAL_EMAIL = "admin@ci.test"
$env:EVAL_PASSWORD = "Ci_Admin_123!"
```

---

## 2. Ingestion & Multi-Arm Evaluation Workflow

Because `Extraction:OcrMode` is a server-level configuration setting, ingestion follows a clean 2-phase API lifecycle, after which evaluation runs rapidly against the persistent PostgreSQL database.

### Phase 1: Ingestion with `OcrMode=Off` (Shared Baseline Collection)
1. Start the API with OCR disabled and rate limits raised for evaluation:
   ```powershell
   $env:Extraction__OcrMode = "Off"
   $env:RateLimiting__AskPermitLimit = "1000"
   $env:RateLimiting__UploadPermitLimit = "1000"
   $env:Diagnostics__EnableEvalEndpoints = "true"
   dotnet run --project src/DocSearch.Api
   ```
2. Ingest the baseline collections (`eval-regime-a-ocr-off` shared by Arm 1 & Arm 2, and `eval-regime-b-scan-off`):
   ```powershell
   .venv\Scripts\python eval/run_eval.py --ingest-only --ocr-mode Off
   ```
   *Verification:* Asserts that 0 pages trigger OCR across all uploaded documents.

---

### Phase 2: Ingestion with `OcrMode=Defective` (Selective OCR Collection)
1. Stop the API process (Ctrl+C) and restart it with selective OCR enabled:
   ```powershell
   $env:Extraction__OcrMode = "Defective"
   $env:RateLimiting__AskPermitLimit = "1000"
   $env:RateLimiting__UploadPermitLimit = "1000"
   $env:Diagnostics__EnableEvalEndpoints = "true"
   dotnet run --project src/DocSearch.Api
   ```
2. Ingest the selective OCR collections (`eval-regime-a-ocr-defective` and `eval-regime-b-scan-defective`):
   ```powershell
   .venv\Scripts\python eval/run_eval.py --ingest-only --ocr-mode Defective
   ```
   *Verification:*
   - Asserts exact match against the verified T12 expected OCR page sets:
     - `tarh-dakheli.pdf`: pages 1–10
     - `forsat-motaleati.pdf`: pages 1–8
     - `dastyar-pazhuheshi.pdf`: pages 1–6
     - `kod-akhlaghi.pdf`: pages 1–2
     - `bedoon-factor.pdf`: pages 1–2
     - `hamayesh.pdf`: page 1
     - `bein-almelali-scan.pdf`: pages 1–8
     - All 8 clean digital PDFs: 0 OCR pages.
   - Automatically benchmarks the extractor sidecar (`POST {sidecar_url}/ocr`) across all 37 pages (29 CMap + 8 scan) to record `pure_ocr_sec_per_page` into `eval/cache/ocr_timing.json`.
   - Re-runs can also execute pure OCR timing independently via `.venv\Scripts\python eval/run_eval.py --benchmark-ocr`.

---

### Phase 3: Stage 1 Evaluation & Statistical Inference
Run Stage 1 retrieval, per-arm plateau threshold calibration, and paired bootstrap inference across all arms:
```powershell
.venv\Scripts\python eval/run_eval.py --stage1 --reuse-collections --out eval/results/t19a_run
```
Reuses collection IDs from `eval/cache/collection_manifest.json` (or live server fallback) and skips re-uploading documents.

#### Fast Smoke Test:
To verify API connectivity and the retrieval pipeline across 5 sample questions without running the full 138-question benchmark:
```powershell
.venv\Scripts\python eval/run_eval.py --smoke --reuse-collections
```

---

## 3. Automated CI-Safe Test Suite

Fast unit tests (< 7 seconds, zero Docker, zero Ollama, zero real PDFs) verifying hit rules, twin mapping, plateau calibration, bootstrap guards, pure OCR timing calculations, and collection manifest persistence:
```powershell
.venv\Scripts\pytest eval/tests/ -v
```

---

## 4. Output Artifacts & Diagnostics

Benchmark runs produce structured artifacts in `eval/results/<run-id>/` and `eval/cache/`:
- `eval/cache/collection_manifest.json`: Collection IDs, Git commit SHA, and document counts across ingestion phases.
- `eval/cache/ocr_timing.json`: Pure OCR sidecar timings (s/page for CMap 29 pages, scan 8 pages, and overall 37 pages).
- `eval/results/<run-id>/results.json`: Full per-question results, per-arm plateau calibrations (plus pure cosine sensitivity), pure OCR sidecar metrics, Regime B summary, and reproducibility manifest.
- `eval/results/<run-id>/summary.csv`: Machine-readable metrics across all cohorts (splits, question types, defect cohorts, heldout-CMap per-document deltas, Regime B 8 questions).
- `eval/results/<run-id>/report.md`: Markdown summary report with pre-registered primary endpoints, plateau calibration ranges, cohort comparisons, and pure OCR timing benchmarks.
- Response Headers: When `Diagnostics:ExposeTimings=true`, `POST /api/v1/ask` returns `X-Timing-Retrieval-Ms` and `X-Timing-Generation-Ms`.
