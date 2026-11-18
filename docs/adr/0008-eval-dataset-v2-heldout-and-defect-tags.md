# ADR 0008: Evaluation Dataset v2.1 Schema (`docset` and `text_layer_defect`) and OCR Evaluation Protocol

- **Status:** Accepted (Frozen at git tag `eval-dataset-v2.1`; supersedes `eval-dataset-v2`)
- **Date:** 2026-11-18
- **Tasks:** T12, T19
- **Amends:** `docs/EVAL.md` §1.2–1.3
- **Dataset SHA-256 (`eval/questions.json`, LF-normalized):** `4e739f2d3408da8297be0e724ab8f12f4fa81ffbb95ffe13c061ae3e9d820f84`

## Context

1. Prior to Task T19, `eval/questions.json` contained 63 questions (52 answerable, 11 `not-in-docs`) referencing **11 digital PDFs** in `eval/pdfs/` (`tuned`). A 12th file, `eval/pdfs/ocr/bein-almelali-scan.pdf`, is an 8-page image-only scanned twin of `bein-almelali.pdf` (`8 pages`, `0` extracted characters per page via PyMuPDF). In `eval/questions.json`, `bein-almelali.pdf` has **8 gold questions**: `q008, q009, q010, q020, q021, q029, q030, q037`.
2. Three unseen university regulations (`shiveh-karamoozi.pdf`, `tarh-dakheli.pdf`, `forsat-motaleati.pdf`) were added to `eval/pdfs/` (`heldout`) along with 75 new questions (`q064`–`q138`), bringing the dataset to **138 questions (112 answerable, 26 `not-in-docs`)** across 14 distinct `source_file` PDFs (+1 scanned twin `bein-almelali-scan.pdf` = 15 PDFs total).
3. Six PDFs (`bedoon-factor.pdf`, `dastyar-pazhuheshi.pdf`, `kod-akhlaghi.pdf`, `hamayesh.pdf` in `tuned`, and `tarh-dakheli.pdf`, `forsat-motaleati.pdf` in `heldout`) exhibit PDF font CMap corruption (corrupted digits, merged article numbers, and `"لا" -> "ال"` ligature substitution). Additionally, `ziro-guide.pdf` embeds UI instructions inside screenshots.

## Decisions

1. **Schema Extension (`eval/questions.json`):**
   - `"docset": "tuned" | "heldout"`:
     - `"tuned"`: 74 questions (60 answerable + 14 `not-in-docs`) across the 11 initial digital PDFs.
     - `"heldout"`: 64 questions (52 answerable + 12 `not-in-docs`) across the 3 unseen PDFs.
   - `"text_layer_defect": true | false` **(Objective Data-Driven Question-Impact Definition)**:
     - To eliminate inconsistency between manual document-level tagging (`tuned`) and manual question-level tagging (`heldout`), all 112 answerable questions were audited programmatically: each question's `answer_keywords` were checked against the raw PyMuPDF text of `source_pages` after full normalization (`NFKC`, Arabic→Persian letters `ي/ك/ۀ/ة/أ/إ/آ`, ASCII digits `0-9`, diacritics/ZWNJ removal, lowercase).
     - Exactly **13** questions had ≥1 missing keyword in the raw text layer:
       - **9 CMap-corrupted questions** (`text_layer_defect: true`):
         - `('tuned', 'dastyar-pazhuheshi.pdf')`: **2** (`q011` missing `300`, `q012` missing `100`)
         - `('heldout', 'tarh-dakheli.pdf')`: **3** (`q079` missing `75, 150`; `q080` missing `350`; `q087` missing `30`)
         - `('heldout', 'forsat-motaleati.pdf')`: **4** (`q096` missing `1000, 20`; `q099` missing `30, 16`; `q101` missing `100, 85, درصد`; `q105` missing `تشویقی`)
       - **2 screenshot-embedded-text questions** (`text_layer_defect: true`):
         - `('tuned', 'ziro-guide.pdf')`: **2** (`q034` missing `پیغام, استاد`; `q039` missing `مشارکت`)
       - **2 phrasing/formatting non-defects on clean PDFs** (kept `text_layer_defect: false`):
         - `q054` (`bge-m3-paper.pdf`, keyword `tokens` vs `token` in clean English text) and `q111` (`aein-91.pdf`, keyword `2/5` formatting).
     - Final frozen split on 112 answerable questions: **`11` `text_layer_defect: true`** (4 `tuned`, 7 `heldout`) and **`101` `text_layer_defect: false`** (+26 `not-in-docs`).
     - *Comparison vs. manual v2 draft (27 manual → 11 data-driven):* 6 `tuned` questions (`q014, q022, q023, q028, q032, q038`) had all normalized keywords intact in raw PyMuPDF text and were relabeled `false`; 12 `heldout` questions (`q081`–`q084, q086, q088, q091, q093, q097, q100, q102, q103`) also matched all normalized keywords in raw text (noting that single-digit keywords such as `2, 3, 5, 9` on W-0510 pages can substring-match headers/footers/page numbers even when the answer sentence itself contains CMap digit noise); 2 `ziro-guide.pdf` screenshot questions (`q034, q039`) moved `false -> true`.
2. **Versioning & Freeze Hygiene:**
   - Between `b1c9f6e` and `963a0ec` (`eval-dataset-v2`), `git diff --stat -- eval/questions.json` was empty (LF line-ending normalization only).
   - Because applying the objective 11-question `text_layer_defect` labels modifies `eval/questions.json`, `eval-dataset-v2` is left immutable and a new annotated tag **`eval-dataset-v2.1`** freezes the updated file at the LF SHA-256 recorded above.
3. **Strict Anti-Leakage Tuning and Held-Out Reporting Rule:**
   - Any threshold or hyperparameter calibration in T19 (such as `Rag:MinSimilarity` sweep in hybrid mode) uses **only** `split == "dev" AND docset == "tuned"` (18 answerable + 7 `not-in-docs`).
   - Held-out generalization metrics (Recall@K, MRR, and false-accept rate across 12 `not-in-docs` questions) are reported on **all of `docset == "heldout"`** (52 answerable, 12 `not-in-docs`), alongside the `heldout | test` breakdown.
4. **T12 (OCR) Extraction Trigger and Two-Regime Evaluation Protocol:**
   - T12 triggers OCR during **extraction** (pre-chunking) via `TextDefectDetector.DetectPageDefects` (page-level and document-wide, after NFKC) alongside `PageQualityScorer` (`NeedsOcr`), controlled by `Extraction:OcrMode = Off | Defective | Always` (default `Off`).
   - **Regime A (Defective Text Layer — 11 data-verified questions + full CMap-document breakdown):** Evaluates pre-retrieval keyword recall and retrieval/RAG metrics comparing raw PyMuPDF text (`OcrMode = Off`) vs Tesseract OCR (`OcrMode = Defective`).
   - **Regime B (True Image Scan — 8 questions on `bein-almelali-scan.pdf`):** Uses an explicit eval-harness twin mapping **`bein-almelali-scan.pdf -> bein-almelali.pdf`** (so the hit rule `chunk.fileName == source_file` evaluates hits against the 8 gold questions `q008, q009, q010, q020, q021, q029, q030, q037` with identical 8-page pagination), comparing `OcrMode = Off` (0 text), `OcrMode = Defective` (Tesseract OCR), and the digital reference `bein-almelali.pdf`.
