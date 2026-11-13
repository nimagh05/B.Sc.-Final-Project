# Task T16 Handoff Note — Antigravity Agent

**Task:** T16 — CI & Integration Hygiene (Linux / Ubuntu CI Follow-up)  
**Branch:** `fix/T16-ci-linux`  
**Date:** 2026-11-13  
**Status:** Completed  

---

## 1. What was Completed

1. **GitHub Actions Workflow (`.github/workflows/ci.yml`)**:
   - Defined top-level permissions (`contents: read`) and concurrency group (`${{ github.workflow }}-${{ github.ref }}`) with `cancel-in-progress: true`.
   - Pinned all GitHub Actions to major releases (`actions/checkout@v4`, `actions/setup-dotnet@v4`, `actions/setup-python@v5`, `actions/cache@v4`, `actions/upload-artifact@v4`).
   - **`dotnet` Job**:
     - Sets up .NET 10 using `global.json` with explicit NuGet package caching (`~/.nuget/packages`).
     - Automated secret scanning: excludes `.github/workflows/ci.yml`, documentation, and `.example` files, while whitelisting development/CI tokens (`CHANGE_ME`, `postgres`, `ci_password`).
     - Restores, builds (`-warnaserror -c Release`), and verifies code formatting (`dotnet format --verify-no-changes --no-restore`).
     - Executes unit and integration test suite (`dotnet test --no-build -c Release --filter "Category!=RequiresOllama"`).
     - Executes `dotnet list package --vulnerable --include-transitive` and fails the pipeline (`exit 1`) if any vulnerabilities are found.
   - **`extractor` Job**:
     - Sets up Python 3.11 with pip caching.
     - Installs `tools/extractor/requirements-dev.txt` and executes `pytest tools/extractor/tests -v`.
   - **`conformance` Job** (`needs: [dotnet, extractor]`):
     - Starts PostgreSQL 16 + pgvector container via `docker compose up -d --wait db` with active readiness polling.
     - Creates `conformance/results/` log directory before starting background services.
     - Pinned `dotnet-ef` global tool installation to exact version `10.0.13` (matching `Microsoft.EntityFrameworkCore.Design` in `Directory.Packages.props`).
     - Builds `src/DocSearch.Api -c Release` and applies database schema migrations via `dotnet ef database update --configuration Release --no-build`.
     - Spawns the Python PyMuPDF extractor sidecar on `127.0.0.1:8000` via `uvicorn` with output redirected to `conformance/results/extractor.log`. On timeout, tails the last 100 log lines.
     - **Pass A (Standard Contracts)**: Runs Kestrel API with Fake AI providers, logging to `conformance/results/api_pass_a.log` (tail on timeout), verifies readiness via `/health/ready`, and executes `pytest conformance -m "not degraded" -v --junitxml=conformance/results/pass_a.xml`.
     - **Pass B (Degraded Mode)**: Restarts Kestrel API with `Ai__Chat__Fake__Fail=true`, logging to `conformance/results/api_pass_b.log`, verifies readiness, and executes `pytest conformance -m degraded -v --junitxml=conformance/results/pass_b.xml`.
     - Merges test results into `conformance/results/report.xml` and runs `conformance/score.py` to produce a scorecard across all 8 functional areas.
     - Uploads JUnit XML reports and execution logs (`conformance/results/*.xml`, `conformance/results/*.log`) as workflow artifacts with `if: always()`.
