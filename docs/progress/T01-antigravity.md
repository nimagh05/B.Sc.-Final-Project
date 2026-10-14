# Progress Note: T01 — Phase-0 experiments: PDF extraction + embedding benchmark

**Agent:** Antigravity  
**Task:** T01 — Phase-0 experiments: PDF extraction + embedding benchmark  
**Date:** 2026-10-14  
**Status:** Completed  

## What Was Done
- **Question Validation:** Created `eval/validate_questions.py` and validated 63 questions in `eval/questions.json` across `dev` (20 items) and `test` (43 items) against the 12 documents in `eval/pdfs/`.
- **PDF Extractor Benchmark:**
  - Built `eval/experiments/pdfpig_probe/` console tool for `PdfPig`.
  - Implemented `eval/experiments/extract_compare.py` unifying extraction across `PyMuPDF`, `pdfplumber`, and `PdfPig`.
  - Evaluated 390 page extraction records with full quality metrics (SPEC-PERSIAN-AND-RAG.md §2).
  - Selected **PyMuPDF** (0% reversed text, 0.892 mean quality, 1.54s total time).
  - Identified scanned pages requiring OCR routing (`NeedsOcr = True` on `bein-almelali-scan.pdf`).
- **Embedding & Retrieval Benchmark:**
  - Implemented `eval/experiments/embed_benchmark.py` adhering to `NormalizeForEmbedding` rules, chunking (147 chunks <= 500 tokens), and batched embedding with `bge-m3`.
  - Made embedding crash-resilient with incremental caching after every batch, `num_thread: 3`, and 0.5s pause to prevent thermal throttling.
  - Computed split-aware retrieval metrics:
    - `dev`: Recall@1: 68.8%, Recall@5: 87.5%, Recall@10: 100.0%, MRR: 0.7616
    - `test`: Recall@1: 75.0%, Recall@5: 91.7%, Recall@10: 94.4%, MRR: 0.8229
    - `all`: Recall@1: 73.1%, Recall@5: 90.4%, Recall@10: 96.2%, MRR: 0.8040
  - Tuned `Rag:MinSimilarity` threshold on `dev` split: recommended `0.650` (or `0.60–0.65`).
  - Documented official Ollama registry unavailability for `multilingual-e5-*` per Golden Rule 6.
- **Token Counter Calibration:**
  - Implemented `eval/experiments/token_calibration.py` querying exact Ollama `prompt_eval_count` on 60 individual sampled chunks.
  - Derived optimal multipliers: `PersianMultiplier = 1.35` (MAPE 6.09%) and `LatinMultiplier = 1.80` (MAPE 12.34%).
- **Documentation & ADR:**
  - Created `eval/experiments/README.md` with one-command run instructions.
  - Created `docs/adr/0002-pdf-extractor-and-embedding-model.md` documenting decisions fixing `vector(1024)` for T03, PyMuPDF for T05, and calibrated multipliers for T06.

## What Was Not Done / Remaining
- None for T01. All phase-0 experiment goals and mandatory refinements are achieved.

## How to Continue
- Next task according to `docs/TASKS.md` is **T01B — Conformance suite & evaluation harness skeleton** (`[PLAN-FIRST]`).
- Goal of T01B: Create pytest conformance test skeleton under `conformance/` (frozen after human approval) and evaluation harness skeleton `eval/run_eval.py`.
- Verification commands for T01:
  ```powershell
  & ".\.venv\Scripts\python.exe" eval/validate_questions.py
  & ".\.venv\Scripts\python.exe" eval/experiments/extract_compare.py
  & ".\.venv\Scripts\python.exe" eval/experiments/embed_benchmark.py
  & ".\.venv\Scripts\python.exe" eval/experiments/token_calibration.py
  ```
