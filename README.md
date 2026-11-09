# DocSearch

Persian RAG (Retrieval-Augmented Generation) document search web service. Answers questions strictly from uploaded PDF documents with exact file name and page citations.

## Status

Core RAG pipeline operational through **Task T14 (Production Hardening)**:
- FastAPI/PyMuPDF extraction sidecar with page quality scoring (T05)
- Header/footer cleaning & legal-aware Persian chunking with structural hierarchy (T06 & T06b)
- Offline & local Ollama (bge-m3) embedding providers with dimension validation (T07)
- PostgreSQL / pgvector HNSW vector store, FTS & article-key hybrid retrieval (T08 & T11)
- Background ingestion queue with streaming PDF upload and SHA-256 deduplication (T09)
- RAG synthesis pipeline, grounding prompts, citation parser & /api/v1/ask (T10)
- Identity & JWT auth, collections CRUD, and role-based ACL enforcement (T13)
- Production hardening: rate limiting, security headers, strict CORS & RFC 7807 errors (T14)

## Prerequisites

- .NET 10 SDK (net10.0), Docker Desktop (WSL2), Windows PowerShell, Python 3.11+ (.venv)

## Quick Start

```
powershell
Copy-Item .env.example .env
. .\scripts\load-env.ps1
docker compose up -d db extractor
dotnet ef database update --project src/DocSearch.Infrastructure --startup-project src/DocSearch.Api
dotnet run --project src/DocSearch.Api
```

API docs: http://localhost:5000/scalar/v1

## Operations

- **Health Checks**: /health/live (liveness), /health/ready (DB, embedding, extractor readiness).
- **Rate Limiting** (429 rate.limited + Retry-After): /api/v1/auth/* (10/min/IP:account), /api/v1/ask (30/min/user), POST /api/v1/collections/{id}/documents (20/hr/user).
- **Security & Observability**: Strict Cors:AllowedOrigins, X-Forwarded-For/Proto, security headers (nosniff, no-referrer, DENY), response compression, 15s graceful shutdown, daily JSON logs.

## Running Tests

```
powershell
dotnet format --verify-no-changes
dotnet build -warnaserror
dotnet test --filter "Category!=RequiresOllama"
```
