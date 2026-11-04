# 4. Post-T06b Chunker Boundary Fix, Benchmark Re-Evaluation, and RAG Hardening

Date: 2026-11-04
Status: Accepted
Amends: ADR 0003 (Persian Legal-Aware Chunking Strategy, Defect Detection, and Token Calibration)

## Context

During the post-T10 / T06b review, two structural boundary bugs were identified in `LegalAwareChunker` alongside edge-case hardening requirements in `RagService` and `Program.cs`:

1. **Marker Capture Timing (`FlushParagraph`):** In the initial T06 implementation, `currentChapter`, `currentArticle`, and `currentHeading` were mutated before calling `FlushParagraph()` when a structure marker line immediately followed a line ending with terminal punctuation (with no blank line in between, which is standard in PyMuPDF output). This caused the preceding paragraph to be stamped with the *next* article's title (e.g., in `aein-91.pdf`, the chunk containing Article 8's body was titled `"ماده9"`).
2. **Article Transition Comparison in `PackChunks` (`isNewArticle` / `isNewChapter`):** `PackChunks` compared `unit.Article` against `current.Article` (the first unit of the chunk) instead of the immediately preceding unit (`units[i - 1].Article`). Whenever a short article (`< MinTokens = 80`) merged with the following article, `current.Article` remained the first article's label, causing every subsequent paragraph start inside the second article to evaluate `isNewArticle = true` and trigger a premature mid-article split as soon as `EstimatedTokens >= MinTokens`. Similarly, overlap `sameSection` compared against `prevChunk.Article` rather than `prevChunk.Units[^1].Article`.
3. **RAG & API Hardening:**
   - `RagService.IsModelRefusal` needed to strip citation brackets and the core refusal phrase, classifying a response as a refusal only when the remaining trimmed text has length `<= RagConstants.MaxRefusalRemainderLength` (15), so two-part answers that answer part 1 and refuse part 2 are preserved.
   - Empty/whitespace LLM responses and `ChatFinishReason.Length` truncations required explicit warning logs and degraded-mode handling, with `AiOptions.Chat.MaxOutputTokens` raised from 800 (in `SPEC-DATA-AND-API.md` §1.4) to `1024`.
   - `request.ConversationId` ownership validation in `RagService.AskAsync` needed to execute immediately after collection ACL checks (pre-embedding and pre-LLM) rather than inside `CompleteAndPersistAsync`.
   - `JsonStringEnumConverter` needed registration on both MVC (`AddJsonOptions`) and Minimal/OpenAPI (`ConfigureHttpJsonOptions`).

Because the `FlushParagraph` timing fix and `prevUnit` transition fix alter chunk boundaries and `EmbeddingText` in Arm (c) `LegalAwareChunker` and Arm (c2) `Legal (No Title)`, the offline embedding benchmark from ADR 0003 was re-executed to keep thesis evaluation tables traceable.

---

## Decisions

1. **Chunker Fixes (`LegalAwareChunker`):**
   - `FlushParagraph()` is invoked strictly *before* updating `currentChapter`, `currentArticle`, and `currentHeading` on marker lines.
   - In `PackChunks`, `isNewArticle` and `isNewChapter` compare `unit.Article` and `unit.Chapter` against `prevUnit = i > 0 ? units[i - 1] : null`.
   - When a chapter-only chunk (`string.IsNullOrEmpty(current.Article)`) merges with its first article unit, `current.Article` and `current.SectionTitle` upgrade to `"<chapter> › <article>"`. When an article chunk (`< MinTokens`) merges with a subsequent article, the first article's `SectionTitle` is retained.
   - Overlap `sameSection` compares against `prevChunk.Units[^1].Article` and `prevChunk.Units[^1].Chapter`.
