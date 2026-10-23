# 3. Persian Legal-Aware Chunking Strategy, Defect Detection, and Token Calibration

Date: 2026-10-19  
Status: Accepted  

## Context

Task T06 implements document chunking (`IChunker`) and token estimation (`ITokenCounter`) for Persian university regulations, administrative circulars, and academic papers. In Task T01 and ADR 0002, PyMuPDF was selected as the PDF extractor and `bge-m3` (1024 dimensions) was selected as the embedding model.

Key technical requirements and constraints:
1. **CPU Embedding Constraint:** On host CPU, `bge-m3` embedding latency is ~5.5 s per chunk. Big batches or chunks exceeding 512 tokens degrade performance significantly. Therefore, a hard cap of **500 estimated tokens per chunk** is enforced on the complete `EmbeddingText` (including section title, overlap, and body).
2. **Token Multipliers:** Heuristic counting uses $\lceil \text{words} \times M \rceil + \lfloor \text{symbols} / 12 \rfloor$. Multipliers must come from configuration options, not constants. The calibrated P90 values from ADR 0002 Amendment are `PersianMultiplier = 1.45` and `LatinMultiplier = 2.25`.
3. **Legal Document Structure:** Regulations are structured by chapters, articles, notes, and clauses (فصل، ماده، تبصره، بند). Chunks must respect article boundaries where possible, start with their article markers, and maintain section titles (`<chapter> › <article>`).
4. **Text-Layer Defects:** Certain PDFs (`bedoon-factor.pdf`, `dastyar-pazhuheshi.pdf`, `kod-akhlaghi.pdf`, `hamayesh.pdf`) exhibit CMap font defects ("لا" stored as "ال"/"یا") and reversed numbers. Per project rules, text must never be silently mutated; defects must be detected and surfaced as metadata flags.
5. **Evaluation Rigor:** Chunker effectiveness must be benchmarked using production C# code against a fixed-size sentence-accumulating control chunker using identical parameters (target tokens 425, max tokens 500, overlap ratio 0.15, same cleaner and counter).

---

## Decisions

1. **Header/Footer Cleaning (`HeaderFooterCleaner`):**
   - For documents with $\ge 4$ pages, candidate lines in the first 2 and last 2 non-empty lines of each page are normalized (`NormalizeForSearch`) with digits mapped to `#`. Any pattern appearing on $\ge 50\%$ of pages is removed from every page.
   - Standalone page-number lines (`^\s*[#\-–—\s]+\s*$` or `^\s*(صفحه|page)\s*#\s*$`) are stripped **only** among the first/last 2 non-empty lines of a page, never in the middle of a page (protecting table cells and numeric data).
   - Short documents ($< 4$ pages) are left completely untouched.

2. **Token Counter Configuration (`HeuristicTokenCounter`):**
   - Multipliers and symbol divisor are configurable in `ChunkingOptions`:
     `PersianMultiplier = 1.45`, `LatinMultiplier = 2.25`, `SymbolsPerToken = 12`.
   - Words, symbols, and Persian ratio definitions strictly mirror `eval/experiments/token_calibration.py`.
   - The 500 cap applies to `EmbeddingText`. `ChunkDraft.TokenCount` records `Count(EmbeddingText)`.

3. **Legal-Aware Chunking Pipeline (`LegalAwareChunker`):**
   - Options defaults: `TargetTokens = 425`, `MaxTokens = 500`, `MinTokens = 80`, `OverlapRatio = 0.15`, `RespectLegalStructure = true`, `IncludeSectionTitleInEmbedding = true`.
   - Regex markers use `\p{Nd}` for digits, handle `"ماده واحده"`, `"ماده ۵ مکرر"`, and `"ماده (۵)"`, with regex timeouts.
   - Marker lines count as section boundaries only when the preceding line ended with terminal punctuation (`.`, `:`, `؛`, `;`, `؟`, `?`, `!`) or was blank.
   - Hard boundaries occur at articles and chapters. Articles with $< \text{MinTokens}$ are merged with the following article. Tiny trailing chunks are merged into the preceding chunk when token budget allows.
   - Sentence overlap ($\le \text{OverlapRatio} \times \text{TargetTokens}$) occurs strictly within the same section. If the chunk exceeds `MaxTokens`, overlap sentences are dropped first before hard-splitting.

