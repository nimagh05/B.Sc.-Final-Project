# DocSearch

Persian RAG (Retrieval-Augmented Generation) document search web service.
Answers questions strictly from uploaded PDF documents with exact file name and page citations.

## Status

Core RAG pipeline operational through **Task T10 + T06b Review Fixes**:
- FastAPI/PyMuPDF extraction sidecar with page quality scoring (T05)
- Header/footer cleaning & legal-aware Persian chunking with structural marker hierarchy (T06 & T06b)
- Offline & local Ollama embedding providers with dimension validation (T07)
- PostgreSQL / pgvector HNSW vector store & multi-collection ACL retrieval (T08)
- Background ingestion queue with streaming PDF upload and SHA-256 deduplication (T09)
- RAG synthesis pipeline, strict grounding prompts, citation parser, self-refusal detection, degraded mode fallback, and versioned `/api/v1/ask` endpoint (T10 & T06b)

## Prerequisites

- .NET 10 SDK (`net10.0`)
- Docker Desktop with WSL2 (runs `pgvector/pgvector:pg16`)
- Python 3.11 with `.venv` and Ollama on host (`bge-m3`)
- Windows PowerShell

## Quickstart

1. Configure environment and apply migrations:
   ```powershell
   Copy-Item .env.example .env
   . .\scripts\load-env.ps1
   docker compose up -d db
   dotnet ef database update --project src/DocSearch.Infrastructure --startup-project src/DocSearch.Api
   ```

2. Run PyMuPDF extractor sidecar (`http://127.0.0.1:8000`):
   ```powershell
   .venv\Scripts\uvicorn.exe app.main:app --app-dir tools/extractor --port 8000
   ```

3. Run API and verify health:
   ```powershell
   dotnet run --project src/DocSearch.Api
   Invoke-RestMethod http://localhost:5000/health/ready
   ```

API docs: `http://localhost:5000/scalar/v1`

## Running Tests

```powershell
# Sidecar pytest suite
.venv\Scripts\pytest.exe tools/extractor/tests
# Unit tests only
dotnet test --filter "Category!=Integration&Category!=RequiresOllama"
# All tests (including Testcontainers PostgreSQL integration)
dotnet test --filter "Category!=RequiresOllama"
dotnet format --verify-no-changes
```

## Stop Services

```powershell
docker compose --profile "*" down
```
