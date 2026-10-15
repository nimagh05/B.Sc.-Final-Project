# DocSearch

Persian RAG (Retrieval-Augmented Generation) document search web service.
Answers questions strictly from uploaded PDF documents with exact file name and page citations.

## Status

Solution skeleton, Docker Compose (database), and health endpoints provisioned (Task T02 completed).

## Prerequisites

- .NET 10 SDK (`net10.0`)
- Docker Desktop with WSL2 (runs `pgvector/pgvector:pg16`)
- Windows PowerShell

## Quickstart

1. Configure environment:
   ```powershell
   Copy-Item .env.example .env
   # Edit POSTGRES_PASSWORD in .env
   . .\scripts\load-env.ps1
   ```

2. Start PostgreSQL + pgvector database:
   ```powershell
   docker compose up -d db
   docker compose ps
   ```

3. Run API:
   ```powershell
   dotnet run --project src/DocSearch.Api
   ```

4. Verify health endpoints:
   ```powershell
   Invoke-RestMethod http://localhost:5000/health/live
   Invoke-RestMethod http://localhost:5000/health/ready
   ```

5. Explore API documentation via Scalar:
   Open `http://localhost:5000/scalar/v1` in browser.

## Running Tests

```powershell
dotnet test
dotnet build -warnaserror
dotnet format --verify-no-changes
```

## Stop Services

```powershell
docker compose down
```
