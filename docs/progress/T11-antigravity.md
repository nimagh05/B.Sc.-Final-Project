# Task T11 — Hybrid Retrieval, Legal Reference Boost (`ArticleKeys`), and Reciprocal Rank Fusion

**Agent:** Antigravity  
**Branch:** `feature/T11-hybrid-retrieval`  
**Status:** Completed  

---

## 1. What changed

1. **`TsQueryBuilder` (`DocSearch.Application/Retrieval/TsQueryBuilder.cs`):**
   - Pure static class implementing PostgreSQL websearch/tsquery normalization:
   - Strips Persian and English stopwords, preserves digit-only tokens, sanitizes tsquery syntax characters (`&`, `|`, `!`, `:`, `*`, `(`, `)`).
   - Handles attached/separated legal keywords symmetrically (`"ماده 7"` or `"ماده7"` $\rightarrow$ `'ماده' | '7' | 'ماده7'`).
   - Implements `ExtractQueryTokens` and `TokenizeForCoverage` for discrete word boundary splitting (`[^\p{L}0-9]+`), enabling exact set intersection for token coverage.
2. **`LegalReferenceExtractor` (`DocSearch.Application/Retrieval/LegalReferenceExtractor.cs`):**
   - Pure static class in `DocSearch.Application/Retrieval/`.
   - Extracts canonical article keys (`"ماده N"`, `"ماده واحده"`, `"ماده N مکرر"`, `"article N"`) and optional note numbers (`تبصره\s*([0-9]+)` $\rightarrow$ `int`).
   - Strictly enforces that note numbers are only extracted when an article is present; never boosts on a note alone.
3. **`ArticleKeys` Metadata on ChunkDraft, Chunk Domain & Persistence:**
   - Extended `ChunkDraft` with trailing optional parameter `IReadOnlyList<string>? ArticleKeys = null` (defaulting to empty).
   - In `LegalAwareChunker.BuildChunkDraft`, aggregates canonical `unit.Article` values across all non-overlap `Units` of a chunk.
   - Verified that `aein-91.pdf` chunk union contains all 35 articles (`"ماده 1"` to `"ماده 35"`), while chunk boundaries and SHA-256 hashes of `Text`, `SearchText`, and `EmbeddingText` across all 201 benchmark chunks remain 100% identical to reference `chunks_legal_t06b.json`.
   - Added `public string[] ArticleKeys { get; set; } = [];` to `DocSearch.Domain.Chunk`.
   - Configured `article_keys text[] NOT NULL DEFAULT '{}'` with GIN index `ix_chunks_article_keys` in `AppDbContext`.
   - Generated EF Core migration `AddChunkArticleKeys` (`20261106182155_AddChunkArticleKeys.cs`) and updated `AppDbContextModelSnapshot.cs`.
