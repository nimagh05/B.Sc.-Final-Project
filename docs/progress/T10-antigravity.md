# Task T10 — RAG Pipeline, LLM Provider & `/ask` Endpoint

**Agent:** Antigravity  
**Branch:** `feature/T10-rag-ask`  
**Status:** Completed  

---

## 1. What was done
- Added `PromptBuilder` (`IPromptBuilder`) and versioned embedded system prompt (`Prompts/system.v1.txt`) packing context within token budgets (`RagOptions.MaxContextTokens = 4000`) using display text with Persian digits intact.
- Added `CitationParser` (`ICitationParser`) supporting `[1]`, `[1][2]`, `[1, 2]`, `[1، 2]`. Out-of-range citations (e.g. `[99]`) and non-ASCII/Persian bracketed digits (e.g. `[۱]`) are cleanly stripped without dangling brackets. Renumbers cited excerpts sequentially so that `every [n] in text maps to sources[n-1]`.
- Added encoding guard tests ensuring `RagConstants.PersianRefusalSentence` matches the exact Unicode-escaped sequence `"\u00AB\u062F\u0631 \u0627\u0633\u0646\u0627\u062F \u0645\u0648\u062C\u0648\u062F \u067E\u0627\u0633\u062E\u06CC \u0628\u0631\u0627\u06CC \u0627\u06CC\u0646 \u067E\u0631\u0633\u0634 \u067E\u06CC\u062F\u0627 \u0646\u0634\u062F.\u00BB"`.
- Implemented `FakeChatClient` (`IChatClient`) with deterministic excerpt-grounded responses, refusal logic, latency simulation, and configurable outage simulation (`Ai:Chat:Fake:Fail`).
- Implemented multi-provider `AddChatProvider` in `DocSearch.Infrastructure/DependencyInjection.cs` supporting `Fake`, `Ollama` (with isolated `"OllamaChatClient"` HttpClient and model), and `OpenAICompatible` (Gemini via OpenAI-compatible endpoint).
- Implemented `AccessService` (`IAccessService`) enforcing role-based ACL (Viewer/Editor/Owner) on collections, with tests covering Admin, Owner, Viewer, Editor, and Non-member roles.
- Implemented `RagService` (`IRagService`) with:
  - Scope verification (unreadable collections stripped, 404 if none readable).
  - Pre-LLM refusal short-circuit when zero chunks or similarity `< RagOptions.MinSimilarity = 0.55`.
  - Resilient LLM invocation with a single linked `CancellationTokenSource` bounded by `AiOptions.Chat.TimeoutSeconds`, passing `Temperature = (float)_ragOptions.Value.Temperature` and `MaxOutputTokens = _aiOptions.Value.Chat.MaxOutputTokens`.
  - Degraded mode fallback on `HttpRequestException`, `TimeoutException`, `ClientResultException`, and timeout-induced `OperationCanceledException`, while strictly rethrowing on caller cancellation.
  - Post-LLM self-refusal detection matching `RagConstants.PersianRefusalCorePhrase` ("در اسناد موجود پاسخی برای این پرسش پیدا نشد") with bracket citation stripping, setting `NotFound = true`, `NoAnswerReason = ModelRefusal`, returning canonical guillemet sentence `RagConstants.PersianRefusalSentence`, and respecting `ReturnSourcesWhenNotFound`.
  - Multi-turn conversation and message persistence, with strict user-level IDOR protection (404 when referencing or accessing another user's conversation ID, including for administrators).
  - Structured Serilog event `AskCompleted`.
- Implemented `AskController` (`POST /api/v1/ask`) and `ConversationsController` (`GET /api/v1/conversations`, `GET /api/v1/conversations/{id}/messages`, `DELETE /api/v1/conversations/{id}`).
- Updated confirmed working Gemini chat model ID to `gemini-3.5-flash-lite` in configuration and defaults (replacing `gemini-3.8-flash` which returned 503).
- Added comprehensive unit and integration tests across Domain, Application, Infrastructure, and API layers (all tests passing, 0 failing).

---

## 2. Architectural Decisions & Deviations from Initial Plan
1. **Placement of `RagService` implementation:**
   Placed in `DocSearch.Infrastructure/Rag/RagService.cs` (and its integration tests in `DocSearch.Infrastructure.Tests/Rag/RagServiceTests.cs`) rather than `DocSearch.Application`. This follows Clean Architecture (`CONVENTIONS.md` §2: "interfaces in Application, implementations in Infrastructure") as `RagService` directly persists conversations and messages into PostgreSQL via `AppDbContext`, keeping `DocSearch.Application` free of EF Core references.
2. **Placement of `AskDtos`:**
   Placed in `DocSearch.Application/Models/AskDtos.cs` alongside `DocumentDtos.cs` rather than `DocSearch.Api`, allowing domain/application services and API contracts to share DTO definitions cleanly.
3. **Inclusion of `ConversationsController`:**
   Added in T10 (`GET /conversations`, `GET /conversations/{id}/messages`, `DELETE /conversations/{id}`) to fulfill the conversation endpoints specified in `SPEC-DATA-AND-API.md` §2.5 and satisfy multi-turn chat assertions in `conformance/test_conformance.py` (Test #24).
4. **Configuration of `ApiBehaviorOptions` in `Program.cs`:**
   Chained `.ConfigureApiBehaviorOptions(...)` directly onto `.AddControllers()` so that automatic `[ApiController]` model validation triggers the custom RFC 7807 `validation.failed` ProblemDetails factory across all endpoints.

---

## 3. Verification Commands
```powershell
# Format check
dotnet format --verify-no-changes

# Build check
dotnet build -warnaserror

# Unit tests
dotnet test --filter "Category!=RequiresOllama&Category!=Integration"

# All tests (including Testcontainers integration tests)
dotnet test --filter "Category!=RequiresOllama"
```

---

## 4. Recommended Next Task
- **`T06b-chunker-followup`:** Chunker follow-up (populating `section_title` at paragraph start in `FlushParagraph` and `ArticleNumbers` on `ChunkDraft`), followed by **`T11 — Hybrid retrieval [PLAN-FIRST]`**.