2. **Cross-Platform Path Validation (`src/DocSearch.Infrastructure/Storage/LocalFileStorage.cs`)**:
   - In `ResolveAndValidatePath(string relativePath)`, added explicit rejection of `\` and `:` alongside `Path.IsPathRooted(relativePath)`.
   - On Linux (`ubuntu-latest`), backslashes are not path separators and drive letters like `C:\` are not treated as rooted by `Path.IsPathRooted`. Explicit rejection ensures directory traversal attacks like `..\..\evil.txt` and rooted paths like `C:\Windows\System32\cmd.exe` are uniformly rejected with `ArgumentException` across Windows and Linux.
   - Internal directory partitioning in `SaveAsync` was updated to construct forward-slash relative paths (`$"{year}/{month}/{fileName}"`).
3. **Integration Test Timing Hygiene (`AclAndSecurityIntegrationTests.cs`)**:
   - In T15, collection reindex was converted from a synchronous stub to an asynchronous background `ReindexWorker`. Calling `adminClient.GetAsync` once immediately after HTTP 202 created a timing race condition where the status could still be `"Running"`. A polling loop with deadline was added, maintaining a strict final assertion that status reaches `"Completed"`.
4. **README Updates**:
   - Added CI workflow status badge.
   - Updated status/progress section to reflect completion through T15 (Re-index & provider swap) and T16 (CI & integration hygiene).

---

## 2. Categorization of Non-Executed Tests in CI

Tests that do not run to completion in the CI pipeline fall into three distinct, well-defined categories:

### Group A: Benchmark Parity Tests in `DocSearch.Application.Tests` (`[SkippableFact]`)
These 5 tests verify parity against local Python benchmark experiments. They skip in CI because `eval/experiments/out/` is git-ignored and documents remain local:
1. `DocSearch.Application.Tests.HeuristicTokenCounterTests.Parity_With_TokenCalibrationCsv` (requires `eval/experiments/out/token_calibration.csv`)
2. `DocSearch.Application.Tests.LegalAwareChunkerTests.ArticleKeys_Aein91RealPdf_CapturesAll35Articles_And_PreservesHashes` (requires `eval/experiments/out/extract/pymupdf/aein-91.pdf`)
3. `DocSearch.Application.Tests.PageQualityScorerTests.T01_Parity_With_PyMuPDF_Extraction_Summary` (requires `eval/experiments/out/extract_summary.csv`)
4. `DocSearch.Application.Tests.TextDefectDetectorTests.Parity_With_BenchmarkPdfs_Separation` (requires `eval/experiments/out/extract_summary.csv`)
5. `DocSearch.Application.Tests.Chunking.ChunkEmbeddingTextBuilderTests.Reindex_Text_Matches_All_201_Benchmark_Chunks` (requires `eval/experiments/out/chunks_legal_t06b.json`)

### Group B: Sidecar Parity Tests in `HttpPdfExtractorTests` (`[SkippableFact]`)
These 3 tests in `DocSearch.Infrastructure.Tests` use synthetic test PDFs that **are** tracked in git in `tests/TestData/` (`persian-sample.pdf`, `english-sample.pdf`, `scanned-sample.pdf`):
1. `DocSearch.Infrastructure.Tests.Extraction.HttpPdfExtractorTests.ExtractAsync_PersianSample_MatchesInvariantsAndPhrase`
2. `DocSearch.Infrastructure.Tests.Extraction.HttpPdfExtractorTests.ExtractAsync_EnglishSample_MatchesInvariants`
3. `DocSearch.Infrastructure.Tests.Extraction.HttpPdfExtractorTests.ExtractAsync_ScannedSample_RoutesToOcr`
They skip in the `dotnet` CI job because the `dotnet` job runner environment does not have `pymupdf` (`fitz`) installed in its system Python (`ModuleNotFoundError`). Full PDF extraction is tested end-to-end in the dedicated `extractor` and `conformance` jobs.

### Group C: Host Ollama Integration Tests in `DocSearch.Infrastructure.Tests` (`[Fact]`)
These 2 tests in `OllamaEmbeddingIntegrationTests` are marked with `[Trait("Category", "RequiresOllama")]`:
1. `DocSearch.Infrastructure.Tests.Integration.OllamaEmbeddingIntegrationTests.Ollama_Returns_1024_Dims_For_Persian_Text`
2. `DocSearch.Infrastructure.Tests.Integration.OllamaEmbeddingIntegrationTests.Ollama_Truncate_False_Throws_On_Overlength_Input`
They are excluded from CI test execution via `--filter "Category!=RequiresOllama"` because CI runs with Fake AI providers and does not host an Ollama CPU/GPU inference daemon.

---

## 3. Local Verification Commands

```powershell
# 1. Format check
dotnet format --verify-no-changes

# 2. Build with warnings as errors
dotnet build -warnaserror -c Release

# 3. Local test run
dotnet test --no-build -c Release --filter "Category!=RequiresOllama"

# 4. Extractor sidecar tests
.\.venv\Scripts\python.exe -m pytest tools/extractor/tests -v

# 5. Conformance test dry-run collection
.\.venv\Scripts\python.exe -m pytest conformance --collect-only

# 6. Check vulnerable packages
dotnet list package --vulnerable --include-transitive

# 7. Secret scan
$matches = git grep -nI -E "AIza[0-9A-Za-z_-]{35}|sk-[A-Za-z0-9]{20,}|Password=[^$\s]+" -- . ":!*.example*" ":!docs/*" ":!conformance/README.md" ":!.github/workflows/ci.yml"
$leaks = $matches | Where-Object { $_ -notmatch "Password=(CHANGE_ME|postgres|ci_password)" }
```