4. **Text-Layer Defect Detection (`TextDefectDetector`):**
   - **Ligature CMap corruption (`CorruptLigatureLamAlef`):** Defect documents in the benchmark set are four (`bedoon-factor.pdf`, `dastyar-pazhuheshi.pdf`, `kod-akhlaghi.pdf`, `hamayesh.pdf`). The page-level statistical ratio of `count("لا") / count("ال")` (requiring $\ge 10$ occurrences of "ال" and ratio $< 0.04$) reliably flags defective pages, but missed `bedoon-factor` p2, `kod-akhlaghi` p2, and `dastyar-pazhuheshi` p6 because those pages had $< 10$ occurrences of "ال". Because CMap defects belong to the document's embedded fonts, `CorruptLigatureLamAlef` is propagated across all chunks of a document when any page triggers it.
   - **Reversed Dates & Years (`SuspectReversedDate`):** Detected via regex for 4-digit reversed solar calendar date patterns (e.g., `dddd/dd/dd` where reversing yields a valid 13xx/14xx Persian year while the original does not). In the 12 benchmark documents, no slash dates exist (`SuspectReversedDate` has no real-data positives). Evidence shows "8931" (reversed 1398) appears 3 times as a bare number in `bedoon-factor.pdf` pages 1–2 (1.txt line 19, 2.txt lines 34 and 40). Isolated reversed numbers cannot be detected. Reversed-year detection and flag persistence are deferred to T09.

5. **Evaluation Protocol & C# Console Tool:**
   - Console tool `eval/experiments/chunker_probe` dumps chunks to JSON for three arms:
     - **Arm (a):** Old baseline (reference only, Recall@5 90.4%, MRR 0.804; Python word-multiplier 1.8).
     - **Arm (b) Control:** Fixed-size sentence-accumulating chunker using production cleaner and counter (425 target / 500 cap).
     - **Arm (c) Legal-Aware:** Production `LegalAwareChunker` with section title in embedding text.
     - **Arm (c2) Legal-Aware (no title):** `LegalAwareChunker` with `IncludeSectionTitleInEmbedding = false`.
   - `eval/experiments/embed_benchmark.py` extended with `--chunks` and SHA-256 text-hash caching.

---

## Benchmark Results & Token Validation

### Header/Footer Cleaner Report on 12 Benchmark PDFs

| Document | Pages | Original Lines | Cleaned Lines | Stripped Lines | Example Stripped Lines |
|---|---|---|---|---|---|
| `aein-91.pdf` | 10 | 632 | 630 | 2 | None |
| `bedoon-factor.pdf` | 2 | 152 | 152 | 0 | None |
| `bein-almelali-scan.pdf` | 8 | 8 | 8 | 0 | None |
| `bein-almelali.pdf` | 8 | 689 | 678 | 11 | `۱`, `۲` |
| `bge-m3-paper.pdf` | 18 | 2543 | 2543 | 0 | None |
| `dastyar-amoozeshi.pdf` | 3 | 175 | 175 | 0 | None |
| `dastyar-pazhuheshi.pdf` | 6 | 700 | 688 | 12 | `W-0510 -09`, `1` |
| `hamayesh.pdf` | 1 | 67 | 67 | 0 | None |
| `kod-akhlaghi.pdf` | 2 | 105 | 105 | 0 | None |
| `rag-lewis-2020.pdf` | 19 | 1342 | 1324 | 18 | `3`, `4` |
| `shoraha-pazhuheshi.pdf` | 5 | 476 | 466 | 10 | `W – ۰۵۱۰ - ۱۴`, `۱` |
| `ziro-guide.pdf` | 48 | 821 | 704 | 117 | `9911`, `راهنمای درخواست اخذ مجوز دفاع...` |

### Text Defect Detector Report on 12 Benchmark PDFs

| Document | Pages | Corrupt Ligature Pages | Reversed Date Pages | Flagged Chunks (Lig/Date) |
|---|---|---|---|---|
| `aein-91.pdf` | 10 | 0 | 0 | 0 / 0 |
| `bedoon-factor.pdf` | 2 | 1 | 0 | 2 / 0 |
| `bein-almelali-scan.pdf` | 8 | 0 | 0 | 0 / 0 |
| `bein-almelali.pdf` | 8 | 0 | 0 | 0 / 0 |
| `bge-m3-paper.pdf` | 18 | 0 | 0 | 0 / 0 |
| `dastyar-amoozeshi.pdf` | 3 | 0 | 0 | 0 / 0 |
| `dastyar-pazhuheshi.pdf` | 6 | 5 | 0 | 10 / 0 |
| `hamayesh.pdf` | 1 | 1 | 0 | 1 / 0 |
| `kod-akhlaghi.pdf` | 2 | 1 | 0 | 2 / 0 |
| `rag-lewis-2020.pdf` | 19 | 0 | 0 | 0 / 0 |
| `shoraha-pazhuheshi.pdf` | 5 | 0 | 0 | 0 / 0 |
| `ziro-guide.pdf` | 48 | 0 | 0 | 0 / 0 |

### Chunker Statistics (from `eval/experiments/chunker_probe`)

