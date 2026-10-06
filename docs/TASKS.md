# TASKS — step-by-step playbook

Legend: **[H]** = human action required (agent cannot do it; ask the human) · **[PLAN-FIRST]** = present a plan and wait for approval before coding · **Done when** = objective acceptance criteria.

## Global task rules (apply to every task)

1. Read: `AGENTS.md`, the spec files the task names, then only the current task.
2. Branch `feature/Txx-name` from an up-to-date `main`.
3. Work in small steps; after each step run `dotnet build` (and the relevant tests).
4. Commands are PowerShell. Run from the repo root unless stated.
5. Verification output must be **real** (copy the actual lines).
6. If blocked by network (restore, Docker pull, model download) → tell the human to enable VPN; do not hack around it.
7. Finish with the 6-heading report (AGENTS.md §3). Stop. Do not start the next task.
8. Naming: test classes `<Class>Tests`; integration tests marked `[Trait("Category","Integration")]`; Ollama tests `[Trait("Category","RequiresOllama")]`.
9. Run commands: build `dotnet build -warnaserror`; unit tests `dotnet test --filter "Category!=Integration&Category!=RequiresOllama"`; all tests `dotnet test --filter "Category!=RequiresOllama"`.

Task order: **T00 → T01 → T01B → T02 → … → T20**, then stretch tasks. Do not reorder unless the human says so.

---

## T00 — Repository bootstrap
**Goal:** a clean, documented, buildable-later repo skeleton.
**Create:** `README.md` (project summary, status, roadmap link, how to run — placeholders only for things not built yet are NOT allowed; write only what is true now), `.gitignore`, `.gitattributes` (`* text=auto`), `.editorconfig`, `.env.example`, `global.json` (`{"sdk":{"version":"10.0.401","rollForward":"latestFeature"}}`), `docs/adr/0001-record-architecture-decisions.md`, `CLAUDE.md` and `GEMINI.md` (each exactly: `@AGENTS.md` for CLAUDE.md, and for GEMINI.md: `Read and follow AGENTS.md in this repository root.`).
**Contents:** `.gitignore` per `docs/INFRA.md` (must ignore `bin/ obj/ .vs/ .idea/ .venv/ node_modules/ data/ logs/ .env eval/pdfs/ eval/results/*.png`, keep `eval/questions.json`). `.env.example` per INFRA.
**Verify:** `git status` clean after commit; `dotnet --version` prints 10.x; `git check-ignore -v eval/pdfs/x.pdf` shows it is ignored.
**Done when:** all files exist and are committed on `feature/T00-bootstrap`; README is ≤ 60 lines and accurate.
**[H]:** create GitHub private repo, push, merge PR.

---

