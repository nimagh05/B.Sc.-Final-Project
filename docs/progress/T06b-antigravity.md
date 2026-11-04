# Task T06b Handoff Note — Chunker Follow-up, RAG Refusal Edge-Cases & Enum Contract Fix

**Agent:** Antigravity  
**Date:** 2026-11-03  
**Branch:** `feature/T06b-chunker-followup`  
**Status:** Task complete. All 221 unit tests and 44 integration tests passing. `dotnet format --verify-no-changes` and `dotnet build -warnaserror` 100% clean. Ready for merge.

---

## 1. What was done

1. **Chunker Section Title Capture Timing & Sub-MinToken Merge Budgeting (`LegalAwareChunker`):**
   - Fixed the `section_title` timing in `ParseDocumentUnits`: `FlushParagraph()` is now invoked **before** updating `currentChapter`, `currentArticle`, and `currentHeading`. This ensures that paragraph units accumulated prior to a marker line are stamped with the section title under which they were authored rather than the succeeding heading.
   - Fixed sub-MinToken merge in `PackChunks`: When a chunk starts with a chapter-only heading (`current.Article == null` and `current.EstimatedTokens < options.MinTokens`) and merges the subsequent first article unit, `current.Article` and `current.SectionTitle` adopt the article's identifiers (`unit.Article` and `unit.SectionTitle`).
   - Title token budget check on merge: `titleTokens` is budgeted using `Math.Max(currentTitleTokens, candidateTitleTokens)` so merged chunks strictly respect `TargetTokens` and `MaxTokens`.
   - **T11 Note for Legal Reference Boost (§6.4):** `ChunkDraft.SectionTitle` and `Chunk.SectionTitle` are stored in Display form (preserving Persian digits like `ماده ۱۲` or `ماده ١٢`), while `ChunkDraft.EmbeddingText` and `ChunkDraft.SearchText` have digits normalized to ASCII (`ماده 12`). When implementing T11 (§6.4 reference boost), query reference matching against `section_title` should normalize digits/whitespace to ensure seamless matching against ASCII query references.

2. **Chunker Probe & Offline Evaluation Artifact Diff (`eval/experiments/chunker_probe`):**
   - Re-ran `chunker_probe` against the 12 extracted benchmark PDFs (`eval/experiments/out/extract/pymupdf`).
   - Comparative analysis between pre-T06b and post-T06b `chunks_legal.json` and `embed_cache_by_hash.npz`:
     - Arm (c) Legal-Aware chunk count: 206 (was 207).
     - Section title changed count: 13 chunks (corrected from next-article leak to current article).
     - Text (EmbeddingText) SHA-256 hash changed count: 27 / 206 chunks.
     - Cached embedding hits in `embed_cache_by_hash.npz`: 189 / 206 (91.7%) remain 100% valid by text hash.

3. **`RagService` Refusal Trimming & Degraded Mode Hardening:**
   - **Multi-Part Answers vs Full Refusal:** In `IsModelRefusal`, after stripping citation brackets and normalizing for search, `normalizedCore` (`"در اسناد موجود پاسخی برای این پرسش پیدا نشد"`) is removed, and common Persian/Latin punctuation and guillemets (`' ', '.', '،', ',', ':', ';', '؛', '!', '?', '؟', '"', '\'', '«', '»', '-', '–', '—', '\r', '\n', '\t'`) are trimmed from the remainder. The response is only treated as a refusal if `trimmedRest.Length <= RagConstants.MaxRefusalRemainderLength` (15). Multi-part responses answering part 1 with citations and stating no answer found for part 2 retain `NotFound = false`, `Degraded = false`, the answer, and cited sources.
   - **IsModelRefusal Trade-Off:** The threshold `RagConstants.MaxRefusalRemainderLength = 15` is chosen so that polite or punctuated refusal variants (e.g. `'«در اسناد موجود پاسخی برای این پرسش پیدا نشد.»'`, `'متاسفانه در اسناد موجود پاسخی برای این پرسش پیدا نشد.'`) are reliably detected as refusals (`NotFound = true`). The conscious trade-off is that polite refusals with >15 extra explanatory characters without citations will fall through to normal answer processing. However, if such answers lack citations, `RagService` logs a `ModelWithoutCitations` warning and falls back to all context chunks, while strict prompt grounding strongly discourages conversational filler.
   - **Empty / Whitespace LLM Responses:** When `_chatClient.GetResponseAsync` returns an empty or whitespace-only response, `RagService` logs a `LogWarning` ("Chat LLM provider returned empty or whitespace response. Falling back to degraded retrieval-only mode.") and triggers Degraded Mode (`degraded = true`, `answer = null`, `sources` populated with context chunks).
   - **MaxOutputTokens Default & Truncation (1024 vs 800):** While `SPEC-DATA-AND-API.md` §1.4 initially mentioned `MaxOutputTokens = 800`, the real default was configured to `1024` in `AiChatOptions` and `src/DocSearch.Api/appsettings.json` to prevent truncation of comprehensive multi-part Persian answers citing multiple legal articles. When an answer is truncated at the token limit, `RagService` detects `response.FinishReason == ChatFinishReason.Length` and logs a structured warning (`LLM response was truncated due to MaxOutputTokens limit (FinishReason=Length).`).
   - **Early Conversation Ownership Check:** In `RagService.AskAsync`, conversation ownership (`c.Id == request.ConversationId.Value && c.UserId == request.UserId`) is validated immediately after the ACL check before any query embedding, retrieval, or LLM invocation occurs, failing fast with `NotFoundException` without consuming AI quota.
   - **AskTimings Refactoring & Per-Stage Latency Logging:** Grouped timing parameters into `AskTimings(long EmbedRetrieveMs, long LlmMs)` passed to `CompleteAndPersistAsync`. Logged in structured Serilog event `AskCompleted`:
     `AskCompleted: ElapsedMs={ElapsedMs}, EmbedRetrieveMs={EmbedRetrieveMs}, LlmMs={LlmMs}, RetrievedCount={RetrievedCount}, BestScore={BestScore:F3}, NotFound={NotFound}, Degraded={Degraded}, NoAnswerReason={NoAnswerReason}`.

