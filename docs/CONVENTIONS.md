# CONVENTIONS — contracts, coding conventions, security, Definition of Done, pitfalls

Read this together with `AGENTS.md` at the start of **every** session. (These rules were moved out of `AGENTS.md` because some tools truncate long rules files.)

---

## 1. Core contracts (Application layer — implement exactly these names)

```csharp
namespace DocSearch.Domain;
public enum DocumentStatus { Uploaded, Processing, Ready, Failed }
public enum CollectionRole { Viewer, Editor, Owner }

namespace DocSearch.Application.Abstractions;

public sealed record PageText(int PageNumber, string Text, double Quality, bool NeedsOcr);

public interface IPdfExtractor
{
    Task<IReadOnlyList<PageText>> ExtractAsync(Stream pdf, CancellationToken ct);
}

public interface ITextNormalizer
{
    string NormalizeForDisplay(string text);    // what users see in snippets
    string NormalizeForEmbedding(string text);  // input to embedding model (docs AND queries)
    string NormalizeForSearch(string text);     // input to full-text search (docs AND queries)
}

public sealed record ChunkingOptions(int TargetTokens = 500, int MaxTokens = 700, int MinTokens = 80,
                                     double OverlapRatio = 0.15, bool RespectLegalStructure = true);

public sealed record ChunkDraft(int Index, int PageStart, int PageEnd, string? SectionTitle,
                                string Text, string SearchText, string EmbeddingText, int TokenCount);

public interface IChunker
{
    IReadOnlyList<ChunkDraft> Chunk(IReadOnlyList<PageText> pages, ChunkingOptions options);
}

public interface ITokenCounter { int Count(string text); }

public interface IFileStorage
{
    Task<string> SaveAsync(Stream content, string suggestedFileName, CancellationToken ct); // returns storage path (relative key)
    Task<Stream> OpenReadAsync(string storagePath, CancellationToken ct);
    Task DeleteAsync(string storagePath, CancellationToken ct);
}

public sealed record SearchRequest(float[] QueryEmbedding, string QueryText, IReadOnlyCollection<Guid> CollectionIds,
                                   string EmbeddingModel, int CandidateK, int TopK, bool UseHybrid);

public sealed record ScoredChunk(Guid ChunkId, Guid DocumentId, string FileName, int PageStart, int PageEnd,
                                 string? SectionTitle, string Text, double VectorScore, double? FtsScore, double FusedScore);

public interface IVectorStore
{
    Task AddChunksAsync(Guid documentId, Guid collectionId, IReadOnlyList<ChunkRecord> chunks, CancellationToken ct);
    Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct);
    Task<IReadOnlyList<ScoredChunk>> SearchAsync(SearchRequest request, CancellationToken ct);
}
public sealed record ChunkRecord(ChunkDraft Draft, float[] Embedding, string EmbeddingModel);

public interface IRetriever
{
    Task<RetrievalResult> RetrieveAsync(string question, IReadOnlyCollection<Guid> collectionIds, int? topK, CancellationToken ct);
}
public sealed record RetrievalResult(IReadOnlyList<ScoredChunk> Chunks, double BestVectorScore, bool HasFtsHit);

public sealed record AskRequest(string UserId, string Question, IReadOnlyCollection<Guid> CollectionIds, Guid? ConversationId, int? TopK);
public sealed record SourceCitation(int Index, Guid DocumentId, string FileName, int Page, string? SectionTitle, string Snippet, double Score);
public sealed record AnswerResult(string? Answer, bool NotFound, bool Degraded, IReadOnlyList<SourceCitation> Sources, Guid? ConversationId);

public interface IRagService { Task<AnswerResult> AskAsync(AskRequest request, CancellationToken ct); }
public interface IIngestionService { Task ProcessDocumentAsync(Guid documentId, CancellationToken ct); }
public interface IIngestionQueue { ValueTask EnqueueAsync(Guid documentId, CancellationToken ct); IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken ct); }
public interface IAccessService { Task<IReadOnlyCollection<Guid>> GetReadableCollectionIdsAsync(string userId, IReadOnlyCollection<Guid>? requested, CancellationToken ct); }
```

