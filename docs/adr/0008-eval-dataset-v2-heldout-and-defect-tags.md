# ADR 0008: Evaluation Dataset v2 Schema (`docset` and `text_layer_defect`) and OCR Evaluation Protocol

- **Status:** Accepted (Frozen at git tag `eval-dataset-v2`)
- **Date:** 2026-11-17
- **Tasks:** T12, T19
- **Amends:** `docs/EVAL.md` §1.2–1.3
- **Dataset SHA-256 (`eval/questions.json`):** `32b77ee8162eb8aadf9da52878ae13967e2e1f1f8939bac69c9e7868a2f2fb27`

## Context

1. Prior to Task T19, `eval/questions.json` contained 63 questions (52 answerable, 11 `not-in-docs`) drawn from **12 initial documents** (`tuned`: 11 digital PDFs in `eval/pdfs/` plus 1 scanned PDF `eval/pdfs/ocr/bein-almelali-scan.pdf`). Evaluating only on those documents would not separate generalization to unseen university regulations from potential tuning on the initial corpus.
2. Three additional university regulations (`shiveh-karamoozi.pdf`, `tarh-dakheli.pdf`, `forsat-motaleati.pdf`) were added to `eval/pdfs/` (git-ignored) along with 75 new questions (`q064`–`q138`), bringing the corpus to **15 PDFs** and the dataset to **138 questions (112 answerable, 26 `not-in-docs`)**.
3. Six documents across the 15-PDF corpus (`bedoon-factor.pdf`, `dastyar-pazhuheshi.pdf`, `kod-akhlaghi.pdf`, `hamayesh.pdf` in `tuned`, and `tarh-dakheli.pdf`, `forsat-motaleati.pdf` in `heldout`) exhibit PDF font CMap corruption (corrupted digits, merged article numbers, and `"لا" -> "ال"` ligature substitution). Ground-truth answers on the defective documents were verified against rendered page images.

## Decisions

1. **Schema Extension (`eval/questions.json`):**
   - `"docset": "tuned" | "heldout"`:
     - `"tuned"`: 74 questions (60 answerable + 14 `not-in-docs`) across the 12 initial documents.
     - `"heldout"`: 64 questions (52 answerable + 12 `not-in-docs`) across the 3 unseen documents.
   - `"text_layer_defect": true | false` **(Question-Impact Definition)**:
     - Defined at the **question level**: `true` iff the question's gold answer, digits, or `answer_keywords` are degraded by PDF CMap/font corruption in the raw PyMuPDF text layer (**27 answerable questions**: 8 in `tuned` across `bedoon-factor.pdf`, `dastyar-pazhuheshi.pdf`, `kod-akhlaghi.pdf`, and 19 in `heldout` across `tarh-dakheli.pdf` and `forsat-motaleati.pdf`).
     - Questions on CMap-defective documents whose specific answer passages rely on spelled-out Persian words without corrupted digits or `"لا" -> "ال"` ligature corruption in any gold keyword/number remain `false` (e.g., `q033` on `hamayesh.pdf`; `q085`, `q089`, `q090`, `q092` on `tarh-dakheli.pdf`; `q094`, `q095`, `q098`, `q104` on `forsat-motaleati.pdf`), yielding **85 clean answerable questions** + 26 `not-in-docs`.
     - *Relabeling note:* An initial draft mistakenly tagged 9 questions from non-CMap-defective files (`q007`–`q010`, `q019`, `q020`, `q024`, `q034`, `q039`) as `true` (yielding `76 clean / 36 defective`); correcting those 9 tags produced the final frozen split of **`85 clean / 27 defective`**.
2. **Strict Anti-Leakage Tuning and Held-Out Reporting Rule:**
   - Any threshold or hyperparameter calibration in T19 (such as `Rag:MinSimilarity` sweep in hybrid mode) uses **only** `split == "dev" AND docset == "tuned"` (18 answerable + 7 `not-in-docs`).
   - Because zero questions in `docset == "heldout"` are used for tuning or threshold selection, held-out generalization metrics (Recall@K, MRR, and false-accept rate across 12 `not-in-docs` questions) are reported on **all of `docset == "heldout"`** (52 answerable, 12 `not-in-docs`), alongside the `heldout | test` breakdown. (Note: `heldout | dev` contains 13 answerable and 0 `not-in-docs` questions and is never used for threshold tuning.)
3. **T12 (OCR) Extraction Trigger and Comparative Evaluation:**
   - Because `ChunkDefectFlags` is computed after chunking, T12 triggers OCR during **extraction** via `TextDefectDetector.DetectPageDefects` (page-level and document-wide) alongside `PageQualityScorer` (`NeedsOcr`), controlled by `Extraction:OcrMode = Off | Defective | Always` (default `Off` so CI without Tesseract remains green).
   - T19 evaluates both pre-retrieval text-layer keyword recall and full retrieval/end-to-end metrics across `OcrMode = Off` vs `Defective` on `text_layer_defect == true` vs `false`.
