# 7. Collection Re-indexing and Embedding Provider / Dimension Swap

Date: 2026-11-12  
Status: Accepted  
References: `docs/SPEC-DATA-AND-API.md` §1.1, §1.3, §2.6; `docs/SPEC-PERSIAN-AND-RAG.md` §1.2; `docs/TASKS.md` (T15)

---

## Context

DocSearch stores dense embeddings (`chunks.embedding vector(1024)`) alongside the active model identifier (`chunks.embedding_model`). Over the lifetime of a deployment, administrators may need to:

1. **Swap the embedding model or provider with the same vector dimension** (e.g., switching from `Ollama` / `bge-m3` to another 1024-dimensional model or an `OpenAICompatible` embedding endpoint, or re-embedding after text-normalization updates).
2. **Change the vector dimension** (e.g., adopting a 768- or 1536-dimensional embedding model).
3. **Swap the chat LLM provider or model** (`Ai:Chat:Provider`, `Ai:Chat:Model`, `Endpoint`, `ApiKey`).

Because CPU-bound embedding generation can take seconds per batch and hosts may restart mid-job, collection re-indexing must be asynchronous, resumable, bounded in DB transaction duration, and protected against concurrent conflicting jobs on the same collection.

---

## Decision

1. **Asynchronous, Resumable Re-index Worker (`ReindexQueue` + `ReindexWorker` + `ReindexService`):**
   - `POST /api/v1/admin/collections/{id}/reindex` (Admin-only) verifies the collection exists (`404 resource.not_found`), rejects duplicate active (`Queued` or `Running`) jobs on the same collection (`409 reindex.already_running`, backed by the partial unique index `ix_reindex_jobs_active_collection` on `reindex_jobs (collection_id) WHERE status IN ('Queued', 'Running')`), snapshots `Ai:Embedding:Model` and the count of chunks in `Ready` documents, persists a `Queued` `ReindexJob`, enqueues it to a bounded `Channel<Guid>`, and returns `202 Accepted { jobId }`.
   - `ReindexWorker` claims jobs (`Queued -> Running`) and processes chunks in batches (`Ai:Embedding:BatchSize`) ordered by `chunks.id` where `c.embedding_model != job.EmbeddingModel` and the parent document is `Ready`.
   - **Exact Text Parity:** Re-indexing reconstructs each passage using `ChunkEmbeddingTextBuilder.Build` (`NormalizeForEmbedding((IncludeSectionTitleInEmbedding && !string.IsNullOrWhiteSpace(SectionTitle) ? SectionTitle + "\n" : "") + Text)`), guaranteeing byte-for-byte parity with `LegalAwareChunker` at ingestion time.
   - **No Long-Running DB Transactions:** `IPassageEmbedder.EmbedPassagesAsync` is always awaited *outside* any database transaction; each batch opens a short transaction only to persist updated `embedding` vectors, `embedding_model`, and `done_chunks` progress.
   - **Crash & Restart Recovery:** On startup, `ReindexWorker` resets any `Running` jobs back to `Queued` and re-enqueues all `Queued` jobs; because the batch query skips chunks whose `embedding_model` already equals `job.EmbeddingModel`, resumed jobs continue without re-embedding already-migrated chunks. Host cancellation leaves jobs recoverable rather than marking them `Failed`.