2. **RAG & Ingestion Hardening:**
   - Pre-LLM conversation ownership check in `RagService.AskAsync`; `MaxRefusalRemainderLength = 15` in `RagConstants`; `AskTimings(EmbedRetrieveMs, LlmMs)` record struct; `ChatFinishReason.Length` truncation warning; `MaxOutputTokens = 1024`.
   - Structured warning logs in `IngestionService` when any page has `LooksReversed == true` or any chunk has `DefectFlags != ChunkDefectFlags.None` (logging `DocumentId`, page numbers, and flags without document text).

---

## Re-Evaluated Benchmark Results (Post-T06b)

### 1. Chunker Statistics (12 Benchmark PDFs)

Eliminating premature mid-article splits reduced total chunks in the legal arms from 207/206 to **201**, increased mean chunk size from 348.8 to **359.7 tokens**, and reduced chunks spanning `>= 4` pages from 6 (2.90%) to **5 (2.49%)**:

| Arm | Chunk Count | Min Tokens | Max Tokens | Mean Tokens | Median Tokens | P90 Tokens | Mean/Max Pages | Chunks >= 4p | Estimated > 500 | Real > 500 (n=45) |
|---|---|---|---|---|---|---|---|---|---|---|
| **(b) Control Chunker** | 177 | 107 | 485 | 411.6 | 426.0 | 472.0 | 1.56 / 14 | 5 (2.82%) | 0 (0.0%) | 0/45 (0.0%) |
| **(c) Legal (T06 Pre-Fix)** | 207 | 82 | 485 | 348.8 | 406.0 | 468.8 | 1.43 / 9 | 6 (2.90%) | 0 (0.0%) | 0/45 (0.0%) |
| **(c) Legal (T06b Post-Fix)** | **201** | 82 | 485 | **359.7** | **411.0** | 468.0 | 1.47 / 13 | **5 (2.49%)** | 0 (0.0%) | **0/45 (0.0%)** |
| **(c2) No-Title (T06b Post-Fix)** | **201** | 79 | 485 | **359.1** | **411.0** | 468.0 | 1.47 / 13 | **5 (2.49%)** | 0 (0.0%) | — |

### 2. Retrieval Benchmark (52 Answerable Questions, `bge-m3`)

#### Loose Hit Rule (Any Page Overlap)

| Arm | Split | Q Count | Recall@1 | Recall@3 | Recall@5 | Recall@10 | MRR |
|---|---|---|---|---|---|---|---|
| **(a) Baseline (T01)** | all | 52 | 73.1% | 82.7% | 90.4% | 96.2% | 0.8040 |
| **(b) Control Chunker** | dev / test / **all** | 16 / 36 / **52** | 81.2% / 66.7% / **71.2%** | 93.8% / 88.9% / **90.4%** | 93.8% / 91.7% / **92.3%** | 93.8% / 100.0% / **98.1%** | 0.8807 / 0.7832 / **0.8132** |
| **(c) Legal (T06 Pre-Fix)** | dev / test / **all** | 16 / 36 / **52** | 81.2% / 72.2% / **75.0%** | 93.8% / 88.9% / **90.4%** | 93.8% / 88.9% / **90.4%** | 93.8% / 94.4% / **94.2%** | 0.8807 / 0.8065 / **0.8293** |
| **(c) Legal (T06b Post-Fix)** | dev / test / **all** | 16 / 36 / **52** | 81.2% / 72.2% / **75.0%** | 87.5% / 88.9% / **88.5%** | 93.8% / 88.9% / **90.4%** | 93.8% / 94.4% / **94.2%** | 0.8651 / **0.8118** / **0.8282** |
| **(c2) No-Title (T06b Post-Fix)** | dev / test / **all** | 16 / 36 / **52** | 75.0% / 72.2% / **73.1%** | 87.5% / 88.9% / **88.5%** | 93.8% / 88.9% / **90.4%** | 93.8% / 94.4% / **94.2%** | 0.8338 / **0.8120** / **0.8187** |

#### Strict Hit Rule (`page_span <= 3`, All 52 Answerable Questions)