## T01 — Phase-0 experiments: PDF extraction + embedding benchmark  [PLAN-FIRST]
**Goal:** decide *with data* (a) the PDF extractor, (b) the embedding model + dimension, (c) token-counter calibration, (d) whether OCR is needed.
**[H] prerequisites:** 10–15 real PDFs in `eval/pdfs/` (Persian + English; ≥ 1 scanned; ≥ 2 Persian legal/regulation docs with "ماده/تبصره") and `eval/questions.json` (≥ 30 items, schema in `docs/EVAL.md`). `ollama pull` for each candidate model (`bge-m3`, `multilingual-e5-base`, `multilingual-e5-small`).
**Create (Python, in `eval/experiments/`, using the repo `.venv`):**
1. `requirements.txt` (pymupdf, pdfplumber, requests, numpy, pandas, matplotlib).
2. `extract_compare.py` — for every PDF and library (PyMuPDF `page.get_text("text")`, pdfplumber `extract_text()`), write per-page text to `out/extract/<lib>/<pdf>/<page>.txt` and a CSV `out/extract_summary.csv` with: pdf, lib, page, chars, letterRatio, persianRatio, presentationRatio, replacementRatio, looksReversed, quality (formulas in `SPEC-PERSIAN-AND-RAG.md` §2), empty_page flag, seconds. Also dump 3 random pages per PDF side-by-side to `out/extract_samples.md` for human reading.
3. `pdfpig_probe/` (tiny C# console, `net10.0`) that does the same with PdfPig and writes `out/extract/pdfpig/…` and the same CSV columns (so all three are comparable). (If this proves impractical, document why and compare the two Python libraries only.)
4. `embed_benchmark.py` — chunk the best extractor's text with a simple fixed chunker (≈ 500 tokens), embed chunks + questions via Ollama `/api/embed` for each model (apply e5 prefixes!), compute **Recall@1/3/5/10 and MRR** (hit = chunk's page range intersects a gold page of the gold file), mean embed time per chunk, and write `out/embed_results.csv` + `out/embed_results.md`.
5. `token_calibration.py` — for ≥ 50 chunks compare the heuristic estimate with Ollama's reported `prompt_eval_count` (if present) and print the best multipliers.
**ADR:** `docs/adr/0002-pdf-extractor-and-embedding-model.md` — results tables (summaries only, no document text), the **decision** (extractor, model, dimension, token multipliers, OCR yes/no) and risks.
**Verify:** scripts run end-to-end with one command each (documented in `eval/experiments/README.md`); CSVs exist.
**Done when:** ADR contains a clear recommendation backed by numbers; reproducible instructions; no document text committed.
**Note:** this decision fixes `vector(N)` in T03 and `Extraction:Provider` in T05.

---

## T01B — Conformance suite & evaluation harness skeleton (FROZEN after approval)  [PLAN-FIRST]
**Goal:** a black-box, implementation-agnostic test suite that any implementation of the API must pass (used for CI and for comparing agents). Details in `docs/EVAL.md` §Conformance.
**Create:** `conformance/` (pytest), `conformance/README.md`, `conformance/data/` (tiny PDFs: provided by the human for Persian; English/corrupt/non-PDF files may be generated), `conformance/conftest.py` (reads `BASE_URL`, `ADMIN_EMAIL`, `ADMIN_PASSWORD`; waits for `/health/ready`), tests per the table in EVAL.md, and `eval/run_eval.py` skeleton (argument parsing, loading questions, calling `/ask`, writing JSON) without metrics yet.
**Verify:** `pytest conformance --collect-only` lists all tests; tests are skipped with a clear message if `BASE_URL` is unreachable.
**Done when:** the human reviews and approves; afterwards `conformance/**` is frozen.

---

## T02 — Solution skeleton, Docker Compose (db), health
**Goal:** buildable solution + running database + health endpoints.
**Steps:**
1. Create solution and projects (target `net10.0`):
```powershell
dotnet new sln -n DocSearch
dotnet new classlib -n DocSearch.Domain         -o src/DocSearch.Domain
dotnet new classlib -n DocSearch.Application    -o src/DocSearch.Application
dotnet new classlib -n DocSearch.Infrastructure -o src/DocSearch.Infrastructure
dotnet new webapi   -n DocSearch.Api            -o src/DocSearch.Api --use-controllers
dotnet new xunit -n DocSearch.Domain.Tests         -o tests/DocSearch.Domain.Tests
dotnet new xunit -n DocSearch.Application.Tests    -o tests/DocSearch.Application.Tests
dotnet new xunit -n DocSearch.Infrastructure.Tests -o tests/DocSearch.Infrastructure.Tests
dotnet new xunit -n DocSearch.Api.Tests            -o tests/DocSearch.Api.Tests
```
 Add all projects to the solution (`dotnet sln add …`), add project references per `docs/CONVENTIONS.md` §7. Delete template junk (WeatherForecast etc.).
