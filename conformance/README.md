# Conformance Suite (Black-box API Verification)

The `conformance/` suite verifies that any implementation of DocSearch adheres to the normative contracts specified in `docs/SPEC-DATA-AND-API.md`, `docs/SPEC-PERSIAN-AND-RAG.md`, and `docs/EVAL.md` §4.

> [!IMPORTANT]
> **Freeze Policy:** Once approved in T01B, the contents of `conformance/**` are **FROZEN**. No test or fixture may be modified, relaxed, or skipped without explicit approval from the project lead.

---

## 1. Prerequisites & Setup

Install the required test dependencies in your Python 3.11 environment:

```powershell
.\.venv\Scripts\python.exe -m pip install -r conformance/requirements.txt
```

Dependencies:
- `pytest >= 8.0.0`
- `requests >= 2.31.0`
- `pymupdf >= 1.24.0`

---

## 2. Configuration & Environment Variables

The test suite runs black-box against a running API instance:

| Variable | Default | Description |
|---|---|---|
| `BASE_URL` | `http://localhost:5000` | Base URL of the running DocSearch API |
| `ADMIN_EMAIL` | `admin@ci.test` | Initial BootstrapAdmin email |
| `ADMIN_PASSWORD` | `Ci_Admin_123!` | Initial BootstrapAdmin password |
| `MAX_UPLOAD_MB` | `50` | Expected maximum document upload size in MB |

Before running conformance, ensure the API is started with Fake AI providers:
- `Ai__Embedding__Provider=Fake`
- `Ai__Chat__Provider=Fake`
- `Rag__MinSimilarity=0.2`
- `Auth__AllowSelfRegistration=true`
- `Auth__BootstrapAdmin__Email=admin@ci.test`
- `Auth__BootstrapAdmin__Password=Ci_Admin_123!`

---

## 3. Running the Conformance Suite

### 3.1 Collect Tests (Dry Run)
Verify that all 24 conformance tests are discovered cleanly:
```powershell
.\.venv\Scripts\python.exe -m pytest conformance --collect-only
```

### 3.2 Run Standard Contract Tests
Executes the 23 standard contract and security tests:
```powershell
.\.venv\Scripts\python.exe -m pytest conformance -m contract -v
```

### 3.3 Run Degraded Mode Test
When the API is started with `Ai__Chat__Fake__Fail=true`:
```powershell
.\.venv\Scripts\python.exe -m pytest conformance -m degraded -v
```

### 3.4 Generate Scoreboard & JUnit Report
```powershell
.\.venv\Scripts\python.exe -m pytest conformance -m contract -v --junitxml=conformance/results/report.xml
.\.venv\Scripts\python.exe conformance/score.py conformance/results/report.xml
```

---

## 4. Test Coverage (24 Tests)

1. **`test_01_health_live_and_ready`**: `GET /health/live`, `/health/ready` (200, checks array, zero secrets).
2. **`test_02_register_and_login`**: User registration, login token generation, wrong password rejection (`401 auth.invalid_credentials`).
3. **`test_03_protected_endpoint_without_token`**: Calling protected routes without bearer token returns `401 auth.unauthorized`.
4. **`test_04_collection_crud`**: Collection create, read, update, duplicate name rejection (`409 collection.name_taken`), delete (`204`).
5. **`test_05_upload_valid_pdf`**: Multipart PDF upload returns `202 Accepted` with `Location` header.
6. **`test_06_document_becomes_ready`**: Polling uploaded PDF reaches status `Ready` with `pageCount >= 1` and `chunkCount >= 1`.
7. **`test_07_upload_text_file_named_pdf`**: Uploading non-PDF file rejected with `400 document.invalid_file`.
8. **`test_08_upload_empty_or_corrupt_file`**: Empty/corrupt PDFs rejected (`400`) or marked `Failed` with `errorMessage`.
9. **`test_09_upload_too_large`**: Uploads exceeding `Storage:MaxUploadMb` rejected with `413 document.too_large`.
10. **`test_10_upload_duplicate_file`**: Duplicate file hash in same collection rejected with `409 document.duplicate`.
11. **`test_11_ask_matching_question`**: Question matching PDF words returns answer, citations, and source metadata.
12. **`test_12_ask_nonsense_question`**: Unrelated questions return `200` with `notFound=true` and fixed refusal sentence.
13. **`test_13_acl_cross_user_isolation`**: User B accessing User A's collections/documents returns `404 resource.not_found`.
14. **`test_14_acl_role_permissions`**: Role enforcement (`Viewer` cannot upload, `Editor` can upload, `Owner` manages members).
15. **`test_15_ask_mixed_collection_scope`**: Unreadable collection IDs are filtered out without leaking sources.
16. **`test_16_delete_document_cascades`**: Deleting document deletes stored files and vector search chunks.
17. **`test_17_degraded_mode`**: Chat provider failure returns `200` with `degraded=true`, `answer=null`, and sources.
18. **`test_18_validation_errors`**: Empty questions, `topK=0`, or >20 collection IDs return `400 validation.failed`.
19. **`test_19_rate_limiting`**: Burst requests on `/auth/login` trigger `429 rate.limited` with `Retry-After`.
20. **`test_20_persian_normalization_roundtrip`**: Queries with ASCII digits, Arabic Kaf/Yeh, and missing ZWNJ retrieve target chunks.
21. **`test_21_admin_endpoints`**: Non-admin rejected with `403 auth.forbidden`; admin reindex runs to `Completed`.
22. **`test_22_error_hygiene`**: Error responses return RFC 7807 problem+json and leak no stack traces or filesystem paths.
23. **`test_23_idempotent_reprocess`**: Reprocessing document leaves chunk count stable without orphans.
24. **`test_24_multi_turn_conversation`**: Multi-turn questions persist chronological message history.

---

## 5. Synthetic Data Files (`conformance/data/`)

Synthetic test fixtures are generated deterministically by `conformance/generate_data.py`:
- `persian_sample.pdf`: 1-page valid Persian PDF containing academic regulations with legal terms (`ماده ۱`), ZWNJ, and digits.
- `english_sample.pdf`: 1-page valid English PDF containing academic regulations.
- `empty.pdf`: 0-byte file for empty upload testing.
- `not_a_pdf.pdf`: Plain text file for invalid magic byte validation.
- `corrupt.pdf`: Corrupt PDF file with valid header and invalid body for parser robustness testing.