| Arm | Chunk Count | Min Tokens | Max Tokens | Mean Tokens | Median Tokens | P90 Tokens | Mean/Max Pages | Chunks >= 4p | Estimated > 500 |
|---|---|---|---|---|---|---|---|---|---|
| **(a) Baseline (T01)** | 147 | - | 499 | 378.2 | 389.0 | - | 1.12 / - | - | 0 (0.0%) |
| **(b) Control Chunker** | 177 | 107 | 485 | 411.6 | 426.0 | 472.0 | 1.56 / 14 | 5 (2.82%) | 0 (0.0%) |
| **(c) LegalAwareChunker** | 207 | 82 | 485 | 348.8 | 406.0 | 468.8 | 1.43 / 9 | 6 (2.90%) | 0 (0.0%) |
| **(c2) Legal (No Title)** | 206 | 79 | 485 | 350.0 | 408.5 | 469.0 | 1.44 / 9 | 6 (2.91%) | 0 (0.0%) |

### Real-Token Validation (Ollama `bge-m3` on CPU)

Sample of 45 produced chunks (30 Persian, 15 Latin) evaluated via Ollama `/api/embed` (`prompt_eval_count - 2`):
- **Persian:** 30 chunks, 0 exceeded 500 real tokens (0.0%)
- **Latin:** 15 chunks, 0 exceeded 500 real tokens (0.0%)
- **Overall:** 45 chunks, 0 exceeded 500 real tokens (0.0%)
- **Result:** Cap strictly respected (well under the $\le 10\%$ threshold). Detailed record saved in `eval/experiments/out/real_token_validation.csv`.

### Retrieval Benchmark

| Arm | Split | Q Count | Recall@1 | Recall@3 | Recall@5 | Recall@10 | MRR |
|---|---|---|---|---|---|---|---|
| **(a) Baseline (T01)** | dev | 16 | 68.8% | 75.0% | 87.5% | 100.0% | 0.7616 |
| | test | 36 | 75.0% | 86.1% | 91.7% | 94.4% | 0.8229 |
| | **all** | **52** | **73.1%** | **82.7%** | **90.4%** | **96.2%** | **0.8040** |
| **(b) Control Chunker** | dev | 16 | 81.2% | 93.8% | 93.8% | 93.8% | 0.8807 |
| | test | 36 | 66.7% | 88.9% | 91.7% | 100.0% | 0.7832 |
| | **all** | **52** | **71.2%** | **90.4%** | **92.3%** | **98.1%** | **0.8132** |
| **(c) LegalAwareChunker** | dev | 16 | 81.2% | 93.8% | 93.8% | 93.8% | 0.8807 |
| | test | 36 | 72.2% | 88.9% | 88.9% | 94.4% | 0.8065 |
| | **all** | **52** | **75.0%** | **90.4%** | **90.4%** | **94.2%** | **0.8293** |
| **(c2) Legal (No Title)** | dev | 16 | 81.2% | 93.8% | 93.8% | 93.8% | 0.8703 |
| | test | 36 | 69.4% | 88.9% | 88.9% | 94.4% | 0.7970 |
| | **all** | **52** | **73.1%** | **90.4%** | **90.4%** | **94.2%** | **0.8195** |

#### Strict Hit Rule (`page_span <= 3`, All 52 Answerable Questions)

| Arm | Strict Recall@1 | Strict Recall@3 | Strict Recall@5 | Strict Recall@10 | Strict MRR |
|---|---|---|---|---|---|
| **(b) Control Chunker** | 71.2% | 86.5% | 88.5% | 94.2% | 0.7972 |
| **(c) LegalAwareChunker** | **75.0%** | **88.5%** | **88.5%** | 92.3% | **0.8234** |
| **(c2) Legal (No Title)** | 73.1% | **88.5%** | **88.5%** | 92.3% | 0.8137 |

### Retrieval results (52 answerable questions, bge-m3, human run 2026-10-19)
See tables above. Paired bootstrap on reciprocal rank (loose rule): legal vs control dMRR +0.0161
(95% CI -0.0493..+0.0833); legal vs no-title +0.0098 (-0.0225..+0.0486); control vs no-title
+0.0063 (-0.0562..+0.0705). Under the strict rule (`span <= 3`): legal vs control dMRR +0.0262
(95% CI -0.0362..+0.0904; hit@1 5 vs 3, hit@3 1 vs 0, hit@5 0 vs 0, hit@10 0 vs 1; counts are
legal-only vs control-only hits); legal vs no-title +0.0096 (-0.0227..+0.0484). No difference is
statistically significant; with 52 questions the smallest detectable dMRR is about 0.07.
- q016 (rank 6) and q050 (rank 8) improved to 1 and 2 already in the control arm: the gain
  over the T01 baseline comes from the shared pipeline (cleaner, token counter, sizes), not
  from legal-structure awareness.
