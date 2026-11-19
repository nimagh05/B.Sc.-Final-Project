# ADR 0008: Evaluation Dataset v2.2 Schema (`docset`, `text_layer_defect`, `defect_mechanism`) and OCR Evaluation Protocol

- **Status:** Accepted (Frozen at git tag `eval-dataset-v2.2`; supersedes `eval-dataset-v2` and `eval-dataset-v2.1`)
- **Date:** 2026-11-19
- **Tasks:** T12, T19
- **Amends:** `docs/EVAL.md` §1.2–1.3
- **Dataset SHA-256 (`eval/questions.json`, LF-normalized):** `71d8573ce418c278a921638cdb9005acb48aa332bc7dae45a90d63bd213da62d`
- **Reproducible Audit Artifacts:** `eval/audit_text_layer.py`, `eval/text_layer_audit.csv`

## Context

1. Prior to Task T19, `eval/questions.json` contained 63 questions (52 answerable, 11 `not-in-docs`) referencing **11 digital PDFs** in `eval/pdfs/` (`tuned`). A 12th file, `eval/pdfs/ocr/bein-almelali-scan.pdf`, is an 8-page image-only scanned twin of `bein-almelali.pdf` (`8 pages`, `0` extracted characters per page via PyMuPDF). In `eval/questions.json`, `bein-almelali.pdf` has **8 gold questions**: `q008, q009, q010, q020, q021, q029, q030, q037`.
2. Three unseen university regulations (`shiveh-karamoozi.pdf`, `tarh-dakheli.pdf`, `forsat-motaleati.pdf`) were added to `eval/pdfs/` (`heldout`) along with 75 new questions (`q064`–`q138`), bringing the dataset to **138 questions (112 answerable, 26 `not-in-docs`)** across 14 distinct `source_file` PDFs (+1 scanned twin `bein-almelali-scan.pdf` = 15 PDFs total).
3. Using the exact per-page NFKC `TextDefectDetector` criterion (`chars >= 300`, `al >= 10`, and `la == 0` or `la/al < 0.04`), exactly **6 PDFs** in the corpus exhibit CMap/font corruption: `bedoon-factor.pdf`, `dastyar-pazhuheshi.pdf`, `hamayesh.pdf`, `kod-akhlaghi.pdf` (`tuned`), and `tarh-dakheli.pdf`, `forsat-motaleati.pdf` (`heldout`). Separately, `ziro-guide.pdf` embeds UI instructions inside raster screenshots rather than corrupted font CMaps.

## Decisions

1. **Schema Extension (`eval/questions.json`):**
   - `"docset": "tuned" | "heldout"`:
     - `"tuned"`: 74 questions (60 answerable + 14 `not-in-docs`) across the 11 initial digital PDFs.
     - `"heldout"`: 64 questions (52 answerable + 12 `not-in-docs`) across the 3 unseen PDFs.
   - `"defect_mechanism": "none" | "cmap" | "screenshot"` and `"text_layer_defect": true | false` (`text_layer_defect == (defect_mechanism != "none")`):
     - Reproducibly audited via `eval/audit_text_layer.py` and logged row-by-row in `eval/text_layer_audit.csv` (both committed so labels can be verified even though `eval/pdfs/` is git-ignored).
     - **Automated CMap check (`auto_cmap`, 16 questions):** Runs on the 6 per-page `TextDefectDetector`-flagged PDFs and flags a question if normalized `answer_keywords`, full multi-digit tokens from `expected_answer` (strict `(?<!\d)...(?!\d)` boundaries), or number+unit phrases (e.g., `2 سال`, `30 درصد`) are missing from the raw PyMuPDF text of `source_pages`. Counter: `{('tuned', 'dastyar-pazhuheshi.pdf'): 3, ('tuned', 'bedoon-factor.pdf'): 1, ('heldout', 'tarh-dakheli.pdf'): 4, ('heldout', 'forsat-motaleati.pdf'): 8}`. *(Calibration note: fuzzy `partial_ratio` against paraphrased `expected_answer` did not separate known CMap positives [mean 73.6] from clean `aein-91.pdf` [mean 71.9], so exact full-number and number+unit checks are used instead.)*
     - **Union CMap label (`manual OR auto`, 27 questions, `defect_mechanism == "cmap"`):** Combines the 16 `auto_cmap` questions with 11 visually verified CMap-affected questions (q023, q028, q032, q038, q082, q083, q084, q086, q088, q091, q093) where article headings or `"لا" -> "ال"` ligatures in the source passage are corrupted. Counter: `{('tuned', 'dastyar-pazhuheshi.pdf'): 5, ('tuned', 'bedoon-factor.pdf'): 2, ('tuned', 'kod-akhlaghi.pdf'): 1, ('heldout', 'tarh-dakheli.pdf'): 11, ('heldout', 'forsat-motaleati.pdf'): 8}`.
     - **Separate Screenshot mechanism (2 questions, `defect_mechanism == "screenshot"`):** `q034, q039` on `ziro-guide.pdf` are isolated under `"defect_mechanism": "screenshot"` and reported separately so screenshot OCR is never conflated with CMap corruption.
     - **Clean answerable (`defect_mechanism == "none"`):** 83 questions (+26 `not-in-docs`).
