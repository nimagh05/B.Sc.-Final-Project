# ADR 0008: Evaluation Dataset v2.2 Schema (`docset`, `text_layer_defect`, `defect_mechanism`) and OCR Evaluation Protocol

- **Status:** Accepted (Frozen at git tag `eval-dataset-v2.2`; supersedes `eval-dataset-v2` and `eval-dataset-v2.1`)
- **Date:** 2026-11-19
- **Tasks:** T12, T19
- **Amends:** `docs/EVAL.md` §1.2–1.3
- **Dataset SHA-256 (`eval/questions.json`, LF-normalized):** `71d8573ce418c278a921638cdb9005acb48aa332bc7dae45a90d63bd213da62d`
- **Audit Artifacts:** `eval/audit_text_layer.py`, `eval/text_layer_audit.csv`

## Context

1. Prior to Task T19, `eval/questions.json` contained 63 questions (52 answerable, 11 `not-in-docs`) referencing **11 digital PDFs** in `eval/pdfs/` (`tuned`). A 12th file, `eval/pdfs/ocr/bein-almelali-scan.pdf`, is an 8-page image-only scanned twin of `bein-almelali.pdf` (`8 pages`, `0` extracted characters per page via PyMuPDF). In `eval/questions.json`, `bein-almelali.pdf` has **8 gold questions**: `q008, q009, q010, q020, q021, q029, q030, q037`.
2. Three unseen university regulations (`shiveh-karamoozi.pdf`, `tarh-dakheli.pdf`, `forsat-motaleati.pdf`) were added to `eval/pdfs/` (`heldout`) along with 75 new questions (`q064`–`q138`), bringing the dataset to **138 questions (112 answerable, 26 `not-in-docs`)** across 14 distinct `source_file` PDFs (+1 scanned twin `bein-almelali-scan.pdf` = 15 PDFs total).
3. Using the exact per-page NFKC `TextDefectDetector` criterion (`chars >= 300`, `al >= 10`, and `la == 0` or `la/al < 0.04`), exactly **6 PDFs** in the corpus exhibit CMap/font corruption: `bedoon-factor.pdf`, `dastyar-pazhuheshi.pdf`, `hamayesh.pdf`, `kod-akhlaghi.pdf` (`tuned`), and `tarh-dakheli.pdf`, `forsat-motaleati.pdf` (`heldout`). Note: an earlier document-sum prototype flagged `ziro-guide.pdf` (`la=0, al=90` across 48 pages), but under the exact per-page `chars >= 300` rule `ziro-guide.pdf` has `0` CMap-flagged pages (`doc_cmap = False`); its UI instructions are embedded in raster screenshots rather than corrupted CMap fonts.

## Decisions

1. **Schema Extension (`eval/questions.json`):**
   - `"docset": "tuned" | "heldout"`:
     - `"tuned"`: 74 questions (60 answerable + 14 `not-in-docs`) across the 11 initial digital PDFs.
     - `"heldout"`: 64 questions (52 answerable + 12 `not-in-docs`) across the 3 unseen PDFs.
   - `"defect_mechanism": "none" | "cmap" | "screenshot"` and `"text_layer_defect": true | false` (`text_layer_defect == (defect_mechanism != "none")`):
     - Audited via `eval/audit_text_layer.py` and logged row-by-row in `eval/text_layer_audit.csv`.
     - **Programmatically reproducible subset (`auto_cmap`, 16 questions):** Runs on the 6 per-page `TextDefectDetector`-flagged PDFs and flags a question if normalized `answer_keywords`, full multi-digit tokens from `expected_answer` (strict `(?<!\d)...(?!\d)` boundaries), or number+unit phrases (e.g., `2 سال`, `30 درصد`) are missing from the raw PyMuPDF text of `source_pages`. Counter: `{('tuned', 'dastyar-pazhuheshi.pdf'): 3, ('tuned', 'bedoon-factor.pdf'): 1, ('heldout', 'tarh-dakheli.pdf'): 4, ('heldout', 'forsat-motaleati.pdf'): 8}`. *(Discarded metric note: fuzzy `partial_ratio` against paraphrased `expected_answer` failed calibration [known CMap positives mean `73.6` vs clean `aein-91.pdf` mean `71.9`] and was removed from the audit script and CSV.)*
     - **Union CMap label (`manual OR auto`, 27 questions, `defect_mechanism == "cmap"`):** Combines the 16 programmatically flagged `auto_cmap` questions with **11 visually verified `manual_only` questions** (q023, q028, q032, q038, q082, q083, q084, q086, q088, q091, q093, ~40.7% of union) where article headings or `"لا" -> "ال"` ligatures in the source passage were confirmed corrupted against rendered PDF page images. Explicitly, these 11 `manual_only` labels rely on human visual inspection (`MANUAL_CMAP_V2`) and cannot be reproduced by automated regex alone. Counter: `{('tuned', 'dastyar-pazhuheshi.pdf'): 5, ('tuned', 'bedoon-factor.pdf'): 2, ('tuned', 'kod-akhlaghi.pdf'): 1, ('heldout', 'tarh-dakheli.pdf'): 11, ('heldout', 'forsat-motaleati.pdf'): 8}`.
     - **Unit-phrase-only caveat for T12 keyword recall:** Among `auto_cmap`, 5 questions (q022, q097, q100, q102, q103) have `missing_keywords == ""` and triggered `auto_cmap` solely via number+unit phrase mismatch (which can also arise from spelled-out numbers or spacing). In T12 pre-retrieval text-layer recall reports, strict `answer_keywords` recall and number+unit recall are reported separately, with `q022, q097, q100, q102, q103` broken out explicitly.
     - **Separate Screenshot mechanism (2 questions, `defect_mechanism == "screenshot"`):** `q034, q039` on `ziro-guide.pdf` are isolated under `"defect_mechanism": "screenshot"` and reported separately so screenshot OCR is never conflated with CMap corruption.
     - **Clean answerable (`defect_mechanism == "none"`):** 83 questions (+26 `not-in-docs`).
