# DocSearch

Persian RAG (Retrieval-Augmented Generation) document search web service.
Designed for university regulations, academic papers, and administrative documents.
Answers questions strictly from uploaded PDF documents with exact file name and page citations.

## Status

Repository bootstrap (Task T00 completed).
Core specifications, coding conventions, and repository standards are established.

## Phased Roadmap

Implementation follows the structured task plan:
- Phased roadmap: [TASKS.md](docs/TASKS.md)
- Architectural conventions: [CONVENTIONS.md](docs/CONVENTIONS.md)
- Data & API specifications: [SPEC-DATA-AND-API.md](docs/SPEC-DATA-AND-API.md)
- Persian NLP & RAG specifications: [SPEC-PERSIAN-AND-RAG.md](docs/SPEC-PERSIAN-AND-RAG.md)
- Infrastructure & Docker specifications: [INFRA.md](docs/INFRA.md)

## Development Prerequisites

- Windows PowerShell dev environment (Linux container deployment target)
- .NET 10 SDK (`net10.0`)
- Docker Desktop with WSL2
- Ollama running on host (`http://localhost:11434`, CPU-only)
- Python 3.11 (virtual environment at `.venv`)

## Getting Started

1. Check .NET SDK version:
   ```powershell
   dotnet --version
   ```
2. Copy environment template:
   ```powershell
   Copy-Item .env.example .env
   ```
3. Database and API service scaffolding will be provisioned in Task T02.
