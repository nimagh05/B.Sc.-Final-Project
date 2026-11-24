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

### Phase 4: Stage 2 End-to-End Evaluation & Reporting (T19b)

Stage 2 evaluates the full `/ask` question-answering pipeline using ungated execution (`Rag__MinSimilarity=0`), live retrieval diagnostics via `POST /api/v1/dev/retrieve`, exact-order content-hash caching, and offline 3-threshold simulation (`prod_055`, `calibrated_tau`, `ungated`).

> [!NOTE]
> Authentication credentials must be provided via `$env:EVAL_EMAIL` and `$env:EVAL_PASSWORD` (matching the T19a collection owner user) or via `--email` and `--password` CLI flags. Never hardcode credentials.
> The `--chat-model` CLI flag must match the running API's configured `Ai__Chat__Model` (e.g. `gemini-2.5-flash`).

#### Step 4.0: Fast Stage 2 Smoke Test
Before executing full benchmark phases, run a fast 5-question smoke test to verify API connectivity, dev/retrieve diagnostics, live `/ask`, and cache mechanics:
1. Start the API in Terminal 1 with:
   ```powershell
   . .\scripts\load-env.ps1
   $env:Rag__UseHybrid = "true"
   $env:Rag__MinSimilarity = "0"
   $env:Rag__Temperature = "0"
   $env:Diagnostics__ExposeTimings = "true"
   $env:Diagnostics__EnableEvalEndpoints = "true"
   $env:RateLimiting__Enabled = "false"
   dotnet run --project src/DocSearch.Api
   ```
2. In Terminal 2, run the smoke test:
   ```powershell
   .venv\Scripts\python eval/run_eval.py --stage2 --smoke --arms arm2_hybrid_ocr_off --chat-model gemini-2.5-flash --out eval/results/t19b_smoke
   ```

#### Step 4.1: Phase A Execution (Vector-Only Arm 1)
1. In Terminal 1, start the API with vector search only:
   ```powershell
   . .\scripts\load-env.ps1
   $env:Rag__UseHybrid = "false"
   $env:Rag__MinSimilarity = "0"
   $env:Rag__Temperature = "0"
   $env:Diagnostics__ExposeTimings = "true"
   $env:Diagnostics__EnableEvalEndpoints = "true"
   $env:RateLimiting__Enabled = "false"
   dotnet run --project src/DocSearch.Api
   ```
2. In Terminal 2, run Arm 1:
   ```powershell
   .venv\Scripts\python eval/run_eval.py --stage2 --arms arm1_vector_ocr_off --chat-model gemini-2.5-flash --out eval/results/t19b_phase_a
   ```

#### Step 4.2: Phase B Execution (Hybrid Arms 2, 3 & Regime B)
1. In Terminal 1, restart the API with hybrid search enabled:
   ```powershell
   . .\scripts\load-env.ps1
   $env:Rag__UseHybrid = "true"
   $env:Rag__MinSimilarity = "0"
   $env:Rag__Temperature = "0"
   $env:Diagnostics__ExposeTimings = "true"
   $env:Diagnostics__EnableEvalEndpoints = "true"
   $env:RateLimiting__Enabled = "false"
   dotnet run --project src/DocSearch.Api
   ```
2. In Terminal 2, run Arms 2, 3, and Regime B:
   ```powershell
   .venv\Scripts\python eval/run_eval.py --stage2 --arms arm2_hybrid_ocr_off,arm3_hybrid_ocr_defective,regime_b_scan_off,regime_b_scan_defective --chat-model gemini-2.5-flash --out eval/results/t19b_phase_b
   ```

#### Step 4.3: Live Threshold Validation Cross-Check (`--validate-offline-sim`)
Validates that the offline `prod_055` simulation exactly matches live gated API behavior. Because `/ask` reads server-level `UseHybrid`, validation is split into two short runs matching the server setting:

**Part A (Vector-Only Validation):**
1. In Terminal 1, restart the API with vector search and `MinSimilarity=0.55`:
   ```powershell
   . .\scripts\load-env.ps1
   $env:Rag__UseHybrid = "false"
   $env:Rag__MinSimilarity = "0.55"
   $env:Rag__Temperature = "0"
   $env:Diagnostics__ExposeTimings = "true"
   $env:Diagnostics__EnableEvalEndpoints = "true"
   $env:RateLimiting__Enabled = "false"
   dotnet run --project src/DocSearch.Api
   ```
2. In Terminal 2, validate the vector-only arm against Phase A:
   ```powershell
   .venv\Scripts\python eval/run_eval.py --validate-offline-sim --stage2-phase-a eval/results/t19b_phase_a --arms arm1_vector_ocr_off --no-cache
   ```

**Part B (Hybrid Validation):**
1. In Terminal 1, restart the API with hybrid search and `MinSimilarity=0.55`:
   ```powershell
   . .\scripts\load-env.ps1
   $env:Rag__UseHybrid = "true"
   $env:Rag__MinSimilarity = "0.55"
   $env:Rag__Temperature = "0"
   $env:Diagnostics__ExposeTimings = "true"
   $env:Diagnostics__EnableEvalEndpoints = "true"
   $env:RateLimiting__Enabled = "false"
   dotnet run --project src/DocSearch.Api
   ```
2. In Terminal 2, validate the hybrid arms against Phase B:
   ```powershell
   .venv\Scripts\python eval/run_eval.py --validate-offline-sim --stage2-phase-b eval/results/t19b_phase_b --arms arm2_hybrid_ocr_off,arm3_hybrid_ocr_defective,regime_b_scan_off --no-cache
   ```

#### Step 4.4: Merge Phases, Final Reports & Plot Generation
Merge Phase A and Phase B into unified Stage 2 results, generate summary CSV, report Markdown, and all 3 Matplotlib plots:
```powershell
.venv\Scripts\python eval/run_eval.py --stage2-report `
  --phase-a-dir eval/results/t19b_phase_a `
  --phase-b-dir eval/results/t19b_phase_b `
  --stage1-results eval/results/t19a_stage1/results.json `
  --out eval/results/t19b_stage2
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