Chat and embeddings use `IChatClient` / `IEmbeddingGenerator<string, Embedding<float>>` directly (no custom wrapper).
If you must deviate from a signature, write an ADR first.

---

## 2. Conventions

**C#**
- `Nullable` enabled; `ImplicitUsings` enabled; file-scoped namespaces; `sealed` classes by default; records for DTOs.
- `async` end to end; every I/O method takes a `CancellationToken` and passes it down. No `.Result`, `.Wait()`, `async void`.
- Options pattern: `IOptions<T>` + `ValidateOnStart()` + `ValidateDataAnnotations()`; bad configuration must fail at startup with a clear message.
- DI: interfaces in Application, implementations in Infrastructure; registration extension methods per layer (`AddApplication`, `AddInfrastructure`).
- Errors: domain/application errors are exceptions mapped centrally to RFC 7807 `ProblemDetails` (see API spec). No exception swallowing. Use specific exception types (`NotFoundException`, `ForbiddenException`, `ValidationException`, `ConflictException`, `ExternalServiceUnavailableException`).
- Logging: structured templates (`_logger.LogInformation("Document {DocumentId} processed in {ElapsedMs} ms", …)`). **Never** log document text, prompts, API keys, JWTs, passwords at Info or above.
- Naming: `PascalCase` types/methods, `_camelCase` private fields, `Async` suffix on async methods, tests `Method_Scenario_Expected`.
- Tests: Arrange/Act/Assert; one behavior per test; no logic in tests; no sleeping (use polling with timeout helpers).
- Formatting: `dotnet format` must produce no diff (`dotnet format --verify-no-changes`).

**Git**
- Branch `feature/Txx-short-name`. Commits like `feat(chunker): split by legal articles (T06)`. Types: feat, fix, test, docs, refactor, chore, ci.
- Never rewrite published history. Never commit `bin/ obj/ .venv/ node_modules/ data/ .env` or PDFs in `eval/pdfs/`.

**Docs**
- Every non-obvious decision → ADR (`docs/adr/NNNN-title.md`: Context, Decision, Consequences, Alternatives).
- README stays accurate: setup, run, test, configuration, troubleshooting.

---

## 3. Security checklist (verify in every relevant task)

- [ ] Upload: only `application/pdf` with magic bytes `%PDF-` at start; size ≤ configured max; file name sanitized (`Path.GetFileName`, strip control chars); storage name is a generated GUID, never the client name; no path traversal.
- [ ] Authorization is enforced **inside queries** (`collection_id = ANY(@allowed)`), not after retrieving.
- [ ] Every endpoint except `/health/*`, `/auth/register`, `/auth/login` requires auth. Non-members get `404` (not `403`) for collections/documents they cannot see, to avoid leaking existence — **except** where the spec says otherwise.
- [ ] Passwords via Identity hashing; JWT signing key from config (≥ 32 bytes), never committed; short expiry.
- [ ] Rate limiting on `/auth/*` and `/ask`.
- [ ] Prompt injection: document excerpts are *data*; the system prompt says so; LLM output is never executed, never used as a file path/SQL/URL; the UI renders answers as plain text/escaped markdown.
- [ ] CORS: allow-list from config; no `AllowAnyOrigin` with credentials.
- [ ] Error responses never leak stack traces or internal paths (only in Development).
- [ ] Dependencies: no known-vulnerable packages (`dotnet list package --vulnerable`).

---

## 4. Definition of Done (every task)