2. **Versioning & Freeze Hygiene (`eval-dataset-v2.2`):**
   - `eval-dataset-v2` (`963a0ec`) and `eval-dataset-v2.1` (`ca8f51e`) remain untouched.
   - Merged via PR + 3/3 CI checks on `main` and frozen by annotated tag **`eval-dataset-v2.2`** applied to the merged `main` commit. No further modifications to `eval/questions.json` are permitted in T12 or once T19 runs begin.
3. **Strict Anti-Leakage Tuning and Held-Out Reporting Rule:**
   - Any threshold or hyperparameter calibration in T19 (such as `Rag:MinSimilarity` sweep in hybrid mode) uses **only** `split == "dev" AND docset == "tuned"` (18 answerable + 7 `not-in-docs`).
   - Held-out generalization metrics (Recall@K, MRR, and false-accept rate across 12 `not-in-docs` questions) are reported on **all of `docset == "heldout"`** (52 answerable, 12 `not-in-docs`), alongside the `heldout | test` breakdown.
4. **T12 (OCR) Extraction Trigger and Two-Regime Evaluation Protocol:**
   - T12 triggers OCR during **extraction** (pre-chunking) via per-page NFKC `TextDefectDetector.DetectPageDefects` (propagated document-wide for CMap-corrupted documents) alongside `PageQualityScorer` (`NeedsOcr`), controlled by `Extraction:OcrMode = Off | Defective | Always` (default `Off`).
   - **Regime A (CMap/Font-Defective Digital PDFs — Dual Reporting):**
     - **Primary (Document-Level CMap Cohort — 36 answerable questions across 6 PDFs):** All answerable questions across the 6 `TextDefectDetector`-flagged PDFs (`{('tuned', 'dastyar-pazhuheshi.pdf'): 5, ('tuned', 'bedoon-factor.pdf'): 2, ('tuned', 'kod-akhlaghi.pdf'): 1, ('tuned', 'hamayesh.pdf'): 1, ('heldout', 'tarh-dakheli.pdf'): 15, ('heldout', 'forsat-motaleati.pdf'): 12}`). Note: even a partial per-page CMap flag (e.g., page 1 of `bedoon-factor.pdf`, or pages 1–5 of `dastyar-pazhuheshi.pdf`) cohorts the entire document into the Primary document-level cohort because embedded font CMap defects affect document-wide chunking, FTS, and retrieval.
     - **Secondary (Question-Impact Subsets):** Reported alongside Primary for both `defect_mechanism == "cmap"` (**27** union questions) and `auto_cmap` (**16** programmatically flagged questions).
     - **Separate Screenshot Subset:** `defect_mechanism == "screenshot"` (**2** questions on `ziro-guide.pdf`) reported separately.
     - **Clean-Document Regression Guard (`aein-91.pdf`):** T12 also runs OCR (`OcrMode = Always`) on clean `aein-91.pdf` and compares post-OCR vs raw PyMuPDF keyword/number recall to verify OCR does not degrade clean digital pages.
   - **Regime B (True Image Scan — 8 questions on `bein-almelali-scan.pdf`):**
     - **Scan Provenance:** `eval/pdfs/ocr/bein-almelali-scan.pdf` is a synthetic 150 DPI (`1240x1755 px`) rasterized scan of `bein-almelali.pdf` with mild skew and paper-grain texture (`8 pages`, `0` text chars/page). Evaluation reports will explicitly note that synthetic 150 DPI scans represent an optimistic bound relative to degraded physical scans.
     - **Collection Isolation & Twin Mapping:** `bein-almelali-scan.pdf` and `bein-almelali.pdf` must **never** be ingested into the same collection (to prevent SHA-256/content interference and retrieval cross-contamination). Regime B runs in a **separate isolated collection** using an explicit eval-harness twin mapping **`bein-almelali-scan.pdf -> bein-almelali.pdf`** against the 8 gold questions (`q008, q009, q010, q020, q021, q029, q030, q037`), comparing `OcrMode = Off` (0 text), `OcrMode = Defective` (Tesseract OCR), and the digital reference `bein-almelali.pdf`.
