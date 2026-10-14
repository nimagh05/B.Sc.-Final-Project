# Progress Note: T01B — Conformance suite & evaluation harness skeleton

**Agent:** Antigravity  
**Task:** T01B — Conformance suite & evaluation harness skeleton (FROZEN after approval)  
**Date:** 2026-10-14  
**Status:** Completed  

## What Was Done

1. **Test Fixtures & Deterministic Generator (`conformance/data/`, `conformance/generate_data.py`)**:
   - Implemented `conformance/generate_data.py` generating 5 synthetic files:
     - `conformance/data/persian_sample.pdf`: 1-page valid Persian PDF rendered via PyMuPDF HTML engine (`<div dir='rtl'>`), verified to contain academic regulations with legal terms (`ماده ۱`), ZWNJ (`آیین‌نامه`, `نیم‌سال`), and Persian digits (`۲۰`, `۲۴`). Includes automated self-check with NFKC decoding.
     - `conformance/data/english_sample.pdf`: 1-page valid English PDF with course credit regulations.
     - `conformance/data/empty.pdf`: 0-byte file for empty upload verification.
     - `conformance/data/not_a_pdf.pdf`: Plain text file for magic-byte validation.
     - `conformance/data/corrupt.pdf`: Valid `%PDF-` header with corrupted body bytes for parser fault-tolerance testing.
   - Updated `.gitignore` to track `conformance/data/` while ignoring runtime `/data/`, `.pytest_cache/`, `__pycache__/`, and `conformance/results/`.

2. **Conformance Configuration & Harness (`conformance/`)**:
   - `conformance/requirements.txt`: Specified `pytest>=8.0.0`, `requests>=2.31.0`, `pymupdf>=1.24.0`.
   - `conformance/pytest.ini`: Configured test root and registered markers: `contract`, `degraded`, `quality`.
   - `conformance/conftest.py`:
     - Implemented `ApiClient` with auto-routing (`/health/*` at root, `/api/v1/*` for APIs) and exponential polling (`wait_until_ready`).
     - Added session-scoped `ensure_api_reachable` pre-flight check that skips tests gracefully with a clear message when `BASE_URL` is offline.
     - Added session-scoped `admin_client`, `user_client`, and `second_user_client` to avoid rate-limit exhaustion across test runs.
     - Implemented in-memory dynamic `oversized_bytes_stream` fixture (> `MAX_UPLOAD_MB`) for Test #9 without disk persistence.
   - `conformance/test_conformance.py`:
     - Implemented all 24 tests from `docs/EVAL.md` Conformance table against `docs/SPEC-DATA-AND-API.md`.
     - Verified `pytest conformance --collect-only` lists 24 items cleanly.
     - Verified `pytest conformance -m contract -v` cleanly skips 23 tests (and deselects 1 degraded test) when the API is offline.
   - `conformance/score.py`: Scoreboard script categorizing JUnit XML results into the 8 specification areas.
   - `conformance/README.md`: Complete guide for test execution, environment variables, and CI integration.

3. **Evaluation Harness CLI Skeleton (`eval/run_eval.py`)**:
   - Created CLI with argument parsing (`--base-url`, `--email`, `--password`, `--collection`, `--questions`, `--split`, `--out`, `--k`, `--timeout`, `--sleep`, `--resume`, `--limit`, `--judge`).
   - Integrated question loading from `eval/questions.json` and split filtering (`dev`, `test`, `all`).
   - Implemented ask request execution, per-question retry, latency measurement, and raw JSON output writing adhering to `docs/EVAL.md` §3 schema.

## What Was Not Done / Remaining

- The evaluation metrics calculation (Recall@K, MRR, correctness, bootstrap confidence intervals, and matplotlib charts) in `eval/run_eval.py` is reserved for **T19** per `docs/TASKS.md`.
- Conformance tests currently skip when the API is not running; they will be executed against real and fake implementations in T02+ and CI in T16.

## How to Continue

- Next task in `docs/TASKS.md` is **T02 — Solution skeleton, Docker Compose (db), health**.
- `conformance/**` is now **FROZEN** per project policy.
- Verification commands:
  ```powershell
  # Collect tests:
  & ".\.venv\Scripts\python.exe" -m pytest conformance --collect-only

  # Verify offline skip resilience:
  & ".\.venv\Scripts\python.exe" -m pytest conformance -m contract -v

  # Test scoreboard parser:
  & ".\.venv\Scripts\python.exe" conformance/score.py

  # Test evaluation harness CLI:
  & ".\.venv\Scripts\python.exe" eval/run_eval.py --help
  & ".\.venv\Scripts\python.exe" eval/run_eval.py --split test
  ```