4. **API Contract & Test Fixture Hardening (`Program.cs`, `ApiTestFixture`):**
   - Registered `JsonStringEnumConverter` on `builder.Services.ConfigureHttpJsonOptions(...)` alongside `AddControllers().AddJsonOptions(...)` so .NET 10 OpenAPI / Scalar schemas serialize enums as strings.
   - `DocumentDto.Status` now serializes as `"Uploaded"`, `"Processing"`, `"Ready"`, `"Failed"`, matching `SPEC-DATA-AND-API.md` §2.4 and `conformance/conftest.py` line 64.
   - Fixed `Pgvector.Vector` type resolution race in `ApiTestFixture`: `InitializeAsync()` runs migrations and seeds on a standalone `AppDbContext` right after starting the container, explicitly calls `ReloadTypesAsync()` on its connection, and if `Services` is initialized, also calls `ReloadTypesAsync()` on the scoped context before clearing connection pools.

5. **Ingestion Defect Logging & Cleanups (`IngestionService`):**
   - Removed dead `_headerFooterCleaner` dependency from `IngestionService` (cleaning is already encapsulated inside `LegalAwareChunker.Chunk`).
   - Added structured warning logging during document processing when any page has `LooksReversed == true` or when chunks exhibit non-zero `ChunkDefectFlags`, reporting `DocumentId`, `AggregatedFlags`, `ReversedPages`, and `AffectedPages` without leaking document text.

---

## 2. Test Verification Summary

| Test Suite | Tests Run | Result | Notes |
|---|---|---|---|
| `DocSearch.Application.Tests` | 146 | **146 Passed, 0 Failed** | Added 3 unit tests: mid-page consecutive headings without blank lines, Persian/Arabic digits preservation in section title, chapter heading merge with article 1 |
| `DocSearch.Infrastructure.Tests` (Unit) | 63 | **63 Passed, 0 Failed** | Fast unit tests |
| `DocSearch.Infrastructure.Tests` (RAG) | 18 | **18 Passed, 0 Failed** | Added 2 tests: multi-part answer with refusal phrase keeps answer/citations; empty/whitespace LLM output triggers degraded mode |
| `DocSearch.Api.Tests` (Unit) | 12 | **12 Passed, 0 Failed** | Health check & problem details |
| `DocSearch.Api.Tests` (Integration) | 26 | **26 Passed, 0 Failed** | DocumentsController (16) & AskController (10) verifying string enum serialization and full end-to-end flows |
| `dotnet format --verify-no-changes` | - | **Clean (Exit 0)** | No formatting discrepancies |
| `dotnet build -warnaserror` | - | **Clean (Exit 0)** | 0 warnings, 0 errors |

---

## 3. Recommended Next Task

Proceed with **T11 — Hybrid Retrieval & Citation Fusion** (combining pgvector HNSW search, full-text search with tsvector/tsquery, legal reference boost §6.4, Reciprocal Rank Fusion, and integration with `IRetriever`).