2. **Procedure — Same-Dimension Embedding Model / Provider Swap:**
   1. Update `Ai:Embedding:Provider`, `Ai:Embedding:Model`, `Ai:Embedding:Endpoint`, `Ai:Embedding:ApiKey` (if applicable), and any model-specific `QueryPrefix` / `PassagePrefix` (e.g., `"query: "` / `"passage: "` for E5 models; empty for `bge-m3`), keeping `Ai:Embedding:Dimensions = 1024`.
   2. Restart the API so validated options and the `IEmbeddingGenerator` pipeline bind the new provider/model.
   3. Trigger `POST /api/v1/admin/collections/{id}/reindex` as an `Admin` user for each collection and poll `GET /api/v1/admin/reindex-jobs/{jobId}` until `status == "Completed"`.
   - **Same-Model Re-index Is a No-Op:** When all chunks in `Ready` documents of the collection already carry `embedding_model == Ai:Embedding:Model`, `TotalChunks` is `0` and the job completes immediately (`DoneChunks = 0`, `Status = Completed`) without calling the embedding provider. This preserves crash-resumable idempotency; consequently, changes to `PersianTextNormalizer`, `ChunkingOptions.IncludeSectionTitleInEmbedding`, or `LegalAwareChunker` do **not** take effect via a same-model collection re-index — those require either reprocessing documents (`POST /api/v1/documents/{id}/reprocess`) or bumping the `Ai:Embedding:Model` identifier before triggering collection re-index.

3. **Procedure — Embedding Dimension Change (Schema Migration Required):**
   - `EmbeddingConstants.VectorDimensions` (`1024`) is compiled into the EF Core model (`chunks.embedding vector(1024)`) and enforced at startup (`AiEmbeddingOptions.Validate`) and during re-indexing. Vector columns and HNSW indexes are **not** altered dynamically at runtime.
   - To adopt a model with a different dimension `N`:
     1. Update `EmbeddingConstants.VectorDimensions` in `src/DocSearch.Domain/Constants/EmbeddingConstants.cs` to `N` and `Ai:Embedding:Dimensions` in configuration to `N`.
     2. Add an EF Core migration that drops `ix_chunks_embedding_hnsw`, alters `chunks.embedding` to `vector(N)` (clearing or casting existing incompatible vectors), and recreates `ix_chunks_embedding_hnsw` with `vector_cosine_ops`.
     3. Apply the migration (`dotnet ef database update`), start the API with the new model configuration, and run `POST /api/v1/admin/collections/{id}/reindex` for every collection.

4. **Procedure — Chat LLM Provider Swap:**
   - Chat LLM models (`Ai:Chat:Provider`, `Ai:Chat:Model`, `Endpoint`, `ApiKey`) do not affect stored vectors or `chunks.embedding_model`. Update configuration and restart the API; no re-index is required.

---

## Consequences

- **Positive:**
  - Single-column schema (`chunks.embedding vector(1024)`) and HNSW index remain simple and consistent with `SPEC-DATA-AND-API.md` §1.1.
  - Re-indexing is safe under host restarts, concurrent admin requests (`409 reindex.already_running`), and slow CPU embedding providers.
  - Zero divergence between ingestion-time and reindex-time embedding text.
- **Operational Trade-off (Search Visibility During Model Swap):**
  - Per `SPEC-DATA-AND-API.md` §1.3 Invariant 3, `PgVectorStore` filters all vector, FTS, and reference-boost queries by `c.embedding_model = @model` (the currently configured `Ai:Embedding:Model`).
  - Consequently, after changing `Ai:Embedding:Model` and **while** a collection's re-index job is still `Queued` or `Running`, chunks still carrying the previous `embedding_model` are **ignored by search** (avoiding mixed-vector-space cosine corruption and avoiding a dual-vector-column schema). Visibility is restored progressively per batch and is complete once the job reaches `Completed`.

---

## Alternatives

- **Dual vector columns (`embedding` + `pending_embedding`) for zero-downtime blue/green swaps:** Rejected for T15 because it doubles vector storage and HNSW index memory/maintenance overhead (critical on the 12 GB RAM / 4 GB Docker target) and deviates from the frozen `SPEC-DATA-AND-API.md` §1.1 schema.
- **Runtime `ALTER TABLE` on dimension mismatch:** Rejected because changing `vector(N)` invalidates existing vectors and requires rebuilding the HNSW index under exclusive locks, which belongs in an explicit, reviewed EF Core migration.
