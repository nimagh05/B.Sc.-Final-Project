# Task T19b Progress Note — Stage 2 End-to-End Evaluation & Results Curation

**Agent:** Antigravity (Google DeepMind)  
**Branch:** `fix/T19b-audit-polish` (branched from latest `main`)  
**Date:** 2026-11-24  
**Task:** T19b — Surgical Documentation & Row-Scoped Audit-Script Polish  

---

### 1. What changed

- **Grounded Results Documentation Polish (`docs/EVAL-RESULTS.md`):**
  - **Stage 1 Provenance Honesty (Section 1.1):** Accurately recorded Stage 1 provenance as `pre-schema / unrecorded; report rendered with dirty: True`, removing unverified claims of "official" or clean tree on Stage 1 data generation.
  - **Reproducibility Archive (Section 1.1):** Added official GitHub Release archive tracking: Git tag `t19b-stage2-data` (`e7f4924554bc733979a72ddc6c7ff998a47d64ca`), GitHub Release asset `t19-eval-data.zip` (18 files, 975,651 bytes), SHA-256: `e8d7e4fc92c6e7e2f3965be9ad1f0e3e325ab030f9d31bd2db667df6209f2b95`.
  - **Generation Temperature Honesty (Section 1.1):** Removed `"greedy decoding"`, documenting `0.0 (minimum temperature; non-deterministic across sessions as shown by q015, vs. production 0.1)`.
  - **Bootstrap Resample Precision (Section 1.2):** Clarified $B = 10,000$ bootstrap resamples at seed 42 per ADR 0010 §5.
  - **ADR References Alignment (Section 2):** Aligned references to ADR 0010 §3 & §5 for Primary Endpoints 1 & 2; ADR 0010 §2.2 & ADR 0011 §2.2 for Regime B; ADR 0009 §5 for Pure OCR Latency; ADR 0011 §7 for Citations; added Post-Hoc entry for Section 8.6.
  - **Hybrid Disconnect Honesty (Section 6.1 & Conclusion 3):** Replaced `"introducing no observed degradation"` with explicit explanation: `Hybrid search with equal RRF weights (1.0/1.0) showed a statistically significant Stage 1 ranking drop (overall ΔMRR = -0.0663 [-0.1214, -0.0124]; heldout-CMap ΔMRR = -0.1068 [-0.2148, -0.0093]), yet produced no statistically distinguishable end-to-end degradation (p = 1.000) because the LLM reads all top-6 chunks. Keyword and article-key support is retained as the architectural design rationale per ADR 0005.`
  - **54-Judgment Manual Correctness Audit on `heldout-CMap` (Section 8.6):** Added manual audit across all 27 answerable questions on `heldout-CMap` ($N = 27 \times 2 = 54$ judgments) under `ungated` execution. Includes 4-label rubric (Correct, Partial, Wrong, Refused), exact table with paired discordant McNemar ($p = 0.508$, descriptive), and qualitative error attribution showing CMap digit errors dropping from 7/27 in Arm 2 to 1/27 in Arm 3 (`q080`).
  - **Reliability Log Detail (Section 9.7):** Explicitly stated that 1 instance was a 60-second client timeout on `q037`, while the remaining 15 first-attempt degraded responses are unclassified in saved logs.
  - **Conclusions & Future Work (Section 11 & 11.1):** Labeled Conclusion 1 as descriptive for Selective OCR, clarified Conclusion 2 operational rationale for retaining $\tau=0.55$, and added Section 11.1 Future Work detailing configurable RRF weights (ADR 0012) and client-side HTTP resilience in `RagService.cs`.

- **Row-Scoped Audit Script Hardening (`eval/audit_docs_numbers.py`):**
  - **Row-Scoped Verification Method:** Implemented `assert_row(self, name: str, row_key: str, *expected_cells: str)` ensuring that all expected statistical numbers, confidence intervals, and ratios appear on the exact matching table row.
  - **Eliminated Vacuous Checks:** Removed bare substring checks (`"0"`, `"7"`, `"12"`, `"18"`, `"42"`, `"22"`, `"99"`, `"100"`, `"97"`, bare `"q079"`). Replaced them with comprehensive row assertions across Table 1.1, Table 3.1, Section 5.1 partition tables, Table 5.5, Table 8.1, Table 8.5, Table 8.6, Table 9.3, Table 9.4, and Table 9.6.
  - **Anti-Fabrication & Strict Hygiene:** Added `"greedy decoding"` and `"introducing no observed degradation"` to `FORBIDDEN_STRINGS`.
  - **Clean Execution:** 156 checks passing with 0 failures, zero pyflakes warnings, and zero network/Docker/Ollama dependencies.