- q034 regressed in the legal arms under the loose page-overlap rule (rank 2 -> 33/28): the
  legal chunk covering p.16 has only 87 tokens (span 3, pages 15-17, about Section 9 checklist),
  while the control chunk spans pages 7-16 (10 pages, 467 tokens). A pattern search
  (`رد ` / `عدم تائید` / `تائید نکنند` / `پیشنهاد`) on the extracted text layer of `ziro-guide.pdf`
  found no passage about proposal rejection (instructions are in screenshots or the gold label is
  under review). Under the strict hit rule (`span <= 3`), the 10-page control chunk is
  disqualified (`strict_rank = -1`), eliminating control's Recall@5 edge (both 88.5%).
  Sensitivity analysis (post-hoc, excluding q034, n=51): loose MRR is 0.8193 (control) vs 0.8450
  (legal), dMRR +0.0257 (95% CI -0.0381..+0.0910); strict MRR is 0.8128 vs 0.8389, dMRR +0.0262
  (95% CI -0.0381..+0.0915). Page spans per chunk: control mean 1.56 / max 14 (2.82% >= 4p),
  legal mean 1.43 / max 9 (2.90% >= 4p).
- Sparse visual guides (`ziro-guide.pdf`): Both `ziro-guide.pdf` questions (`q034` and `q039`)
  only hit in the top ranks via 8-to-10-page chunks (`q039` strict rank is 40 in legal and unhit
  in control) because screenshot-heavy pages have very little text per page. Future work (T06b/T09):
  consider a `MaxPagesPerChunk` option (e.g., 3) to close chunks when crossing page-span limits.
- Rank changes control -> legal (10 of 52 questions changed rank): better q004 (2->1), q010 (2->1),
  q024 (3->1), q026 (3->1), q031 (4->1); worse q034 (2->33), q015 (6->17), q005 (1->3), q020 (1->2),
  q035 (1->2). Legal-reference questions are mixed (q024 better, q020 and q015 worse), so the data
  do not show a gain from article-aware boundaries on that question type.
- On q015 (rank 6 -> 17) the legal chunk `aein-91.pdf_7` (p.4, 284 tokens) contains Article 7 with
  Notes 1 and 2 and is correctly isolated, yet ranks 17. Tested hypothesis: inserting spaces between letters and digits (`"ماده7"` -> `"ماده 7"`) only changed cosine similarity with `aein-91.pdf_7` from 0.5106 to 0.5161 (vs top-1 0.6109), refuting letter-digit spacing as the cause for dense retrieval. Dense
  retrieval is also known to be weak at exact article-number references; T11 (hybrid retrieval with
  reference boost) is the planned remedy, not chunking.
- Dev top-1 similarity (answerable vs not-in-docs, n=16 vs 4): control 0.667/0.649, legal
  0.679/0.632, legal no-title 0.679/0.640. Rag:MinSimilarity is not changed in this task.

Decision: LegalAwareChunker with section title stays the default. It is not measurably better than
the control (strict dMRR +0.026, 95% CI includes 0) and costs +17% chunks; it is kept because it is
not worse within noise and provides section titles/boundaries for display and citation. A
citation-precision advantage is NOT demonstrated: both arms have a similar share of chunks spanning
>= 4 pages (2.82% vs 2.90%). The T19 evaluation needs more questions (>= 100 answerable) to resolve
differences below 0.07 MRR.

### Diagnostic Questions Performance

| Question ID | Target Document | Query Type | Arm (a) Rank | Arm (b) Control | Arm (c) Legal | Arm (c2) No-Title |
|---|---|---|---|---|---|---|
| `q016` | `aein-91.pdf` (p. 5) | Legal Reference (ماده ۸) | 6 | 1 | 1 | 1 |
| `q034` | `ziro-guide.pdf` (p. 16) | Paraphrase | 12 | 2 | 33 | 28 |
| `q050` | `rag-lewis-2020.pdf` (p. 1) | English Technical | 8 | 2 | 2 | 2 |
| `q055` | `rag-lewis-2020.pdf` (p. 1) | English Technical | 11 | 9 | 9 | 9 |
| `q056` | `bge-m3-paper.pdf` (p. 5) | English Technical | 9 | 11 | 11 | 11 |

---

## Consequences

- `DocSearch.Application` exposes `ChunkDefectFlags` and includes `DefectFlags` on `ChunkDraft`.
- Default configuration uses `TargetTokens = 425` and `MaxTokens = 500`.
- Chunks never exceed 500 estimated tokens in `EmbeddingText`.
- Subsequent ingestion in T09 will record defect warnings when `ChunkDefectFlags` is non-zero.