2. `Directory.Build.props` (`TargetFramework net10.0`, `Nullable enable`, `ImplicitUsings enable`, `LangVersion latest`, `Deterministic true`, `AnalysisLevel latest-recommended`, `EnforceCodeStyleInBuild true`); `Directory.Packages.props` with `ManagePackageVersionsCentrally=true` (versions added by `dotnet add package`).
3. Packages for Api: Serilog.AspNetCore, Microsoft.AspNetCore.OpenApi, Scalar.AspNetCore, Asp.Versioning.Mvc, AspNetCore.HealthChecks.NpgSql (or custom check).
4. `Program.cs`: Serilog from config, controllers, API versioning, ProblemDetails (+ global exception handler → problem+json per spec), OpenAPI+Scalar (when `Docs:Enabled`), health checks `/health/live` (no checks) and `/health/ready` (DB check; others added later), options classes (`AiOptions`, `RagOptions`, `ChunkingOptions`, `ExtractionOptions`, `StorageOptions`, `IngestionOptions`, `JwtOptions`, `CorsOptions`) with `ValidateOnStart`.
5. `docker-compose.yml` with `db` service exactly as `docs/INFRA.md`; `.env.example` complete; ports configurable.
6. `scripts/load-env.ps1`: loads `.env` into the current PowerShell session as environment variables, builds `ConnectionStrings__Default` from `POSTGRES_*`/`DB_PORT`, sets `ASPNETCORE_URLS=http://localhost:$API_PORT`. Usage: `. .\scripts\load-env.ps1`. **Connection strings and keys must come from environment variables, never from `dotnet user-secrets`** (several copies of the repo may run on one machine and share the same `UserSecretsId`).
7. `README.md`: how to start db, run API, run tests.
**Tests:** `ProblemDetailsTests` (unhandled exception → 500 problem+json without stack trace), `OptionsValidationTests` (bad config fails startup).
**Verify:**
```powershell
copy .env.example .env   # then edit POSTGRES_PASSWORD
docker compose up -d db
docker compose ps        # db healthy
dotnet run --project src/DocSearch.Api
# in another terminal:
Invoke-RestMethod http://localhost:5000/health/ready
```
(use the port from your launch settings/`ASPNETCORE_URLS`).
**Done when:** `/health/ready` is Healthy with DB up and Unhealthy (503) with DB down; Scalar UI opens; build/test green.
**[H]:** Docker Desktop running; `.env` created.

---

## T03 — Domain model & persistence
**Goal:** entities, `AppDbContext`, migration, repository-level tests.
**Steps:** entities in Domain exactly per `SPEC-DATA-AND-API.md` §1 (plain classes/records, no EF attributes). `AppDbContext : IdentityDbContext<ApplicationUser>` in Infrastructure with Fluent configuration for every table/index/constraint (HNSW, GIN, trigram, unique constraints, check constraints, generated tsvector). Vector dimension from **one** constant sourced from the T01 ADR. Initial migration (with `CREATE EXTENSION`). Design-time factory. DI extension `AddInfrastructure`.
**Tests (Testcontainers, `Category=Integration`):** shared `PostgresFixture` (image `pgvector/pgvector:pg16`, applies migrations); `Migration_Applies_OnEmptyDatabase`; `Chunk_RoundTrip_PreservesEmbedding`; `Chunk_Tsv_IsGeneratedFromSearchText`; `Document_Duplicate_Hash_InSameCollection_IsRejected`; `Delete_Document_Cascades_Chunks`; `Check_Constraint_Rejects_InvalidStatus`.
**Verify:** `dotnet ef database update …` on the compose DB; `\d chunks` via `docker compose exec db psql -U postgres -d docsearch -c "\d chunks"` shows hnsw/gin indexes.
**Done when:** all tests green; migration reproducible from scratch.

---

## T04 — PersianTextNormalizer
**Goal:** implement `ITextNormalizer` exactly per `SPEC-PERSIAN-AND-RAG.md` §1.
**Steps:** `PersianTextNormalizer` in Application (pure, no I/O). Use `string.Normalize(NormalizationForm.FormKC)`, a precomputed `Dictionary<char,char>`/switch for maps, `StringBuilder`, source-generated `Regex` for whitespace/hyphenation. Constants file with every code point named (no magic literals).
**Tests (`PersianTextNormalizerTests`):** the 15 mandatory cases + ≥ 25 more; property-style idempotency test over ≥ 200 generated strings (fixed seed) mixing Persian/Arabic/Latin/digits/ZWNJ/bidi marks; null/empty; performance sanity (100 KB text < 50 ms).
**Done when:** all tests pass; spec table fully covered.

