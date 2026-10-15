# Task Handoff — T03: Domain model & persistence

**Agent:** Antigravity (Gemini 3.8 Flash)  
**Date:** 2026-10-15  
**Branch:** `feature/T03-persistence`  
**Status:** Completed and Verified  

---

## 1. What was done
- **Domain entities & constants** (`src/DocSearch.Domain`):
  - Defined `EmbeddingConstants.VectorDimensions = 1024` in `DocSearch.Domain.Constants`.
  - Added enums `MessageRole` (`User`, `Assistant`) and `ReindexJobStatus` (`Queued`, `Running`, `Completed`, `Failed`) alongside existing `DocumentStatus` and `CollectionRole`.
  - Created plain C# domain entities with no EF attributes or Infrastructure dependencies: `Collection`, `CollectionMember`, `Document`, `Chunk`, `Conversation`, `Message`, and `ReindexJob`. All use `Guid.CreateVersion7()` and UTC `DateTimeOffset`.
- **Application layer options**:
  - Updated `AiEmbeddingOptions` in `DocSearch.Application` to validate dimensions against `EmbeddingConstants.VectorDimensions`.
- **Infrastructure persistence** (`src/DocSearch.Infrastructure`):
  - Implemented `ApplicationUser : IdentityUser` in `DocSearch.Infrastructure.Identity`.
  - Implemented `AppDbContext : IdentityDbContext<ApplicationUser>` in `DocSearch.Infrastructure.Persistence` with complete Fluent API configurations matching `SPEC-DATA-AND-API.md` §1.1.
  - Implemented snake_case table and column names, foreign keys with cascade delete, check constraints (`ck_collection_members_role`, `ck_documents_status`, `ck_messages_role`, `ck_reindex_jobs_status`), unique constraints, HNSW cosine index (`m = 16, ef_construction = 64`), GIN index on generated stored tsvector (`to_tsvector('simple', search_text)`), and GIN trigram index on `search_text`.
  - Implemented design-time `AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>` reading `ConnectionStrings__Default` from environment variables.
  - Registered `AppDbContext` with `UseNpgsql(..., o => o.UseVector())` in `AddInfrastructure`.
- **Initial migration**:
  - Generated migration `Initial` (`20261015091148_Initial`) with `CREATE EXTENSION IF NOT EXISTS vector;` and `CREATE EXTENSION IF NOT EXISTS pg_trgm;` explicitly executing before table creation.
- **Integration tests** (`tests/DocSearch.Infrastructure.Tests`):
  - Created `PostgresFixture` with `Testcontainers.PostgreSql` sharing image `pgvector/pgvector:pg16` across the test collection.
  - Implemented all 6 mandatory tests in `PersistenceIntegrationTests`:
    1. `Migration_Applies_OnEmptyDatabase`
    2. `Chunk_RoundTrip_PreservesEmbedding`
    3. `Chunk_Tsv_IsGeneratedFromSearchText`
    4. `Document_Duplicate_Hash_InSameCollection_IsRejected`
    5. `Delete_Document_Cascades_Chunks`
    6. `Check_Constraint_Rejects_InvalidStatus`

---

## 2. Verification results
- `dotnet build -warnaserror`: **0 warnings, 0 errors**.
- `dotnet test --filter "Category!=Integration&Category!=RequiresOllama"`: **23 passed, 0 failed**.
- `dotnet test --filter "Category=Integration"`: **6 passed, 0 failed**.
- `dotnet test --filter "Category!=RequiresOllama"`: **29 passed, 0 failed**.
- `dotnet format --verify-no-changes`: **Clean (0 errors, 0 diff)**.
- Compose DB verification:
  - Started DB via `docker compose up -d db`.
  - Applied migration via `dotnet ef database update -p src/DocSearch.Infrastructure -s src/DocSearch.Api`.
  - Verified `\d chunks` and `\d documents` using `psql`, confirming `vector(1024)`, stored `tsv`, HNSW, GIN, unique, and check constraints.
  - Stopped DB via `docker compose down`.

---

## 3. How to continue / Commands to run
- To run unit tests:
  ```powershell
  dotnet test --filter "Category!=Integration&Category!=RequiresOllama"
  ```
- To run integration tests with Testcontainers:
  ```powershell
  dotnet test --filter "Category=Integration"
  ```
- To run all tests:
  ```powershell
  dotnet test --filter "Category!=RequiresOllama"
  ```
- Recommended next task: **T04 — PersianTextNormalizer**.
