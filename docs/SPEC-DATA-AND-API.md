# SPEC — Data model, API, configuration, auth, fakes

Source of truth for database schema, REST contracts, configuration keys, error format, and test doubles.
If reality forces a change, write an ADR and tell the human before changing it.

---

## 1. Database (PostgreSQL 16 + pgvector)

All identifiers `uuid` (generated in the application with `Guid.CreateVersion7()` or `Guid.NewGuid()`; pick one and be consistent). All timestamps `timestamptz` stored in UTC.
Identity tables (`AspNetUsers`, `AspNetRoles`, …) come from ASP.NET Core Identity; user ids are `text`.

### 1.1 Tables

```sql
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE TABLE collections (
  id           uuid PRIMARY KEY,
  name         varchar(200)  NOT NULL,
  description  varchar(2000) NULL,
  owner_id     text          NOT NULL REFERENCES "AspNetUsers"("Id"),
  created_at   timestamptz   NOT NULL DEFAULT now(),
  UNIQUE (owner_id, name)
);

CREATE TABLE collection_members (
  collection_id uuid NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
  user_id       text NOT NULL REFERENCES "AspNetUsers"("Id") ON DELETE CASCADE,
  role          text NOT NULL CHECK (role IN ('Viewer','Editor','Owner')),
  PRIMARY KEY (collection_id, user_id)
);

CREATE TABLE documents (
  id             uuid PRIMARY KEY,
  collection_id  uuid NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
  file_name      varchar(500)  NOT NULL,          -- sanitized original name (display only)
  storage_path   varchar(1000) NOT NULL,          -- generated key, never user input
  content_type   varchar(100)  NOT NULL,
  size_bytes     bigint        NOT NULL,
  content_hash   char(64)      NOT NULL,          -- sha256 hex of file bytes
  page_count     int           NULL,
  ocr_pages      int[]         NULL,              -- pages where OCR text was used
  status         text          NOT NULL CHECK (status IN ('Uploaded','Processing','Ready','Failed')),
  error_message  varchar(2000) NULL,
  uploaded_by    text          NOT NULL REFERENCES "AspNetUsers"("Id"),
  created_at     timestamptz   NOT NULL DEFAULT now(),
  processed_at   timestamptz   NULL,
  UNIQUE (collection_id, content_hash)
);

CREATE TABLE chunks (
  id               uuid PRIMARY KEY,
  document_id      uuid NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
  collection_id    uuid NOT NULL,                  -- denormalized for fast ACL filtering
  chunk_index      int  NOT NULL,
  page_start       int  NOT NULL,
  page_end         int  NOT NULL,
  section_title    varchar(500) NULL,
  text             text NOT NULL,                  -- display text (NormalizeForDisplay)
  search_text      text NOT NULL,                  -- NormalizeForSearch(text)
  token_count      int  NOT NULL,
  embedding        vector(1024) NOT NULL,          -- dimension = Ai:Embedding:Dimensions
  embedding_model  varchar(200) NOT NULL,
  tsv              tsvector GENERATED ALWAYS AS (to_tsvector('simple', search_text)) STORED,
  UNIQUE (document_id, chunk_index)
);

CREATE INDEX ix_chunks_collection      ON chunks (collection_id);
CREATE INDEX ix_chunks_embedding_hnsw  ON chunks USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 64);
CREATE INDEX ix_chunks_tsv             ON chunks USING gin (tsv);
CREATE INDEX ix_chunks_search_trgm     ON chunks USING gin (search_text gin_trgm_ops);
CREATE INDEX ix_documents_collection   ON documents (collection_id, status);

CREATE TABLE conversations (
  id uuid PRIMARY KEY, user_id text NOT NULL REFERENCES "AspNetUsers"("Id") ON DELETE CASCADE,
  title varchar(300) NULL, created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE messages (
  id uuid PRIMARY KEY, conversation_id uuid NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
  role text NOT NULL CHECK (role IN ('user','assistant')),
  content text NOT NULL,
  sources_json jsonb NULL,                -- array of SourceCitation
  degraded boolean NOT NULL DEFAULT false,
  not_found boolean NOT NULL DEFAULT false,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_messages_conv ON messages (conversation_id, created_at);

CREATE TABLE reindex_jobs (
  id uuid PRIMARY KEY, collection_id uuid NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
  status text NOT NULL CHECK (status IN ('Queued','Running','Completed','Failed')),
  total_chunks int NOT NULL DEFAULT 0, done_chunks int NOT NULL DEFAULT 0,
  embedding_model varchar(200) NOT NULL, error_message varchar(2000) NULL,
  created_at timestamptz NOT NULL DEFAULT now(), finished_at timestamptz NULL
);
```

