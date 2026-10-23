# Task T06 Handoff Note — Persian Legal-Aware Chunker, Token Counter, and Chunker Probe

**Agent:** Antigravity  
**Date:** 2026-10-20  
**Branch:** `feature/T06-chunker`  
**Status:** Task complete. All 134 unit and parity tests passing. Strict hit evaluation rule and page span metrics added to `embed_benchmark.py`. Defect flags propagated document-wide. Real-token validation 100% compliant (0/45 over 500). Human benchmark run results verified and recorded in ADR 0003. Ready for merge.

---

## 1. What was done

1. **ChunkDraft & ChunkingOptions Contracts:**
   - Extended `ChunkDraft` with `ChunkDefectFlags DefectFlags` (values: `None`, `CorruptLigatureLamAlef`, `SuspectReversedDate`).
   - Updated `ChunkingOptions` with defaults: `TargetTokens = 425`, `MaxTokens = 500`, `MinTokens = 80`, `OverlapRatio = 0.15`, `RespectLegalStructure = true`, `IncludeSectionTitleInEmbedding = true`, `PersianMultiplier = 1.45`, `LatinMultiplier = 2.25`, `SymbolsPerToken = 12`.
   - Updated validation rules (`TargetTokens <= MaxTokens`, multipliers > 0).

2. **HeaderFooterCleaner:**
   - Implemented `src/DocSearch.Application/Chunking/HeaderFooterCleaner.cs`.
   - Cleans repeating top-2 and bottom-2 lines occurring on $\ge 50\%$ of pages for documents with $\ge 4$ pages.
   - Strips standalone page numbers strictly from the top 2 / bottom 2 lines, protecting numeric data in tables.
   - Unit tests: `tests/DocSearch.Application.Tests/HeaderFooterCleanerTests.cs` (6/6 passing).

3. **HeuristicTokenCounter & Parity Tests:**
   - Implemented `src/DocSearch.Application/Chunking/HeuristicTokenCounter.cs` using options-based multipliers and definitions matching `eval/experiments/token_calibration.py`.
   - Unit and parity tests: `tests/DocSearch.Application.Tests/HeuristicTokenCounterTests.cs` (6/6 passing, including `[SkippableFact] Parity_With_TokenCalibrationCsv` testing all 60 calibration rows with 93.3% coverage against `bge-m3`).

4. **TextDefectDetector & Document-Wide Propagation:**
   - Implemented `src/DocSearch.Application/Chunking/TextDefectDetector.cs`.
   - Calibrated ligature corruption threshold on the 12 extracted PDFs (`chars >= 300 && alCount >= 10 && (laCount == 0 || la/al < 0.04)`), detecting defect PDFs (`bedoon-factor.pdf`, `dastyar-pazhuheshi.pdf`, `kod-akhlaghi.pdf`, `hamayesh.pdf`).
   - In `LegalAwareChunker`, if any page of a document triggers `CorruptLigatureLamAlef`, that flag is OR'd into all chunks of the document (because font CMap defects belong to the document's embedded fonts).
   - Unit tests: `tests/DocSearch.Application.Tests/TextDefectDetectorTests.cs` (5/5 passing) and `LegalAwareChunkerTests` (15/15 passing).
   - Documented in ADR 0003: bare reversed years (e.g. `"8931"` in `bedoon-factor.pdf`) and defect persistence are deferred to T09.

5. **LegalAwareChunker:**
   - Implemented `src/DocSearch.Application/Chunking/LegalStructureMarkers.cs` with compiled regexes, `\p{Nd}`, "ماده واحده", "ماده ۵ مکرر", "ماده (۵)".
   - Implemented `src/DocSearch.Application/Chunking/LegalAwareChunker.cs`:
     - Hard boundaries at chapters and articles; section titles (`<chapter> › <article>`) prefixed to `EmbeddingText`.
     - 500 estimated token cap strictly enforced on `EmbeddingText` (title + overlap + body) by shedding overlap sentences first.
     - Budgets for title tokens during sentence packing so body text is never truncated.
     - Merges small last chunks ($< \text{MinTokens}$) when token budget permits.

6. **DI Registration & AppSettings:**
   - Registered `HeaderFooterCleaner`, `ITokenCounter` (`HeuristicTokenCounter`), and `IChunker` (`LegalAwareChunker`) in `DependencyInjection.cs`.
   - Added chunking defaults to `appsettings.json`.

7. **Evaluation Harness & Strict Hit Rule:**
   - Extended `eval/experiments/embed_benchmark.py`:
     - Added strict hit rule: `pdf matches && gold page in [page_start, page_end] && span <= 3`.
     - Added `first_hit_span` and `rank_first_hit_strict` to CSV and markdown reports.
     - Reports percentage of chunks spanning $\ge 4$ pages per arm.
   - Ran `chunker_probe` with 45-chunk real-token validation against Ollama `bge-m3` on CPU (0.0% over 500).

8. **ADR 0003 Finalized:**
   - Updated ADR 0003 with the verified benchmark numbers across all splits and the paired bootstrap statistical summary block.

---

## 2. Benchmark Summary (Human Run 2026-10-19)

| Arm | Chunk Count | dev MRR | test MRR | all MRR | Recall@1 | Recall@3 | Recall@5 | Recall@10 |
|---|---|---|---|---|---|---|---|---|
| **(a) Baseline (T01)** | 147 | 0.7616 | 0.8229 | 0.8040 | 73.1% | 82.7% | 90.4% | 96.2% |
| **(b) Control Chunker** | 177 | 0.8807 | 0.7832 | 0.8132 | 71.2% | 90.4% | 92.3% | 98.1% |
| **(c) LegalAwareChunker** | 207 | 0.8807 | 0.8065 | 0.8293 | 75.0% | 90.4% | 90.4% | 94.2% |
| **(c2) Legal (No Title)** | 206 | 0.8703 | 0.7970 | 0.8195 | 73.1% | 90.4% | 90.4% | 94.2% |

Paired bootstrap on reciprocal rank: legal vs control dMRR +0.016 (95% CI -0.049..+0.083); legal vs no-title +0.010 (-0.023..+0.049); control vs no-title +0.006 (-0.056..+0.071). Retain `LegalAwareChunker` with title as production default.

---

## 3. Recommended Next Task

T07 - Embedding provider (see docs/TASKS.md, section T07), followed by T08 - Vector store & retrieval (vector-only) and T09 - Upload & ingestion pipeline.
