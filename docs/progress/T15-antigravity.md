# Task T15 Handoff — Re-index & Provider Swap

## Summary of Completed Work

1. **Application Contracts & Parity Text Builder (`DocSearch.Application`)**:
   - Added `IReindexService` (`EnqueueReindexAsync`, `GetJobStatusAsync`, `ProcessJobAsync`) and `IReindexQueue` (`EnqueueAsync`, `DequeueAllAsync`) in `src/DocSearch.Application/Abstractions/Contracts.cs`.
   - Extracted `ChunkEmbeddingTextBuilder.Build` (with both `ChunkingOptions` and `bool includeSectionTitleInEmbedding = true` overloads) in `src/DocSearch.Application/Chunking/ChunkEmbeddingTextBuilder.cs` and updated `LegalAwareChunker.BuildChunkDraft` to call it, guaranteeing byte-for-byte parity between ingestion-time `ChunkDraft.EmbeddingText` and reindex-time passage reconstruction.

2. **Persistence & Partial Unique Index (`DocSearch.Infrastructure`)**:
   - Added `ix_reindex_jobs_active_collection` on `reindex_jobs (collection_id) WHERE status IN ('Queued', 'Running')` in `src/DocSearch.Infrastructure/Persistence/AppDbContext.cs`.
   - Generated EF Core migration `20261112094400_AddReindexJobActiveCollectionIndex`.

3. **Background Queue, Worker, and Resumable Re-index Service (`DocSearch.Infrastructure` & `DocSearch.Api`)**:
   - Implemented `ReindexQueue` (bounded `Channel<Guid>` with `FullMode = BoundedChannelFullMode.Wait`), `ReindexWorker` (`BackgroundService` with `SkipStartupDbSeeding` / `CanConnectAsync` guards, resetting `Running -> Queued` and re-enqueueing `Queued` jobs on startup), and `ReindexService`:
     - Validates collection existence (`404 resource.not_found`).
     - Rejects concurrent active (`Queued`/`Running`) reindex jobs on the same collection (`409 reindex.already_running`), enforced both upfront and via PostgreSQL unique violation (`23505`) translation.
     - Snapshots `Ai:Embedding:Model` and `TotalChunks` as the count of stale chunks (`c.EmbeddingModel != targetModel`) in `Ready` documents of the collection, persists `Queued` job, enqueues `jobId`, and returns `202 Accepted`.
     - Processes stale chunks (`c.EmbeddingModel != targetModel` in `Ready` documents, ordered by `c.Id`, batched by `Ai:Embedding:BatchSize`, queried with `.AsNoTracking()`) with `IPassageEmbedder.EmbedPassagesAsync` executed **outside** any DB transaction.
     - Updates chunks and job progress/completion inside short per-batch transactions using `ExecuteUpdateAsync` (`WHERE c.Id == chunkId`) so concurrent document delete/reprocess cleanly affects 0 rows without `DbUpdateConcurrencyException`, and computes `DoneChunks = Math.Clamp(totalChunks - remainingStale, 0, totalChunks)` so `DoneChunks == TotalChunks` on completion.
     - Leaves cancelled jobs recoverable on host shutdown and records sanitized `ErrorMessage` on failures via shared `ErrorMessageSanitizer` (`src/DocSearch.Infrastructure/Common/ErrorMessageSanitizer.cs`, also reused by `IngestionService`).
   - Refactored `src/DocSearch.Api/Controllers/AdminController.cs` to delegate to `IReindexService` and registered `ReindexWorker` in `src/DocSearch.Api/Program.cs`.

4. **ADR & Tests**:
   - Created `docs/adr/0007-reindex-and-provider-swap.md` documenting same-dimension provider/model swap (including explicit same-model no-op behavior and reprocess/model-bump requirement for normalizer/chunker changes), dimension-change migration procedure, chat provider swap, and the search-visibility trade-off during in-progress model swaps.
   - Added unit and integration tests:
     - `ChunkEmbeddingTextBuilderTests.Reindex_Text_Matches_Ingestion_EmbeddingText`
     - `ChunkEmbeddingTextBuilderTests.Reindex_Text_Matches_All_201_Benchmark_Chunks` (`[SkippableFact]` against `eval/experiments/out/chunks_legal_t06b.json`)
     - `ReindexQueueTests` (`EnqueueAndDequeue_PreservesFifoOrder`, `BoundedChannel_WaitsWhenFull_AndResumesOnDequeue`)
     - `ReindexServiceIntegrationTests` (`Reindex_Updates_EmbeddingModel_And_Vectors`, `Search_Ignores_Chunks_Of_Old_Model_During_Reindex`, `Reindex_Only_Targets_Stale_Chunks_TotalChunks_Matches_MigratedCount`, `Reindex_Is_Resumable`, `Reindex_Continues_When_Chunk_Deleted_MidBatch`, `ProcessJobAsync_WhenEmbedderThrows_SetsFailedWithSanitizedMessage`, `PartialUniqueIndex_Enforces_Single_Active_Job_Per_Collection`, `ReindexWorker_StartupRecovery_Resets_Running_Job_And_Completes`)
     - `AdminControllerIntegrationTests` (`Only_Admin_Can_Reindex`, `Concurrent_Reindex_On_Same_Collection_Returns_409`)

5. **Pre-Merge Cleanup**:
   - Confirmed no `run_conformance.ps1` exists in the repository root or git index, and removed the temporary scratch script after verifying it never read `.env` or touched `docsearch_db`.

## Verification Commands & Measured Results

- `dotnet format --verify-no-changes` → exited with code `0`.
- `dotnet build -warnaserror` → `Build succeeded. 0 Warning(s), 0 Error(s)` (`00:00:26.78`).
- `dotnet test --filter "Category!=RequiresOllama" --no-build`:
  - `DocSearch.Application.Tests`: `Passed! - Failed: 0, Passed: 180, Skipped: 0, Total: 180, Duration: 946 ms`
  - `DocSearch.Infrastructure.Tests`: `Passed! - Failed: 0, Passed: 131, Skipped: 0, Total: 131, Duration: 39 s`
  - `DocSearch.Api.Tests`: `Passed! - Failed: 0, Passed: 62, Skipped: 0, Total: 62, Duration: 51 s`
  - Total: **373 passed, 0 skipped, 0 failed**.
- `dotnet list package --vulnerable --include-transitive` → no vulnerable packages across all 8 projects (verified prior to test/doc-only adjustments; no package references changed).
- Two-pass Conformance Suite (`conformance/test_conformance.py`, executed prior to pre-merge test/ReindexService adjustments):
  - Pass A (`-m "not degraded"`, `Ai__Chat__Fake__Fail=false`): `23 passed, 1 deselected in 32.73s` (`test_21_admin_endpoints`: `1.25s`).
  - Pass B (`-m degraded`, `Ai__Chat__Fake__Fail=true`): `1 passed, 23 deselected in 3.29s`.
  - Total: **24/24 passed**.

## Next Steps
- **T16 — CI & integration hygiene**: Set up GitHub Actions workflow (`restore`, `build -warnaserror`, `format --verify-no-changes`, `test`, vulnerable package audit, python sidecar tests, and two-pass conformance suite).