### 1.2 EF Core mapping notes
- `UseNpgsql(cs, o => o.UseVector())`; map `Pgvector.Vector Embedding` with `HasColumnType("vector(1024)")` (dimension from a single constant/config, validated at startup).
- `tsv`: `NpgsqlTsVector` with `HasComputedColumnSql("to_tsvector('simple', search_text)", stored: true)`; never write to it.
- Enums stored as strings (`HasConversion<string>()`) matching the CHECK constraints.
- First migration `Initial` must run `CREATE EXTENSION IF NOT EXISTS vector; CREATE EXTENSION IF NOT EXISTS pg_trgm;` before creating tables.
- Cascade deletes are done by the DB (`ON DELETE CASCADE`); the application additionally deletes the stored file.
- Migrations command (from repo root):
  `dotnet ef migrations add <Name> -p src/DocSearch.Infrastructure -s src/DocSearch.Api`
  `dotnet ef database update -p src/DocSearch.Infrastructure -s src/DocSearch.Api`
- Provide a design-time `IDesignTimeDbContextFactory` that reads the connection string from env var `ConnectionStrings__Default`.

### 1.3 Invariants (must hold; test them)
1. A `Ready` document has ≥ 1 chunk; a `Failed` document has `error_message`.
2. Every chunk's `collection_id` equals its document's `collection_id`.
3. All chunks of one collection that are searched share the same `embedding_model` as the active configuration; others are ignored by search.
4. `page_start ≤ page_end`, pages are 1-based.
5. Deleting a document deletes its chunks and its file.

---

## 2. REST API (v1)

Base path `/api/v1`. JSON (camelCase). All timestamps ISO-8601 UTC. IDs are GUID strings. Auth: `Authorization: Bearer <jwt>`.
OpenAPI document served at `/openapi/v1.json`, UI at `/scalar` (Development and when `Docs:Enabled=true`).

### 2.1 Error format (RFC 7807 `application/problem+json`)

```json
{
  "type": "https://docsearch.local/errors/document.invalid_file",
  "title": "Invalid file",
  "status": 400,
  "detail": "Only PDF files are accepted.",
  "code": "document.invalid_file",
  "traceId": "00-abc…",
  "errors": { "file": ["Only PDF files are accepted."] }
}
```

| code | status | when |
|---|---|---|
| `validation.failed` | 400 | model validation errors (`errors` filled) |
| `document.invalid_file` | 400 | not a PDF / corrupt / empty |
| `document.too_large` | 413 | larger than `Storage:MaxUploadMb` |
| `document.duplicate` | 409 | same hash already in that collection (`detail` includes existing document id) |
| `auth.invalid_credentials` | 401 | wrong login |
| `auth.unauthorized` | 401 | missing/invalid token |
| `auth.forbidden` | 403 | authenticated but role not allowed (e.g. non-admin on admin endpoint) |
| `resource.not_found` | 404 | not found **or not visible to the caller** |
| `collection.name_taken` | 409 | duplicate collection name for owner |
| `rag.invalid_scope` | 400 | `collectionIds` empty/contains unreadable ids (treated as not found → 404 when none readable) |
| `rate.limited` | 429 | rate limit hit (include `Retry-After`) |
| `server.error` | 500 | unexpected; no internals leaked |

LLM outage is **not** an error for `/ask`: it returns `200` with `degraded=true` (see §2.5).

### 2.2 Auth

`POST /auth/register` `{ "email": "a@b.com", "password": "Str0ng!pass", "displayName": "نیما" }` → `201 { "id": "...", "email": "a@b.com" }`
 - Allowed only if `Auth:AllowSelfRegistration=true`. Password: ≥ 10 chars, upper+lower+digit.
`POST /auth/login` `{ "email", "password" }` → `200 { "accessToken": "...", "expiresAtUtc": "...", "roles": ["User"] }`
JWT claims: `sub` (user id), `email`, `role`, `jti`, `exp`. Lifetime `Jwt:ExpiryMinutes` (default 60). (Refresh tokens: stretch only.)
Bootstrap admin: on startup, if no users exist and `Auth:BootstrapAdmin:Email/Password` are set, create that user with role `Admin`.

### 2.3 Collections

