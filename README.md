# DocSearch

Persian RAG (Retrieval-Augmented Generation) document search web service.
Answers questions strictly from uploaded PDF documents with exact file name and page citations.

## Status

PDF extraction adapter and sidecar with quality scoring operational (Task T05 completed).

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

2. Run PyMuPDF extractor sidecar:
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
docker compose down
```