- [ ] Builds: `dotnet build -warnaserror` (or equivalent) is clean; no new warnings.
- [ ] `dotnet test` green; new logic has tests (list their names in the report).
- [ ] `dotnet format --verify-no-changes` clean.
- [ ] Acceptance criteria of the task demonstrated with commands + real output (not "should work").
- [ ] No secrets, no `TODO`, no dead code, no debug prints.
- [ ] Docs updated (README/config tables/ADR) if behavior or config changed.
- [ ] Security checklist items relevant to the task verified.
- [ ] Final report in the required format (AGENTS.md §3).

---

## 5. Frequent pitfalls (read before coding)

- **EF + pgvector:** call `UseVector()` in `UseNpgsql(...)`; map `Vector` (from `Pgvector`) columns with `HasColumnType("vector(N)")`; HNSW index via `HasIndex(...).HasMethod("hnsw").HasOperators("vector_cosine_ops")`; `CREATE EXTENSION` goes in the first migration. The dimension in the column must equal `Ai:Embedding:Dimensions`; mismatch must fail fast at startup.
- **Cosine score:** pgvector `<=>` is cosine *distance*; similarity = `1 - distance`.
- **Filtered HNSW queries** can return fewer than K rows; for filtered search set `SET LOCAL hnsw.iterative_scan = relaxed_order` (pgvector ≥ 0.8) or fall back to exact scan when the corpus is small. Verify the extension version with `SELECT extversion FROM pg_extension WHERE extname='vector'`.
- **Generated tsvector column:** use `to_tsvector('simple', search_text)` (immutable) — never a language config that does not exist for Persian.
- **Persian in PowerShell:** PowerShell 5.1 mangles non-ASCII in pipes/`Invoke-RestMethod` bodies unless you send UTF-8 bytes. In scripts, send `[System.Text.Encoding]::UTF8.GetBytes($json)` with `Content-Type: application/json; charset=utf-8`.
- **Ollama embed API:** `POST /api/embed` with `{"model","input":[...]}` returns `embeddings` (L2-normalized). Always batch (default 16). Set a client timeout ≥ 60 s on CPU. e5 models need `query: ` / `passage: ` prefixes; `bge-m3` needs none.
- **Ollama on CPU is slow** with big batches: keep chunks ≤ 512 tokens, batch ≤ 16, process one document at a time.
- **`Channel<T>`:** use `BoundedChannelOptions` with `FullMode = Wait`; complete the writer on shutdown; the worker must catch per-item exceptions, mark the document `Failed`, and continue.
- **HttpClient:** use `IHttpClientFactory`/typed clients with timeouts; never `new HttpClient()` per call.
- **Testcontainers** need Docker running; keep one container per test class collection (shared fixture) to save RAM/time.
- **Large uploads:** raise Kestrel `MaxRequestBodySize` and `FormOptions.MultipartBodyLengthLimit` to the configured max; stream to disk, do not buffer the whole file in memory.
- **Line endings/BOM:** never add a BOM; do not convert Persian text encodings; keep `.gitattributes` `* text=auto`.
- **Windows long paths / spaces:** the repo path has no spaces; still quote paths in scripts.

---

---

## 6. Technology decisions (fixed)