| Arm | dev Strict MRR | test Strict MRR | Strict Recall@1 | Strict Recall@3 | Strict Recall@5 | Strict Recall@10 | All Strict MRR |
|---|---|---|---|---|---|---|---|
| **(b) Control Chunker** | 0.8807 | 0.7600 | 71.2% | 86.5% | 88.5% | 94.2% | 0.7972 |
| **(c) Legal (T06 Pre-Fix)** | 0.8807 | 0.7979 | 75.0% | 88.5% | 88.5% | 92.3% | 0.8234 |
| **(c) Legal (T06b Post-Fix)** | 0.8651 | **0.8032** | **75.0%** | **86.5%** | **88.5%** | 92.3% | **0.8222** |
| **(c2) No-Title (T06b Post-Fix)** | 0.8338 | **0.8038** | 73.1% | **86.5%** | **88.5%** | 92.3% | 0.8130 |

### 3. Paired Bootstrap & Diagnostic Findings (5000 resamples, n=52)

- **Pre-Fix vs Post-Fix Legal (`legal_pre_fix` vs `legal`):**
  - Loose: `hit@1` 1 vs 1, `hit@3` 1 vs 0, `hit@5` 0 vs 0, `hit@10` 0 vs 0; `mean dRR = -0.0012` (`95% CI = [-0.0304, +0.0277]`).
  - Strict (`span <= 3`): `mean dRR = -0.0012` (`95% CI = [-0.0305, +0.0277]`).
  - Test split MRR improved from `0.8065` to `0.8118` (strict: `0.7979` to `0.8032`), while overall MRR (`0.8282` vs `0.8293`) is virtually identical.
- **Control vs Post-Fix Legal (`control` vs `legal`):**
  - Loose: `hit@1` only control=3, only legal=5; `mean dRR = +0.0150` (`95% CI = [-0.0513, +0.0833]`).
  - Strict (`span <= 3`): `hit@1` only control=3, only legal=5; `hit@3` 1 vs 1; `hit@5` 0 vs 0; `hit@10` 1 vs 0; `mean dRR = +0.0251` (`95% CI = [-0.0387, +0.0918]`).
- **No-Title vs With-Title Post-Fix (`legal_notitle` vs `legal`):**
  - Loose: `hit@1` only no-title=1, only legal=2; `hit@3/5/10` 0 vs 0; `mean dRR = +0.0095` (`95% CI = [-0.0194, +0.0385]`).
  - Strict (`span <= 3`): `mean dRR = +0.0092` (`95% CI = [-0.0199, +0.0385]`).
- **Diagnostic Question `q016` (`aein-91.pdf`, Article 8):**
  - Fixing the marker timing bug assigned `"ماده8"` (instead of `"ماده9"`) to `aein-91.pdf_6` (p.5). Consequently, `q016` remains at Rank 1 and its Top-1 similarity increased from `0.6593` (pre-fix) to **`0.6980`** (post-fix), compared to `0.5311` in `control` (which would be false-rejected by `MinSimilarity = 0.55`) and `0.6742` in `legal_notitle`.
- **Dev Split Top-1 Similarity Distribution (`legal` post-fix):**
  - **Answerable (n=16):** `Mean = 0.6707`, `Std = 0.0531`, `Min = 0.5902`, `Max = 0.7688`, `Median = 0.6753`.
  - **Not-in-docs (n=4):** `Mean = 0.6464`, `Std = 0.0403`, `Min = 0.5793`, `Max = 0.6859`, `Median = 0.6601`.
  - `Rag:MinSimilarity` remains `0.55` per ADR 0002 (all 16 dev answerable questions score `>= 0.5902`, well above `0.55`).
- **Implications for Task T11 (Hybrid Retrieval & Reference Boost):**
  - Inspection of `aein-91.pdf` chunks confirms that `section_title` is stored in Display form and often lacks whitespace before digits in raw PDF text (`"ماده1"`, `"ماده8"`, `"ماده19"`), while `search_text` has ASCII digits. Therefore, T11's Reference Boost (§6.4) must normalize digits and match optional whitespace (`ماده\s*<N>`) across both `section_title` and the first 120 characters of `search_text`.