| Method & path | Body | Success | Notes |
|---|---|---|---|
| `GET /collections` | – | `200 { items:[{id,name,description,role,documentCount,createdAt}], total, page, pageSize }` | only collections the user is a member of; paging `?page=1&pageSize=20` (max 100) |
| `POST /collections` | `{ name, description? }` | `201` + object, `Location` header | creator becomes `Owner` |
| `GET /collections/{id}` | – | `200` | 404 if not visible |
| `PUT /collections/{id}` | `{ name, description? }` | `200` | Owner/Editor |
| `DELETE /collections/{id}` | – | `204` | Owner only; deletes docs, files, chunks |
| `GET /collections/{id}/members` | – | `200 [{userId,email,role}]` | Owner |
| `PUT /collections/{id}/members` | `{ email, role }` | `200` | Owner; upsert member |
| `DELETE /collections/{id}/members/{userId}` | – | `204` | Owner |

Role permissions: **Viewer**: read docs, ask. **Editor**: + upload/delete documents. **Owner**: + members, rename, delete collection. Global role `Admin` may read everything and call `/admin/*`.

### 2.4 Documents

`POST /collections/{id}/documents` — `multipart/form-data`, field `file` (one PDF).
 - Validate: size, magic bytes `%PDF-`, non-empty, content type; compute sha256 while streaming to storage.
 - Duplicate hash in the same collection → `409 document.duplicate`.
 - Response `202 Accepted`, `Location: /api/v1/documents/{id}`:
```json
{ "id":"…","collectionId":"…","fileName":"aeen-name.pdf","sizeBytes":123456,"status":"Uploaded","pageCount":null,"createdAt":"…" }
```
`GET /collections/{id}/documents?page=&pageSize=&status=` → paged list `{items:[…], total, page, pageSize}`.
`GET /documents/{id}` → `{ id, collectionId, fileName, sizeBytes, status, errorMessage, pageCount, ocrPages, chunkCount, createdAt, processedAt }`.
`GET /documents/{id}/file` → original PDF (`application/pdf`, `Content-Disposition: attachment`), Viewer+.
`DELETE /documents/{id}` → `204` (Editor+): delete chunks (DB cascade), stored file, row.
`POST /documents/{id}/reprocess` → `202` (Editor+): re-run ingestion (delete old chunks first).

### 2.5 Ask

`POST /ask`
```json
{ "question": "حداکثر واحد مجاز در ترم چند واحد است؟", "collectionIds": ["…"], "conversationId": null, "topK": 6 }
```
Validation: `question` 3–2000 chars after trim; `collectionIds` 1–20 ids; `topK` 1–20 (default `Rag:TopK`).
Success `200`:
```json
{
  "answer": "حداکثر ۲۰ واحد است [1].",
  "notFound": false,
  "degraded": false,
  "conversationId": "…",
  "sources": [
    { "index": 1, "documentId": "…", "fileName": "aeen-name.pdf", "page": 12,
      "sectionTitle": "ماده ۱۲", "snippet": "…(≤ 300 chars of the chunk text)…", "score": 0.78 }
  ]
}
```
Rules:
- `notFound=true` → `answer` is the fixed Persian refusal sentence (see RAG spec), `sources` may be empty or the nearest chunks (configurable; default empty).
- `degraded=true` → `answer=null`, `sources` = retrieved chunks (retrieval-only mode when the LLM is unreachable/times out).
- A normal answer always has ≥ 1 source and every `[n]` in the text maps to `sources[n-1]`.
- Collections the user cannot read are removed from scope; if none remain → `404 resource.not_found`.

`GET /conversations?page=` → user's conversations. `GET /conversations/{id}/messages` → messages with `sources`. `DELETE /conversations/{id}` → `204`.

### 2.6 Admin & health

`POST /admin/collections/{id}/reindex` (role Admin) → `202 { "jobId": "…" }`; `GET /admin/reindex-jobs/{jobId}` → `{ status, totalChunks, doneChunks, errorMessage }`.
`GET /health/live` → 200 if process up. `GET /health/ready` → checks DB, embedding endpoint reachability (HEAD/GET), extractor (if configured); JSON `{ status, checks:[{name,status,durationMs}] }`; **never** includes secrets. LLM availability is **not** part of readiness (it may legitimately be down).

### 2.7 Rate limits (defaults, configurable)
`/auth/*`: 10 req/min/IP. `/ask`: 30 req/min/user. Upload: 20/hour/user. Return `429` with `Retry-After`.

---

## 3. Configuration (`appsettings.json`; env vars use `__`)

