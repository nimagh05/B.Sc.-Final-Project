# ADR 0008: Evaluation Dataset v2 Schema (`docset` and `text_layer_defect`) and OCR Evaluation Protocol

- **Status:** Accepted (Frozen at git tag `eval-dataset-v2`)
- **Date:** 2026-11-17
- **Tasks:** T12, T19
- **Amends:** `docs/EVAL.md` §1.2–1.3
- **Dataset SHA-256 (`eval/questions.json`, LF-normalized):** `0af239ef41dad3b3174c2ef5934e411ae3c0d854abf51d21a87ea6d929f7113e`

## Context

1. Prior to Task T19, `eval/questions.json` contained 63 questions (52 answerable, 11 `not-in-docs`) referencing **11 digital PDFs** in `eval/pdfs/` (`tuned`). A 12th file, `eval/pdfs/ocr/bein-almelali-scan.pdf`, is an 8-page image-only scanned twin of `bein-almelali.pdf` created in T01 as an OCR fixture (`source_file` in `questions.json` references `bein-almelali.pdf`, which has 8 gold questions: `q005, q006, q013, q021, q025, q030, q035, q037`).
2. Three unseen university regulations (`shiveh-karamoozi.pdf`, `tarh-dakheli.pdf`, `forsat-motaleati.pdf`) were added to `eval/pdfs/` (`heldout`) along with 75 new questions (`q064`–`q138`), bringing the dataset to **138 questions (112 answerable, 26 `not-in-docs`)** across 14 distinct `source_file` PDFs (+1 scanned twin `bein-almelali-scan.pdf` = 15 PDFs total).
3. Six PDFs (`bedoon-factor.pdf`, `dastyar-pazhuheshi.pdf`, `kod-akhlaghi.pdf`, `hamayesh.pdf` in `tuned`, and `tarh-dakheli.pdf`, `forsat-motaleati.pdf` in `heldout`) exhibit PDF font CMap corruption (corrupted digits, merged article numbers, and `"لا" -> "ال"` ligature substitution). Ground-truth answers on the defective documents were verified against rendered page images.

## Decisions

1. **Schema Extension (`eval/questions.json`):**
   - `"docset": "tuned" | "heldout"`:
     - `"tuned"`: 74 questions (60 answerable + 14 `not-in-docs`) across the 11 initial digital PDFs.
     - `"heldout"`: 64 questions (52 answerable + 12 `not-in-docs`) across the 3 unseen PDFs.
   - `"text_layer_defect": true | false` **(Question-Impact Definition)**:
     - Defined at the **question level**: `true` iff the question's gold answer, digits, or `answer_keywords` are degraded by PDF CMap/font corruption in the raw PyMuPDF text layer (**27 answerable questions**), and `false` otherwise (**85 clean answerable questions** + 26 `not-in-docs`).
     - Exact breakdown of `text_layer_defect == true` by `(docset, source_file)` (`Counter` output = 27 total):
       - `('tuned', 'dastyar-pazhuheshi.pdf')`: **5** (`q011, q012, q022, q023, q038`)
       - `('tuned', 'bedoon-factor.pdf')`: **2** (`q014, q032`)
       - `('tuned', 'kod-akhlaghi.pdf')`: **1** (`q028`)
       - `('heldout', 'tarh-dakheli.pdf')`: **11** (`q079, q080, q081, q082, q083, q084, q086, q087, q088, q091, q093`)
       - `('heldout', 'forsat-motaleati.pdf')`: **8** (`q096, q097, q099, q100, q101, q102, q103, q105`)
     - Questions on CMap-defective documents whose specific answer passages rely on spelled-out Persian words without corrupted digits or `"لا" -> "ال"` ligature corruption in any gold keyword/number are tagged `false`:
       - `hamayesh.pdf`: `q033` (1 question; document has CMap ligature defect elsewhere, but `q033` gold keywords are unaffected)
       - `tarh-dakheli.pdf`: `q085, q089, q090, q092` (4 questions; e.g., `"دو سال"` in `q089` is spelled out in letters)
       - `forsat-motaleati.pdf`: `q094, q095, q098, q104` (4 questions; e.g., `"سه"`, `"هفت"`, `"دوازده"` in `q094` are spelled out in letters)
2. **Strict Anti-Leakage Tuning and Held-Out Reporting Rule:**
   - Any threshold or hyperparameter calibration in T19 (such as `Rag:MinSimilarity` sweep in hybrid mode) uses **only** `split == "dev" AND docset == "tuned"` (18 answerable + 7 `not-in-docs`).
   - Because zero questions in `docset == "heldout"` are used for tuning or threshold selection, held-out generalization metrics (Recall@K, MRR, and false-accept rate across 12 `not-in-docs` questions) are reported on **all of `docset == "heldout"`** (52 answerable, 12 `not-in-docs`), alongside the `heldout | test` breakdown.
3. **T12 (OCR) Extraction Trigger and Two-Regime Evaluation Protocol:**
   - Because `ChunkDefectFlags` is computed after chunking, T12 triggers OCR during **extraction** via `TextDefectDetector.DetectPageDefects` (page-level and document-wide) alongside `PageQualityScorer` (`NeedsOcr`), controlled by `Extraction:OcrMode = Off | Defective | Always` (default `Off`).
   - **Regime A (CMap/Font-Defective Digital PDFs — 27 questions):** Evaluates pre-retrieval keyword recall and retrieval/RAG metrics on the 27 `text_layer_defect == true` questions comparing raw PyMuPDF text (`OcrMode = Off`) vs Tesseract OCR (`OcrMode = Defective`).
   - **Regime B (True Image Scan — 8 questions on `bein-almelali-scan.pdf`):** Evaluates `eval/pdfs/ocr/bein-almelali-scan.pdf` (`NeedsOcr = true` on all 8 pages) against the 8 existing gold questions of its digital twin `bein-almelali.pdf` (`q005, q006, q013, q021, q025, q030, q035, q037`), comparing `OcrMode = Off` (0 text), `OcrMode = Defective` (Tesseract OCR), and the digital reference `bein-almelali.pdf`.