---

## T05 — PDF extraction adapter  [PLAN-FIRST]
**Goal:** `IPdfExtractor` per the T01 ADR + page quality scoring.
**Option A (PdfPig):** `UglyToad.PdfPig`; extract per page with `ContentOrderTextExtractor` (or `page.Text` if the ADR says better); no temp files; stream input.
**Option B (Python sidecar):** `tools/extractor/` FastAPI app: `POST /extract` (multipart `file`) → `{ "pages": [ { "page": 1, "text": "…" } ] }`, `GET /health`; PyMuPDF (`fitz`), pages sorted by blocks (`page.get_text("blocks")` sorted by y then x, RTL-aware per ADR); size limit; no file persistence; `requirements.txt`, `Dockerfile`, `pytest` tests. C# `HttpPdfExtractor` (typed `HttpClient`, timeout, 2 retries, maps failures to `ExternalServiceUnavailableException`).
**Both:** `PageQualityScorer` (pure, per `SPEC` §2) producing `PageText(Page, Text, Quality, NeedsOcr)`; `Extraction:Provider` switch in DI; `/health/ready` includes extractor check when Option B.
**Tests:** `PageQualityScorerTests` (empty, garbage, replacement chars, reversed Persian sample, good Persian, English); extractor tests using small PDFs in `tests/TestData` (supplied by human): page count, non-empty text, Persian sample contains an expected phrase after `NormalizeForDisplay`; scanned sample → `NeedsOcr=true`.
**Done when:** extraction tests green; quality flags correct on the scanned sample; README explains how to run the sidecar if used.
**[H]:** put 3 small sample PDFs (Persian text, English text, scanned) in `tests/TestData/`.

---

## T06 — Chunker
**Goal:** `IChunker` + `ITokenCounter` per `SPEC-PERSIAN-AND-RAG.md` §3–5.
**Steps:** `HeaderFooterCleaner` (pure), `HeuristicTokenCounter` (multipliers from the T01 ADR), `LegalAwareChunker`. Marker regexes in one static class with tests.
**Tests:** `HeaderFooterCleanerTests` (repeated header removed, page numbers removed, short docs untouched); `ChunkerTests`: `Chunks_Never_Exceed_MaxTokens`, `Article_Starts_New_Chunk`, `Tiny_Article_Is_Merged_With_Next`, `Overlap_Only_Within_Same_Section`, `No_Text_Lost`, `Pages_Tracked_Correctly`, `SectionTitle_Contains_Chapter_And_Article`, `English_Document_Chunks_By_Paragraph`, `Deterministic_Output`, `Single_Giant_Sentence_Is_HardSplit`, `Empty_Input_ReturnsEmpty`; fixture text for a fake regulation (Persian) stored as test resource.
**Done when:** all invariants in SPEC §5 are tested and green.

---

## T07 — Embedding provider
**Goal:** configurable `IEmbeddingGenerator<string, Embedding<float>>`.
**Steps:** packages `Microsoft.Extensions.AI`, `OllamaSharp`, `Microsoft.Extensions.AI.OpenAI`. `AddEmbeddingProvider(config)`: `Ollama` → OllamaSharp client (it implements the MEAI interface — verify against the installed version), `OpenAICompatible`, `Fake` (SPEC §4). Decorators: `PrefixingEmbeddingGenerator` (query vs passage prefixes — expose two named usages: `IQueryEmbedder`/`IPassageEmbedder` thin wrappers or a method parameter; choose one, document), `BatchingEmbeddingGenerator` (batch size, order preserved, dimension check), `RetryingEmbeddingGenerator` (3 tries, backoff 200 ms/800 ms/3 s, only transient errors). `/health/ready` check "embedding" (lightweight call or endpoint ping).
**Tests (unit, Fake/stub inner):** `Batches_Are_Split_And_Reassembled_InOrder`, `Retries_On_Transient_Then_Succeeds`, `Does_Not_Retry_On_4xx`, `Throws_On_Dimension_Mismatch`, `Prefix_Applied_For_Query_And_Passage`, `FakeEmbedding_Is_Deterministic`, `FakeEmbedding_Related_Text_Scores_Higher`. **RequiresOllama:** `Ollama_Returns_1024_Dims_For_Persian_Text`.
**Done when:** unit tests green; (human) Ollama test green locally.
**[H]:** Ollama running with the chosen model.

