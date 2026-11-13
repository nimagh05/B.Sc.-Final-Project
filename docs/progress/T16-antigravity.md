# Task T16 Handoff Note — Antigravity Agent

**Task:** T16 — CI & Integration Hygiene  
**Branch:** `feature/T16-ci`  
**Date:** 2026-11-13  
**Status:** Completed  

---

## 1. What was Completed

1. **GitHub Actions Workflow (`.github/workflows/ci.yml`)**:
   - Defined top-level permissions (`contents: read`) and concurrency group (`${{ github.workflow }}-${{ github.ref }}`) with `cancel-in-progress: true`.
   - Pinned all GitHub Actions to major releases (`actions/checkout@v4`, `actions/setup-dotnet@v4`, `actions/setup-python@v5`, `actions/cache@v4`, `actions/upload-artifact@v4`).
   - **`dotnet` Job**:
     - Sets up .NET 10 using `global.json`.
     - Explicit NuGet caching (`~/.nuget/packages`) keyed on `Directory.Packages.props` and `**/*.csproj`.
     - Scans for accidental secrets using `git grep -nI -E "AIza[0-9A-Za-z_-]{35}|sk-[A-Za-z0-9]{20,}|Password=[^$\s]+"`, filtering out false-positive placeholders (`CHANGE_ME`, `postgres`, `*.example`, docs).
     - Restores, builds (`-warnaserror -c Release`), and verifies code formatting (`dotnet format --verify-no-changes --no-restore`).
     - Executes unit and integration test suite (`dotnet test --no-build -c Release --filter "Category!=RequiresOllama"`).
     - Executes `dotnet list package --vulnerable --include-transitive` and fails the pipeline (`exit 1`) if any vulnerabilities are reported.
   - **`extractor` Job**:
     - Sets up Python 3.11 with pip caching.
     - Installs `tools/extractor/requirements-dev.txt`.
     - Executes `pytest tools/extractor/tests -v`.
   - **`conformance` Job** (`needs: [dotnet, extractor]`):
     - Starts PostgreSQL 16 + pgvector container via `docker compose up -d --wait db` with readiness polling.
     - Builds `src/DocSearch.Api -c Release` and applies database migrations via `dotnet ef database update --configuration Release --no-build`.
     - Spawns the Python PyMuPDF extractor sidecar on `127.0.0.1:8000` via `uvicorn` and verifies `/health`.
     - **Pass A (Standard Contracts)**: Runs Kestrel API with Fake AI providers, verifies readiness via `/health/ready`, and runs `pytest conformance -m "not degraded" -v --junitxml=conformance/results/pass_a.xml`.
     - **Pass B (Degraded Mode)**: Restarts Kestrel API with `Ai__Chat__Fake__Fail=true`, verifies readiness, and runs `pytest conformance -m degraded -v --junitxml=conformance/results/pass_b.xml`.
     - Merges test results into `conformance/results/report.xml` and runs `conformance/score.py` to produce a scorecard across all 8 functional areas.
     - Uploads JUnit XML reports as workflow artifacts.
2. **README CI Badge**:
   - Added CI workflow status badge linked to the GitHub repository actions.

---

## 2. Known Gaps & Skipped Tests in CI

In accordance with project design, test files dependent on local evaluation artifacts (`eval/experiments/out/`, which is git-ignored) or live local Ollama GPU/CPU daemon are dynamically skipped in CI via `[SkippableFact]`:

### Parity Tests Requiring Local `eval/experiments/out/` (Skipped in CI):
1. `DocSearch.Application.Tests.HeuristicTokenCounterTests.Parity_With_TokenCalibrationCsv` (requires `eval/experiments/out/token_calibration.csv`)
2. `DocSearch.Application.Tests.LegalAwareChunkerTests.ArticleKeys_Aein91RealPdf_CapturesAll35Articles_And_PreservesHashes` (requires `eval/experiments/out/extract/pymupdf/aein-91.pdf`)
3. `DocSearch.Application.Tests.PageQualityScorerTests.T01_Parity_With_PyMuPDF_Extraction_Summary` (requires `eval/experiments/out/extract_summary.csv`)
4. `DocSearch.Application.Tests.TextDefectDetectorTests.Parity_With_BenchmarkPdfs_Separation` (requires `eval/experiments/out/extract_summary.csv`)
5. `DocSearch.Application.Tests.Chunking.ChunkEmbeddingTextBuilderTests.Reindex_Text_Matches_All_201_Benchmark_Chunks` (requires `eval/experiments/out/embed_benchmark_chunks.csv`)
6. `DocSearch.Infrastructure.Tests.Extraction.HttpPdfExtractorTests.ExtractAsync_PersianSample_MatchesInvariantsAndPhrase` (requires local extractor or benchmark PDF artifact)
7. `DocSearch.Infrastructure.Tests.Extraction.HttpPdfExtractorTests.ExtractAsync_EnglishSample_MatchesInvariants`
8. `DocSearch.Infrastructure.Tests.Extraction.HttpPdfExtractorTests.ExtractAsync_ScannedSample_RoutesToOcr`

### Host Ollama Integration Tests (Filtered out by `--filter "Category!=RequiresOllama"`):
9. `DocSearch.Infrastructure.Tests.Integration.OllamaEmbeddingIntegrationTests.Ollama_Returns_1024_Dims_For_Persian_Text`
10. `DocSearch.Infrastructure.Tests.Integration.OllamaEmbeddingIntegrationTests.Ollama_Truncate_False_Throws_On_Overlength_Input`

---

## 3. How to Continue & Verification Commands

To verify locally before pushing:
```powershell
# 1. Format check
dotnet format --verify-no-changes

# 2. Build with warnings as errors
dotnet build -warnaserror -c Release

# 3. Local test run
dotnet test --filter "Category!=RequiresOllama"

# 4. Extractor sidecar tests
.\.venv\Scripts\python.exe -m pytest tools/extractor/tests -v

# 5. Conformance test dry-run collection
.\.venv\Scripts\python.exe -m pytest conformance --collect-only

# 6. Check vulnerable packages
dotnet list package --vulnerable --include-transitive
```