- **CI-Safe Test Suite Integration (`eval/tests/test_run_eval.py`):**
  - Added `test_audit_docs_numbers_assert_row_catches_swapped_table_rows(tmp_path)` proving that `assert_row` detects swapped table values and flags failures.
  - All 33 unit tests pass cleanly in pytest.

- **Documentation Updates (`eval/README.md`):**
  - Documented local execution of `audit_docs_numbers.py` against `eval/results/` vs CI execution with synthetic fixtures.
  - Provided copy-paste instructions for downloading and unpacking the official reproducibility archive `t19-eval-data.zip`.

---

### 2. How I verified

1. **Automated Row-Scoped Documentation Audit:**
   ```powershell
   .venv\Scripts\python eval/audit_docs_numbers.py
   ```
   *Output:*
   ```
   ========================================================
     Auditing EVAL-RESULTS.md against ground-truth data
   ========================================================
     [PASS] hygiene_zero_file_urls
     ...
     [PASS] s1_tbl31_overall
     ...
     [PASS] pe21_heldout_a1_prod055
     ...
     [PASS] sec_tbl86_correct
     ...
     [PASS] tbl93_numeric_row
     ...
     [PASS] rel_unclassified_15

   --------------------------------------------------------
     Audit Summary: 156 PASSED, 0 FAILED
   --------------------------------------------------------
   ```
   Exit code: `0`. 100% of assertions passed cleanly.

2. **Pyflakes Verification:**
   ```powershell
   .venv\Scripts\python -m pyflakes eval/
   ```
   *Output:* Exited with code `0` and **zero warnings**.

3. **Fast Evaluation Unit Test Suite:**
   ```powershell
   .venv\Scripts\pytest eval/tests/ -v
   ```
   *Output:* `======================== 33 passed in 64.74s (0:01:04) ========================`

4. **Full .NET Test Suite:**
   ```powershell
   dotnet test --filter "Category!=RequiresOllama"
   ```
   *Output:* `Passed! - Failed: 0, Passed: 412, Skipped: 0, Total: 412`

5. **Git Status & Working Tree Hygiene:**
   Verified that ONLY the 5 allowed files are modified, with zero modifications to C# code, figures, CI workflow, `.env`, or frozen files.

---

### 3. Tests added/changed

- `eval/tests/test_run_eval.py::test_audit_docs_numbers_assert_row_catches_swapped_table_rows` (NEW: verifies row-scoped table mismatch detection)
- `eval/audit_docs_numbers.py` (HARDENED: row-scoped `assert_row` across all key markdown tables, forbidden string guards for `"greedy decoding"` and `"introducing no observed degradation"`, lazy loading for CI safety)

---

### 4. What the human must check manually

1. Review the git diff on `fix/T19b-audit-polish`:
   ```powershell
   git diff --stat
   ```
   Confirm that only the 5 specified files were modified (`docs/EVAL-RESULTS.md`, `eval/audit_docs_numbers.py`, `eval/tests/test_run_eval.py`, `eval/README.md`, `docs/progress/T19b-antigravity.md`).
2. Run the automated audit script directly from PowerShell:
   ```powershell
   .venv\Scripts\python eval/audit_docs_numbers.py
   ```
   Confirm all 156 checks pass with 0 failures.
3. Review Section 8.6 in `docs/EVAL-RESULTS.md` to review the 54-judgment manual audit on `heldout-CMap`.

---

### 5. Known limitations / deviations / ADRs

- **No Spec or Rule Deviations:** All frozen files (`eval/questions.json`, `eval/text_layer_audit.csv`, `conformance/**`, `AGENTS.md`, ADRs `0001`–`0011`) were left completely untouched. C# production code (`src/**`) and test code (`tests/**`) were untouched.
- **Exploratory Scope of Document-Cluster Bounds:** With $G=3$ natural clusters in the heldout collection, asymptotic normal convergence of cluster-robust standard errors does not hold. Question-level Wilson intervals and paired non-parametric tests (paired bootstrap CIs and exact McNemar) serve as the primary inferential standard.
- **ADR 0011 Reference:** Formalized in `docs/adr/0011-stage2-evaluation-protocol.md`.

---

### 6. Recommended next task

- Review and merge branch `fix/T19b-audit-polish` into `main`.
- Proceed to **Task T17 (Web UI: React + Vite + TypeScript, RTL)** followed by **Task T20 (Final Packaging & Documentation)**.