---

## T08 — Vector store & retrieval (vector-only)
**Goal:** `IVectorStore` + `IRetriever` v1.
**Steps:** `PgVectorStore`: `AddChunksAsync` (batch insert in one transaction), `DeleteByDocumentAsync`, `SearchAsync` using the vector SQL of SPEC §6.2 (parameters via Npgsql; `Vector` type), filtering by collections, model, `Ready` documents. `Retriever`: normalize → embed query → `SearchAsync` → `RetrievalResult` (BestVectorScore). RRF pure function `RankFusion.Fuse(...)` implemented now with unit tests (used in T11).
**Tests:** unit: `RankFusionTests` (hand-computed examples, ties, missing lists). Integration: `Search_Returns_Nearest_First`, `Search_Excludes_Other_Collections` (ACL), `Search_Excludes_Other_Embedding_Models`, `Search_Excludes_NonReady_Documents`, `Search_With_Empty_Collection_List_Returns_Empty`, `Score_Is_One_Minus_Distance`.
**Done when:** ACL tests prove no leakage across collections.

---

## T09 — Upload & ingestion pipeline  [PLAN-FIRST]
**Goal:** upload endpoint + background ingestion with statuses.
**Steps:**
1. `LocalFileStorage` (`IFileStorage`): generated GUID file names under `Storage:RootPath/<yyyy>/<MM>/`, atomic write (temp + move), safe delete, path traversal impossible.
2. `DocumentsController` upload per SPEC §2.4: stream to storage while hashing (SHA-256), magic-byte check (`%PDF-`), size limit (Kestrel + form options), duplicate detection, create `Documents` row `Uploaded`, enqueue, return `202`.
3. `IngestionQueue` (bounded `Channel<Guid>`), `IngestionWorker : BackgroundService` (max concurrency from config, per-item try/catch, graceful shutdown), on startup re-enqueue documents in `Uploaded`/`Processing`.
4. `IngestionService.ProcessDocumentAsync`: set `Processing` → open file → extract pages → (OCR hook later) → `HeaderFooterCleaner` → chunk → embed chunk `EmbeddingText` in batches → **single transaction**: delete old chunks, insert new, set `Ready`, `page_count`, `processed_at`. On any failure: `Failed` + truncated `error_message`; never leave `Processing` forever.
5. Endpoints: list/get/delete/download/reprocess per spec. (Auth arrives in T13: until then use a **temporary** single-user stub `ICurrentUser` returning a fixed user, registered only in Development, with a comment-free TODO-free ADR noting it is replaced in T13.)
**Tests:** unit: `IngestionServiceTests` with fakes (happy path → Ready; extractor throws → Failed; embedding throws → Failed; reprocess replaces chunks; zero-text document → Failed with clear message). API tests (`WebApplicationFactory`, fake providers, Testcontainers DB): `Upload_Pdf_Returns_202`, `Upload_NonPdf_Returns_400`, `Upload_TooLarge_Returns_413`, `Upload_Duplicate_Returns_409`, `Document_Becomes_Ready_And_Has_Chunks`, `Delete_Removes_File_And_Chunks`, `Worker_Requeues_Stuck_Documents_On_Startup`.
**Verify (manual with a real Persian PDF, Ollama on):** upload via Scalar → status polls to `Ready`; `SELECT page_start, section_title, left(text,80) FROM chunks WHERE document_id=… ORDER BY chunk_index` looks sane.
**Done when:** the full flow works with a real PDF; all tests green.