2. **Versioning & Freeze Hygiene (`eval-dataset-v2.2`):**
   - `eval-dataset-v2` (`963a0ec`) and `eval-dataset-v2.1` (`ca8f51e`) remain untouched.
   - This PR freezes the final pre-T19 dataset via PR + 3/3 CI checks and annotated tag **`eval-dataset-v2.2`** at the LF SHA-256 recorded above. No further modifications to `eval/questions.json` are permitted once T19 runs begin.
3. **Strict Anti-Leakage Tuning and Held-Out Reporting Rule:**
   - Any threshold or hyperparameter calibration in T19 (such as `Rag:MinSimilarity` sweep in hybrid mode) uses **only** `split == "dev" AND docset == "tuned"` (18 answerable + 7 `not-in-docs`).
   - Held-out generalization metrics (Recall@K, MRR, and false-accept rate across 12 `not-in-docs` questions) are reported on **all of `docset == "heldout"`** (52 answerable, 12 `not-in-docs`), alongside the `heldout | test` breakdown.
4. **T12 (OCR) Extraction Trigger and Two-Regime Evaluation Protocol:**
   - T12 triggers OCR during **extraction** (pre-chunking) via per-page NFKC `TextDefectDetector.DetectPageDefects` (propagated document-wide for CMap-corrupted documents) alongside `PageQualityScorer` (`NeedsOcr`), controlled by `Extraction:OcrMode = Off | Defective | Always` (default `Off`).
   - **Regime A (CMap/Font-Defective Digital PDFs — Dual Reporting):**
     - **Primary (Document-Level CMap Cohort — 36 answerable questions):** All answerable questions across the 6 `TextDefectDetector`-flagged PDFs (`{('tuned', 'dastyar-pazhuheshi.pdf'): 5, ('tuned', 'bedoon-factor.pdf'): 2, ('tuned', 'kod-akhlaghi.pdf'): 1, ('tuned', 'hamayesh.pdf'): 1, ('heldout', 'tarh-dakheli.pdf'): 15, ('heldout', 'forsat-motaleati.pdf'): 12}`), providing adequate statistical power for retrieval + bootstrap CI because CMap corruption degrades embeddings/FTS/ArticleKeys across the entire document.
     - **Secondary (Question-Impact Subsets):** Reported alongside Primary for both `defect_mechanism == "cmap"` (**27** union questions) and `auto_cmap` (**16** digit/keyword-corrupted questions).
     - **Separate Screenshot Subset:** `defect_mechanism == "screenshot"` (**2** questions on `ziro-guide.pdf`) reported separately.
   - **Regime B (True Image Scan — 8 questions on `bein-almelali-scan.pdf`):**
     - **Scan Provenance:** `eval/pdfs/ocr/bein-almelali-scan.pdf` is a synthetic 150 DPI (`1240x1755 px`) rasterized scan of `bein-almelali.pdf` with mild skew and paper-grain texture (`8 pages`, `0` text chars/page). Evaluation reports will explicitly note that synthetic 150 DPI scans represent an optimistic bound relative to degraded physical scans.
     - **Collection Isolation & Twin Mapping:** `bein-almelali-scan.pdf` and `bein-almelali.pdf` must **never** be ingested into the same collection (to prevent SHA-256/content interference and retrieval cross-contamination). Regime B runs in a **separate isolated collection** using an explicit eval-harness twin mapping **`bein-almelali-scan.pdf -> bein-almelali.pdf`** against the 8 gold questions (`q008, q009, q010, q020, q021, q029, q030, q037`), comparing `OcrMode = Off` (0 text), `OcrMode = Defective` (Tesseract OCR), and the digital reference `bein-almelali.pdf`.