```json
{
  "ConnectionStrings": { "Default": "Host=localhost;Port=5432;Database=docsearch;Username=postgres;Password=CHANGE_ME" },
  "Ai": {
    "Embedding": {
      "Provider": "Ollama",            // Ollama | OpenAICompatible | Fake
      "Model": "bge-m3",
      "Endpoint": "http://localhost:11434",
      "ApiKey": null,
      "Dimensions": 1024,
      "QueryPrefix": "",
      "PassagePrefix": "",
      "BatchSize": 16,
      "TimeoutSeconds": 120
    },
    "Chat": {
      "Provider": "OpenAICompatible",  // OpenAICompatible | Ollama | Fake
      "Model": "gemini-3.8-flash",     // model id documented by Google in Oct 2026; confirm in the official docs at T10 (names change)
      "Endpoint": "https://generativelanguage.googleapis.com/v1beta/openai/",
      "ApiKey": null,                  // user-secrets / env only
      "TimeoutSeconds": 60,
      "Temperature": 0.1,
      "MaxOutputTokens": 800
    }
  },
  "Rag": {
    "TopK": 6, "CandidateK": 30, "MinSimilarity": 0.50, "MaxContextTokens": 4000,
    "UseHybrid": true, "RrfK": 60, "ReturnSourcesWhenNotFound": false, "PromptVersion": "v1"
  },
  "Chunking": { "TargetTokens": 500, "MaxTokens": 700, "MinTokens": 80, "OverlapRatio": 0.15, "RespectLegalStructure": true },
  "Extraction": { "Provider": "PdfPig", "ExtractorBaseUrl": "http://localhost:8000", "OcrEnabled": false, "OcrDpi": 250, "MinPageQuality": 0.35 },
  "Storage": { "RootPath": "./data/files", "MaxUploadMb": 50 },
  "Ingestion": { "MaxConcurrentDocuments": 1, "QueueCapacity": 100 },
  "Auth": { "AllowSelfRegistration": true, "BootstrapAdmin": { "Email": null, "Password": null } },
  "Jwt": { "Issuer": "docsearch", "Audience": "docsearch", "SigningKey": "<from user-secrets, >=32 bytes>", "ExpiryMinutes": 60 },
  "Cors": { "AllowedOrigins": [ "http://localhost:5173" ] },
  "Docs": { "Enabled": true }
}
```
(JSON above has comments only for explanation; real `appsettings.json` must be valid JSON without comments.)

- Secrets (`Ai:Chat:ApiKey`, `Jwt:SigningKey`, DB password) → `dotnet user-secrets set "Ai:Chat:ApiKey" "<key>" --project src/DocSearch.Api` or environment variables (`Ai__Chat__ApiKey`).
- Validation at startup: required values present; `Embedding.Dimensions` equals the vector column dimension; `MinSimilarity` in [0,1]; `OverlapRatio` in [0,0.5]; `TargetTokens < MaxTokens`.
- `appsettings.Development.json` may be committed only if it has **no secrets**.
- Env var overrides used by tests/conformance: `Ai__Embedding__Provider=Fake`, `Ai__Chat__Provider=Fake`, `Rag__MinSimilarity=0.2`.

---

## 4. Fake providers (required; used by tests, CI, conformance, bake-off)

Implement in `DocSearch.Infrastructure/Ai/Fakes/`, selectable by `Provider=Fake`.

**FakeEmbeddingGenerator** (`IEmbeddingGenerator<string, Embedding<float>>`) — deterministic, no network:
1. Normalize with `NormalizeForSearch`, split into word tokens on non-letter/non-digit.
2. For each token compute 32-bit FNV-1a hash `h`; index = `h % Dimensions`; sign = `+1` if bit 31 of `h` is 0 else `-1`; add sign to that vector slot.
3. L2-normalize (zero vector stays zero).
4. Texts sharing words have positive cosine; unrelated texts ≈ 0.
Dimension from `Ai:Embedding:Dimensions`.

**FakeChatClient** (`IChatClient`):
- Normal mode: finds the first `[1]`-style excerpt in the user message and returns `"پاسخ آزمایشی بر اساس منبع [1]."`; if no excerpts, returns the refusal sentence.
- `Ai:Chat:Fake:Fail=true` → throws `HttpRequestException` (simulates outage); `Ai:Chat:Fake:DelayMs=N` simulates latency (for timeout tests).

**FakePdfExtractor** is not needed; use real extractors with small test PDFs.

---

## 5. Observability
- Serilog console (compact JSON in containers) + rolling file `logs/docsearch-.log` (7 days). Enrich with `RequestId`, `UserId` (hash or id, never email in logs), `DocumentId` where relevant.
- Log events (Info): `DocumentUploaded`, `IngestionStarted/Completed/Failed (elapsed, pages, chunks)`, `AskCompleted (elapsedMs, retrievedCount, bestScore, notFound, degraded)`. No texts.
- Optional (T14): OpenTelemetry traces/metrics to console exporter.