---

## T10 — RAG ask endpoint
**Goal:** `/ask` per SPEC §2.5 and RAG spec §7.
**Steps:** `PromptBuilder` (pure; resource files `Prompts/system.v1.txt`), `CitationParser` (pure), `RagService`, `AskController`, conversation persistence, chat provider registration (`OpenAICompatible`, `Ollama`, `Fake`), degraded mode, not-found short-circuit, rate limiting hook (full limiter in T14).
**Tests:** unit: `PromptBuilderTests` (numbering, context budget, includes file/page/section), `CitationParserTests` (`[1]`, `[1][2]`, `[1, 2]`, `[۱]`? — Persian digits in brackets are **not** valid citations; out-of-range removed; no dangling brackets), `RagServiceTests` with fakes: `Normal_Answer_Has_Valid_Sources`, `Below_Threshold_Returns_NotFound_Without_Calling_LLM`, `LLM_Outage_Returns_Degraded_With_Sources`, `Refusal_Sentence_Sets_NotFound`, `Answer_Without_Citations_Falls_Back_To_All_Sources`, `Invalid_Citation_Is_Stripped`, `Conversation_Is_Persisted`. API tests: `Ask_Returns_200_With_Sources`, `Ask_Empty_Question_Returns_400`, `Ask_Unreadable_Collection_Returns_404`.
**Verify (manual):** with Gemini key via user-secrets and VPN: ask 5 real questions; turn VPN off → `degraded=true` with sources.
**Done when:** all tests green; manual verification pasted.
**[H]:** Gemini key (put it in `.env` as `Ai__Chat__ApiKey`; user-secrets only if a single copy of the repo is used); confirm the model id (`gemini-3.8-flash` is the documented default at the time of writing) and the OpenAI-compatible endpoint in the official docs, and check the free-tier rate limits.

---

## T11 — Hybrid retrieval  [PLAN-FIRST]
**Goal:** vector + FTS + reference boost with RRF (SPEC §6.3–6.6).
**Steps:** `TsQueryBuilder` (pure; stopwords, sanitization, OR query), `LegalReferenceExtractor` (pure), extend `PgVectorStore.SearchAsync` to run the lists (parameterized SQL, same transaction), fuse with `RankFusion`. `Rag:UseHybrid` toggle.
**Tests:** `TsQueryBuilderTests` (stopwords removed, injection characters `'` `&` `|` `:` `!` `(` `)` stripped, digits kept, empty → null), `LegalReferenceExtractorTests`, integration: `Exact_Article_Number_Query_Ranks_Article_First`, `Hybrid_Off_Equals_Vector_Only`, `Fts_Only_Match_Is_Included`, `ACL_Still_Enforced_In_Hybrid`.
**Done when:** tests green; before/after numbers from the eval harness (if available) recorded in the PR description.

---

## T12 — OCR for scanned pages
**Goal:** pages with `NeedsOcr` get text via Tesseract (`fas`+`eng`).
**Steps:** implement in the Python sidecar (`pytesseract` + PyMuPDF page render at `OcrDpi`) endpoint `POST /ocr` (multipart pdf + `pages=3,4,7`) → texts; C# `OcrPdfExtractor` decorator: run base extraction, then OCR only flagged pages, merge, record `ocr_pages`. Config `Extraction:OcrEnabled`. If Tesseract is unavailable, log a warning and continue without OCR.
**Tests:** sidecar pytest with a scanned sample (skipped if Tesseract missing); C# unit test with a fake OCR client verifying merge + `ocr_pages`.
**Done when:** scanned sample PDF becomes searchable (human verifies one question).
**[H]:** install Tesseract + `fas` traineddata (or use the Docker image defined in `docs/INFRA.md`).

---