| Concern | Decision |
|---|---|
| Language/runtime | C# 14 / **.NET 10 (LTS)**, `net10.0` |
| Web | ASP.NET Core Web API with **controllers** (`[ApiController]`), consistent everywhere |
| Architecture | Clean Architecture: `Domain`, `Application`, `Infrastructure`, `Api` |
| DB | **PostgreSQL 16 + pgvector** (single DB: metadata, vectors, full-text). Docker image `pgvector/pgvector:pg16` |
| ORM | EF Core + `Npgsql.EntityFrameworkCore.PostgreSQL` + `Pgvector.EntityFrameworkCore` (raw SQL allowed for search queries) |
| AI abstraction | `Microsoft.Extensions.AI` → `IChatClient`, `IEmbeddingGenerator<string, Embedding<float>>` |
| Embedding (dev) | Ollama via **OllamaSharp**; model decided by T01 (default `bge-m3`) |
| Chat (demo) | OpenAI-compatible endpoint (Gemini) via `Microsoft.Extensions.AI.OpenAI` |
| PDF extraction | decided by T01: **PdfPig** (`UglyToad.PdfPig`) or **Python FastAPI sidecar with PyMuPDF**; OCR: Tesseract (`fas`+`eng`) |
| Background work | `BackgroundService` + bounded `Channel<Guid>`; durability via DB `status` |
| File storage | `IFileStorage` (local disk now; S3/MinIO-ready) |
| Auth | ASP.NET Core Identity (EF) + JWT bearer; roles `Admin`, `User` |
| Logging | Serilog (structured) |
| API docs | built-in OpenAPI (`Microsoft.AspNetCore.OpenApi`) + **Scalar** UI (`Scalar.AspNetCore`); if incompatible with net10.0, choose an alternative and write an ADR |
| Versioning | `Asp.Versioning.Mvc` (`/api/v1`) |
| Tests | xUnit, `Testcontainers.PostgreSql` (image `pgvector/pgvector:pg16`), `Microsoft.AspNetCore.Mvc.Testing`; plain xUnit asserts or Shouldly (**not** FluentAssertions: license) |
| Frontend | React + Vite + TypeScript, RTL, self-hosted Vazirmatn font, Vitest |
| Packaging | Docker Compose; GitHub Actions CI |
| Package versions | Use the **latest stable version compatible with net10.0**, added with `dotnet add package`; central management in `Directory.Packages.props` |

---

## 7. Repository layout (create exactly this; add files only when tasks require)

```
/
├─ AGENTS.md  CLAUDE.md  GEMINI.md        (CLAUDE.md/GEMINI.md just point to AGENTS.md)
├─ README.md
├─ global.json                            (pins SDK 10)
├─ Directory.Build.props  Directory.Packages.props
├─ .editorconfig  .gitignore  .gitattributes  .env.example
├─ docker-compose.yml
├─ docs/
│   ├─ SPEC-DATA-AND-API.md  SPEC-PERSIAN-AND-RAG.md  TASKS.md  INFRA.md  EVAL.md  PARALLEL-BAKEOFF.md
│   ├─ adr/                               (0001-…, 0002-…)
│   └─ DEMO.md                            (T20)
├─ src/
│   ├─ DocSearch.Domain/                  (entities, enums; no dependencies)
│   ├─ DocSearch.Application/             (interfaces, DTOs, services, options)
│   ├─ DocSearch.Infrastructure/          (EF Core, AI clients, extractors, storage, vector search)
│   └─ DocSearch.Api/                     (controllers, Program.cs, workers, auth, Dockerfile)
├─ tests/
│   ├─ DocSearch.Domain.Tests/
│   ├─ DocSearch.Application.Tests/       (normalizer, chunker, RRF, prompt, citation parser, RagService with fakes)
│   ├─ DocSearch.Infrastructure.Tests/    (Testcontainers: EF, vector/FTS search, ACL)
│   ├─ DocSearch.Api.Tests/               (WebApplicationFactory, fakes)
│   └─ TestData/                          (small PDFs supplied by the human)
├─ scripts/                               (load-env.ps1 and other helper scripts)
├─ tools/extractor/                       (Python sidecar; only if chosen in T01)
├─ conformance/                           (pytest black-box suite; FROZEN; see docs/EVAL.md)
├─ eval/
│   ├─ pdfs/  (git-ignored)   questions.json   experiments/   results/   run_eval.py
└─ web/                                   (React app, T17)
```

### Project references (strict)
- `Domain` → nothing
- `Application` → `Domain`
- `Infrastructure` → `Application`, `Domain`
- `Api` → `Application`, `Infrastructure`, `Domain` (composition root only)
- Test projects reference what they test (+ `Application`/`Domain` as needed). Controllers contain **no business logic**.

---