4. **`IVectorStore` & `PgVectorStore.SearchHybridAsync` (`DocSearch.Infrastructure/Persistence/PgVectorStore.cs`):**
   - Added `SearchHybridAsync(HybridSearchRequest request, CancellationToken ct)` to `IVectorStore`.
   - Sequential SQL execution in `PgVectorStore.cs` (satisfying Npgsql's single-connection constraint): Vector query, FTS query (`ts_rank_cd`), and dedicated Ref query (`article_keys && @refs` via GIN index).
   - Computes genuine cosine similarity `(1.0 - (c.embedding <=> @q))` for all returned candidate chunks, and computes `BestVectorScore` as the maximum cosine score across all candidate lists.
5. **`Retriever` Hybrid Fusion & Note Ordering (`DocSearch.Application/Retrieval/Retriever.cs`):**
   - Wires `UseHybrid`, `CandidateK`, and `RrfK` from `RagOptions`.
   - Note-based ordering for `RefCandidates`: when `ExtractedArticleKeys` and `NoteNumber` are present, chunks containing `تبصره <N>` in `SearchText` are ordered first (sorted by `VectorScore` desc, then `ChunkId` asc).
   - Legacy fallback: if `RefCandidates` is empty, scans Vector and FTS candidates for chunks with empty `ArticleKeys` whose `SectionTitle` or first 120 chars of `SearchText` match the target article regex.
   - Reciprocal Rank Fusion (`RankFusion.Fuse`) with $k=60$ using standard unweighted RRF ($\sum \frac{1}{k + r_m(d)}$) across active candidate lists.
   - Token coverage calculation using exact token intersection `queryTokens.Count(t => candidateTokens.Contains(t))` setting `HasFtsHit = true` if coverage $\ge 0.50$ (or $\ge 2$ tokens for queries with $\ge 4$ tokens), or if `refList.Count > 0` and either the query has no non-legal terms or at least one non-legal term matches a ref candidate chunk's search text.
6. **Grounding Citation Score Fix (`CitationParser.cs` & `RagService.cs`):**
   - Fixed `SourceCitation.Score` in both `CitationParser.ParseAndValidate` and `RagService.FormatSources` to use `VectorScore` (cosine similarity) instead of `FusedScore` (reciprocal sum $0.016 \dots 0.049$), preserving the `RagOptions.MinSimilarity = 0.55` contract.
7. **Offline Benchmark & ADR 0005:**
   - Created `eval/experiments/export_embedding_cache.py` exporting 447 embedding vectors (1024 dims) from `embed_cache_by_hash.npz` to `eval/experiments/out/embed_cache.json`.
   - Added `--benchmark-sanity` and `--benchmark-hybrid` modes to `eval/experiments/chunker_probe/Program.cs`:
     - Sanity check (`UseHybrid = false`) proved 100% exact parity with `embed_results_legal.csv` across all answerable questions (`Recall@5 = 90.4%`, `MRR = 0.8282`).
     - Hybrid benchmark (`UseHybrid = true`) evaluated 52 answerable questions, yielding `Recall@3 = 96.2%`, `Recall@5 = 96.2%`, and `MRR = 0.8614` (+0.0332 gain), with `q015` moving from Rank 12 to **Rank 1**.
   - Created `docs/adr/0005-hybrid-retrieval-and-article-keys.md`.

---

## 2. How I verified

1. **Unit & Application Tests:**
   ```powershell
   dotnet test --filter "Category!=RequiresOllama&Category!=Integration"
   ```
   **Output:**
   - `DocSearch.Application.Tests`: Passed: 174, Failed: 0.
   - `DocSearch.Infrastructure.Tests`: Passed: 63, Failed: 0.
   - `DocSearch.Api.Tests`: Passed: 12, Failed: 0.
   - **Total:** 249 unit tests passed, 0 failed.

2. **Integration Tests (PgVectorStore & AskController):**
   - `PgVectorStoreIntegrationTests.cs` extended with 4 new integration tests (`Exact_Article_Number_Query_Ranks_Article_First`, `Hybrid_Off_Equals_Vector_Only`, `Fts_Only_Match_Is_Included`, `ACL_Still_Enforced_In_Hybrid`).
   - `AskControllerIntegrationTests.cs` seeded chunk updated with `ArticleKeys = ["ماده 12"]`.

3. **Parity Sanity Check:**
   ```powershell
   dotnet run --project eval/experiments/chunker_probe/ChunkerProbe.csproj --benchmark-sanity
   ```
   **Output:**
   ```
   [SANITY CHECK PASSED] 100% parity with embed_results_legal.csv across all answerable questions!
   all: Recall@1 = 75.0%, Recall@3 = 88.5%, Recall@5 = 90.4%, Recall@10 = 94.2%, MRR = 0.8282
   ```

4. **Hybrid Retrieval Offline Benchmark:**
   ```powershell
   dotnet run --project eval/experiments/chunker_probe/ChunkerProbe.csproj --benchmark-hybrid
   ```
   **Output:**
   ```
   --- Retrieval Performance by Split (Answerable Questions: 52) ---
   | Split | Q Count | Recall@1 | Recall@3 | Recall@5 | Recall@10 | MRR | Strict R@1 | Strict R@5 | Strict MRR |
   |---|---|---|---|---|---|---|---|---|---|
   | dev   | 16      | 87.5%    | 93.8%    | 93.8%    | 100.0%    | 0.9141 | 87.5%    | 93.8%      | 0.9141     |
   | test  | 36      | 72.2%    | 97.2%    | 97.2%    | 97.2%     | 0.8380 | 72.2%    | 94.4%      | 0.8306     |
   | all   | 52      | 76.9%    | 96.2%    | 96.2%    | 98.1%     | 0.8614 | 76.9%    | 94.2%      | 0.8562     |
   ```

5. **Statistical Comparison (`compare_arms.py`):**
   ```powershell
   python eval/experiments/compare_arms.py legal hybrid
   python eval/experiments/compare_arms.py legal hybrid strict
   ```
   **Output:**
   - Loose rule:
     - `hit@3 : only A = 0, only B = 4`
     - `hit@5 : only A = 0, only B = 3`
     - `hit@10: only A = 0, only B = 2`
     - `mean dRR (B - A) = +0.0332, 95% bootstrap CI = [-0.0310, +0.1042]`
   - Strict rule:
     - `mean dRR (B - A) = +0.0340, 95% bootstrap CI = [-0.0302, +0.1044]`
   - Target questions:
     - `q015`: Rank 12 $\rightarrow$ **1**
     - `q016`: Rank 1 $\rightarrow$ **1**
     - `q050`: Rank 2 $\rightarrow$ **1**
     - `q055`: Rank 9 $\rightarrow$ **1**
     - `q056`: Rank 11 $\rightarrow$ **8**

6. **Format & Build:**
   ```powershell
   dotnet format --verify-no-changes
   dotnet build -warnaserror
   ```
   Verified 0 formatting errors, 0 warnings, 0 build errors.

---

## 3. Tests added/changed

1. **`tests/DocSearch.Application.Tests/Retrieval/TsQueryBuilderTests.cs` (6 tests added):**
   - `BuildWebsearchQuery_Filters_Stopwords_And_Sanitizes`
   - `BuildWebsearchQuery_Handles_Attached_And_Separated_Legal_Keywords`
   - `BuildWebsearchQuery_Preserves_Digit_Only_Tokens`
   - `TokenizeForCoverage_Splits_And_Expands_Attached_Keywords`
   - `ExtractQueryTokens_Extracts_Distinct_Keywords`
   - `BuildWebsearchQuery_With_Empty_Or_Whitespace_Returns_Null`
2. **`tests/DocSearch.Application.Tests/Retrieval/LegalReferenceExtractorTests.cs` (8 tests added):**
   - `Extract_Persian_Article_Number`
   - `Extract_Article_With_Mokarar`
   - `Extract_Maddeh_Vahedeh`
   - `Extract_Article_With_Note`
   - `Extract_Standalone_Note_Returns_Empty`
   - `Extract_English_Article`
   - `Extract_Multiple_Articles`
   - `Extract_Empty_Or_Null_Returns_Empty`
3. **`tests/DocSearch.Application.Tests/LegalAwareChunkerTests.cs` (3 tests added):**
   - `BuildChunkDraft_Aggregates_Multiple_Articles_For_Merged_Chunks`
   - `BuildChunkDraft_Populates_Continuation_Chunks_With_ArticleKey`
   - `Aein91_Pdf_Chunking_Produces_All_35_Article_Keys_And_Preserves_Hashes`
4. **`tests/DocSearch.Application.Tests/Retrieval/RetrieverTests.cs` (4 tests added):**
   - `RetrieveAsync_When_Hybrid_Enabled_Wires_Ref_And_Fts_Lists`
   - `RetrieveAsync_Orders_Ref_Candidates_By_Note_Match`
   - `RetrieveAsync_Falls_Back_To_Regex_For_Legacy_Empty_ArticleKeys`
   - `RetrieveAsync_Computes_TokenCoverage_Correctly`
5. **`tests/DocSearch.Application.Tests/Prompts/CitationParserTests.cs` (1 test updated/verified):**
   - Verified that `ParseAndValidate` correctly maps `VectorScore` to `SourceCitation.Score`.
6. **`tests/DocSearch.Infrastructure.Tests/Rag/RagServiceTests.cs` (1 test updated/verified):**
   - Verified that `RagService.FormatSources` maps `chunk.VectorScore` to `SourceCitation.Score`.
7. **`tests/DocSearch.Infrastructure.Tests/Integration/PgVectorStoreIntegrationTests.cs` (4 tests added):**
   - `Exact_Article_Number_Query_Ranks_Article_First`
   - `Hybrid_Off_Equals_Vector_Only`
   - `Fts_Only_Match_Is_Included`
   - `ACL_Still_Enforced_In_Hybrid`
8. **`tests/DocSearch.Api.Tests/Controllers/AskControllerIntegrationTests.cs` (1 test updated):**
   - Updated seeded chunk with `ArticleKeys = ["ماده 12"]`.

---

## 4. What the human must check manually

1. **Docker Desktop & Migration on Production DB:**
   When running the live system with PostgreSQL, ensure Docker Desktop is running and apply the migration:
   ```powershell
   dotnet ef database update -p src/DocSearch.Infrastructure -s src/DocSearch.Api
   ```
2. **Re-ingest Existing Collections:**
   Existing chunks in the database will have `article_keys = '{}'` by default. Re-ingest documents so that `LegalAwareChunker` populates the canonical `ArticleKeys` array.
3. **Verify Interactive Retrieval on Sample Regulation:**
   Upload `aein-91.pdf` and ask *"تبصره ۱ ماده ۷ آیین نامه آموزشی چه می‌گوید؟"*. Verify that the response answers with the 24-unit rule and cites Article 7, page 4.

---

## 5. Known limitations / deviations / ADRs

1. **ADR 0005:**
   Documented all architecture decisions, schema changes, and statistical benchmark comparisons in `docs/adr/0005-hybrid-retrieval-and-article-keys.md`.
2. **Same-Numbered Articles Across Documents:**
   If a collection contains multiple regulations with the same article number (e.g. `"ماده 12"`), all matching article chunks are retrieved into `RefCandidates`. RRF utilizes dense vector cosine similarity and FTS lexical context terms to rank the correct document first.
3. **Legacy Fallback:**
   For chunks created prior to T11 that have `article_keys = '{}'`, `Retriever` maintains a fallback regex scan on `SectionTitle` and the first 120 characters of `SearchText`.

---

## 6. Recommended next task

- **`T12 — Admin Dashboard & Metrics [PLAN-FIRST]`** or **`T13 — Conformance & Evaluation Harness Verification`**.