## T13 — Auth, collections, ACL
**Goal:** real authentication/authorization per SPEC §2.2–2.3.
**Steps:** Identity + JWT; `AuthController`; `CollectionsController` (CRUD, members); `IAccessService` implementation (SQL-based readable collection ids; Admin sees all); `ICurrentUser` from claims; remove the T09 dev stub; bootstrap admin; authorization policies per role; all endpoints protected.
**Tests:** `Unauthenticated_Gets_401`, `NonMember_Gets_404_For_Collection_And_Document`, `Viewer_Cannot_Upload_Or_Delete`, `Editor_Can_Upload`, `Owner_Can_Manage_Members`, `Ask_Cannot_Bypass_Acl_Via_CollectionIds`, `Admin_Can_Call_Admin_Endpoints`, `Register_Disabled_Returns_403_When_Config_Off`, `Weak_Password_Rejected`, `Login_Wrong_Password_Returns_401`.
**Done when:** ACL matrix tests green; no endpoint is accidentally anonymous (add a reflection test listing all routes and their auth requirement).

---

## T14 — Production hardening
**Goal:** operability and safety.
**Steps:** rate limiting (policies from SPEC §2.7), request size limits, CORS from config, response compression, security headers (`X-Content-Type-Options`, `Referrer-Policy`, `X-Frame-Options` for the API), consistent ProblemDetails for all errors (incl. 401/403/404/429 from the pipeline), Serilog enrichers, optional OpenTelemetry console exporter, graceful shutdown, `dotnet list package --vulnerable` clean, README "Operations" section.
**Tests:** `RateLimit_Returns_429_With_RetryAfter`, `Errors_Are_ProblemJson`, `Cors_Allows_Only_Configured_Origin`, `Security_Headers_Present`.
**Done when:** a 20-concurrent-request smoke script (`conformance`-style, fake providers) passes without 5xx.

---

## T15 — Re-index & provider swap
**Goal:** safe change of embedding model/dimension and LLM provider.
**Steps:** `ReindexService` + `reindex_jobs` (re-embed all chunks of a collection from stored `embedding_text`-equivalent: recompute with the current normalizer from `text`), batch + resumable + progress, endpoints per spec; search ignores stale-model chunks. If the **dimension** changes: document the procedure (new migration altering the column + index, then reindex) in an ADR.
**Tests:** `Reindex_Updates_EmbeddingModel_And_Vectors`, `Search_Ignores_Chunks_Of_Old_Model_During_Reindex`, `Reindex_Is_Resumable`, `Only_Admin_Can_Reindex`.
**Done when:** switching `Ai:Embedding:Model` + reindex yields working retrieval (human demo).

---

## T16 — CI & integration hygiene
**Goal:** GitHub Actions per `docs/INFRA.md`: restore, build (`-warnaserror`), format check, tests (excluding `RequiresOllama`), vulnerable-package check; python sidecar tests (if exists); conformance suite run against the API started with fake providers (compose `ci` profile or `dotnet run` in the job).
**Done when:** green pipeline on `main` and on PRs; badge in README.

---

## T17 — Web UI (React + Vite + TypeScript, RTL)
**Goal:** small, clean, accessible UI. Keep scope tight.
**Structure:** `web/` with `src/api/` (typed client from OpenAPI or hand-written), `src/pages/` (Login, Collections, CollectionDetail with documents + upload, Chat), `src/components/` (SourceCard, StatusBadge, DegradedBanner, FileDrop), `src/i18n/fa.ts` (all strings), `src/styles/`.
**Requirements:** `<html lang="fa" dir="rtl">`; Vazirmatn font **self-hosted** (woff2 in `public/fonts`, no CDN); correct bidi for English terms (`<bdi>`/`unicode-bidi: plaintext` for filenames); upload with progress and status polling; chat shows answer + clickable sources (file, page, snippet); notFound and degraded states have distinct clear messages; token stored in memory (+ optional `sessionStorage`), not `localStorage`; answers rendered as escaped text (no `dangerouslySetInnerHTML`); keyboard accessible; loading/empty/error states everywhere.
**Tests:** Vitest + Testing Library: login flow, upload status polling (mocked), citation rendering, RTL attributes present, degraded banner.
**Verify:** `npm run build` succeeds; manual end-to-end with the API.
**Done when:** full flow works in the browser and tests pass.
**[H]:** install Node.js LTS.

