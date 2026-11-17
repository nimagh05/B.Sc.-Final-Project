# ADR 0006: Evaluation Dataset v2 Schema (`docset` and `text_layer_defect`) and OCR Evaluation Protocol

- **Status:** Accepted
- **Date:** 2026-11-16
- **Tasks:** T12, T19

## Context

1. Prior to Task T19, `eval/questions.json` contained 63 questions (52 answerable, 11 `not-in-docs`) drawn from 11 documents used during Phase-0/T06/T11 development. Evaluating only on those documents would not separate generalization to unseen university regulations from potential tuning on the initial corpus.
2. Three additional university regulations (`shiveh-karamoozi.pdf`, `tarh-dakheli.pdf`, `forsat-motaleati.pdf`) were added to `eval/pdfs/` (git-ignored) along with 67 new questions (`q064`–`q130`), bringing the dataset to **130 questions (112 answerable, 18 `not-in-docs`)**.
3. Two of the new held-out documents (`tarh-dakheli.pdf` and `forsat-motaleati.pdf`, W-0510 template family), like `bedoon-factor.pdf` and `dastyar-pazhuheshi.pdf` in the tuned set, suffer from broken PDF font CMap tables (corrupted digits, merged article numbers, and `"لا" -> "ال"` ligature corruption). Ground-truth answers (`expected_answer` and `answer_keywords`) were authored from the rendered page images rather than the broken text layer.

## Decisions

1. **Schema Extension (`eval/questions.json`):**
   - `"docset": "tuned" | "heldout"`: Distinguishes questions on the original 11 documents (`"tuned"`, 74 questions: 60 answerable + 14 `not-in-docs`) from questions on the 3 unseen documents (`"heldout"`, 56 questions: 52 answerable + 4 `not-in-docs`).
   - `"text_layer_defect": true | false`: Marks questions whose source passages or numbers are affected by PDF CMap/font corruption in the raw text layer (36 answerable questions across `bedoon-factor.pdf`, `dastyar-pazhuheshi.pdf`, `tarh-dakheli.pdf`, and `forsat-motaleati.pdf`; 76 clean answerable questions).
2. **Strict Anti-Leakage Tuning Rule:**
   - Any threshold or hyperparameter calibration in T19 (such as `Rag:MinSimilarity` re-sweep in hybrid mode) must use **only** `split == "dev" AND docset == "tuned"`.
   - Held-out generalization metrics are reported on `docset == "heldout" AND split == "test"` (and overall held-out).
3. **T12 (OCR) Trigger and Comparative Evaluation:**
   - `TextDefectDetector` already flags document-wide `CorruptLigatureLamAlef` (`la=0, al=211` on `tarh-dakheli.pdf` and `la=0, al=187` on `forsat-motaleati.pdf`, vs `la=35, al=56` on clean `shiveh-karamoozi.pdf`).
   - Task T12 will trigger OCR fallback when `NeedsOcr` is true **or** when page/document `TextDefectFlags` indicates font/CMap corruption, allowing T19 to report retrieval and end-to-end metrics on `text_layer_defect: true` vs `false` both before and after OCR.