---

## T18 — (Optional) Embeddable widget
**Goal:** `<docsearch-widget api-url="…" token="…" collection-ids="…">` Web Component (Vite library mode, single JS file), Shadow DOM, RTL, minimal UI (question box + answer + sources), documented CORS needs, demo `web/widget-demo.html`.
**Tests:** renders, calls API (mock), shows degraded/notFound states.
**Done when:** demo page works from a different origin (CORS config documented).

---

## T19 — Evaluation harness  [PLAN-FIRST]
**Goal:** reproducible experiments for the thesis (full spec in `docs/EVAL.md`).
**Steps:** complete `eval/run_eval.py`: retrieval metrics (Recall@K, MRR), answer metrics (normalized contains-match, optional LLM-judge), not-found precision/recall, latency percentiles; experiment runner reading `eval/experiments.yaml` (variants via env/config overrides + separate collections per chunking variant); outputs `eval/results/<run-id>/{results.json,summary.csv,report.md,*.png}`; bootstrap confidence intervals; `eval/README.md`.
**Done when:** one command reproduces all tables/plots; results deterministic for fixed config (except LLM calls, cached by hash).
**[H]:** verify `questions.json` quality.

---

## T20 — Packaging & documentation
**Goal:** one-command run + complete docs.
**Steps:** full compose (`api`, `extractor`, `web` under profile `full`), Dockerfiles, `host.docker.internal` for Ollama, healthchecks, `.env.example` complete, README (architecture Mermaid diagram, setup from zero on Windows, configuration reference table, API summary, troubleshooting: VPN/LLM down, Ollama not running, low RAM, Docker memory), ADR index, `docs/DEMO.md` (8–10 questions with expected behavior, degraded-mode demo, fallback plan), backup/restore notes, LICENSE decision (ask human), integration guide for the university website team (REST usage, widget, SSO notes).
**Done when:** a fresh clone + README steps works on a clean machine (human verifies).
**[H]:** record a backup demo video.

---

# Stretch tasks (use extra time **after** T19 is done and results are recorded)

Each stretch task needs its own ADR, tests, and an evaluation comparison against the baseline (T19 harness). Never merge a stretch feature that does not improve measured results or a clear operational need.

- **S1 — Reranker:** cross-encoder reranker (e.g., `bge-reranker-v2-m3`) via the Python sidecar; rerank top `CandidateK`; measure Recall/MRR and latency on CPU.
- **S2 — Conversation-aware query rewriting:** rewrite follow-up questions into standalone queries using chat history (LLM call, cached); evaluate on a multi-turn set.
- **S3 — Context expansion:** include neighbor chunks (`chunk_index ± 1`) for the top hits; measure faithfulness/correctness.
- **S4 — Answer cache:** cache by hash(normalized question, collection ids, embedding model, prompt version, retrieved chunk ids) for demo robustness and cost control; invalidation on document change.
- **S5 — Agentic RAG:** tool-calling loop (`search_documents`, `get_page`, `list_documents`) with step/token budgets for multi-hop questions ("difference between article 5 and 12"); compare with baseline on a multi-chunk question set.
- **S6 — SSO/OIDC:** integrate Keycloak (or the university IdP) via OIDC; map claims to roles/collections.
- **S7 — Feedback & analytics:** thumbs up/down on answers, admin dashboard of unanswered questions.
- **S8 — Document versioning & incremental re-ingestion:** replace a document while keeping history; only re-embed changed pages.
- **S9 — Multi-provider benchmark:** run the eval across ≥ 3 chat providers (local small model, Gemini, a paid frontier model) and report quality/latency/cost.
