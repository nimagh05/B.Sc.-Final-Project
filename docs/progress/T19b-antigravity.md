# Task T19b Progress Note — Stage 2 End-to-End Evaluation & Results Curation

**Agent:** Antigravity (Google DeepMind)  
**Branch:** `feature/T19b-stage2`  
**Date:** 2026-11-24  
**Task:** T19b — Stage 2 End-to-End `/ask` Evaluation, Offline Threshold Simulation, Live Validation, Plots & Curated Thesis Results  

---

### 1. What changed

- **Harness & Plot Polish (Commit A — `506aa58`):**
  - **Pyflakes Lint Cleaning:** Completely eliminated all 27 `pyflakes` warnings across `eval/` (`eval/run_eval.py`, `eval/stage2.py`, `eval/audit_text_layer.py`, `eval/experiments/*.py`, `eval/tests/test_run_eval.py`). `.venv\Scripts\python -m pyflakes eval/` exits 0 with zero warnings.
  - **CI Workflow Integration (`.github/workflows/ci.yml`):** Added an `eval` job executing `pyflakes eval/` and `pytest eval/tests -v`, chaining directly into downstream `conformance`.
  - **Plot 1 Upgrades (`plot1_stage2_quality_comparison.png`):** Outside top legend, 95% Wilson score error bars only on true Bernoulli proportions with exact denominators ($N=112$ for Answer Rate and Keyword Recall; $N=99, 100, 97$ for Citation Validity across Arms 1, 2, 3). Removed misleading Wilson whisker from Citation Precision and labeled it as a mean ratio.
  - **Plot 2 Upgrades (`plot2_threshold_tradeoff_curves.png`):** Expanded from 2 panels to 3 side-by-side subplots (`figsize=(16, 5.2)`): `Tuned Dev (N_ans=18, N_nid=7)`, `Tuned Test (N_ans=42, N_nid=7)`, and `Held-out (N_ans=52, N_nid=12)`. Differentiated curves using distinct styles/markers/linewidths/markevery, added reference lines for `prod_055` ($\tau=0.55$), Arm 1 calibrated $\tau^*=0.70$, and Arms 2–3 calibrated $\tau^*=0.75$, and positioned the legend outside below panels.
  - **Plot 3 Upgrades (`plot3_ocr_generation_impact.png`):** Left panel includes 95% Wilson whiskers on all 6 bars with exact $k/N$ count labels positioned above whiskers. Right panel includes 3rd bar for `Digital Ref (Arm 2)` ($8/8 = 100.0\%$) with 95% Wilson error bars alongside `Scan Twin OCR-Off` ($0/8$) and `Scan Twin Selective OCR` ($6/8$).
  - **Automated Verification Harness (`eval/audit_docs_numbers.py`):** Added a standalone, CI-safe verification script that audits `docs/EVAL-RESULTS.md` against live JSON/CSV artifacts. Verifies zero `file:///` URLs, zero forbidden/fabricated phrases, zero credentials, dataset structural invariants, exact retrieval/generation metrics, McNemar p-values, Wilson CIs, and latency profiles.
- **Intermediate Offline Report Regeneration on Clean Tree:**
  - Ran `--stage2-report` on a completely clean git working tree matching Commit A (`506aa58`).
  - Merged official report manifest in `eval/results/t19b_stage2/results.json`:
    - `data_git_sha`: `e7f4924554bc733979a72ddc6c7ff998a47d64ca`
    - `report_git_sha`: `506aa58708dde16154aa358bf6ce2455a834bbed`
    - `git_dirty`: `false`
    - `run_status`: `official`
    - `failed_provider_count`: 0
  - Exported freshly rendered headless figures to `docs/figures/plot[1-3]*.png`.
- **Grounded Results Documentation (Commit B):**
  - **Complete Rewrite of `docs/EVAL-RESULTS.md`:** Grounded 100% of reported statistics strictly in verified artifacts from Stage 1 (`eval/results/t19a_stage1/results.json`) and Stage 2 (`eval/results/t19b_stage2/results.json`, `summary.csv`, `report.md`).
  - **Anti-Fabrication & Strict Hygiene:** Completely purged all ungrounded numbers, broken `file:///` URLs, and forbidden phrases. All image references use clean relative markdown paths (`figures/plot*.png`).
  - **Comprehensive Partition Breakdowns:** Included full tables for `heldout` ($N_{\text{ans}}=52, N_{\text{nid}}=12$), `tuned|dev` ($N_{\text{ans}}=18, N_{\text{nid}}=7$), `tuned|test` ($N_{\text{ans}}=42, N_{\text{nid}}=7$), and `overall` NID ($N=26$), reporting exact Wilson score intervals and refusal attributions (`LowSimilarity` vs. `ModelRefusal`).

---

### 2. How I verified

1. **Automated Documentation Number & Hygiene Audit:**
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
     [PASS] rel_recovered_16_16

   --------------------------------------------------------
     Audit Summary: 171 PASSED, 0 FAILED
   --------------------------------------------------------
   ```
   Exit code: `0`. 100% of assertions passed cleanly.

2. **Python Lint & Pyflakes Verification:**
   ```powershell
   .venv\Scripts\python -m pyflakes eval/
   ```
   *Output:* Exited with code `0` and **zero output**.

3. **Fast Evaluation Unit Test Suite:**
   ```powershell
   .venv\Scripts\pytest eval/tests/ -v
   ```
   *Output:* `============================= 32 passed in 46.99s =============================`

4. **Full .NET Test Suite:**
   ```powershell
   dotnet test --filter "Category!=RequiresOllama"
   ```
   *Output:* `Passed! - Failed: 0, Passed: 412, Skipped: 0, Total: 412`

5. **Git Tree Cleanliness:**
   Confirmed that working tree modifications are strictly limited to audited documentation, figures, and audit code; no untracked artifact churn under `eval/results/` or `eval/cache/`.

---

### 3. Tests added/changed

- `eval/audit_docs_numbers.py` (NEW standalone doc-number & anti-fabrication verification script)
- `eval/tests/test_run_eval.py::test_audit_docs_numbers_catches_file_url_and_forbidden_strings` (NEW)
- `eval/tests/test_run_eval.py::test_stage2_validate_offline_sim_zero_questions` (NEW)
- `eval/tests/test_run_eval.py::test_stage2_run_stage2_report_end_to_end` (NEW)

---

### 4. What the human must check manually

1. Review the git log on `feature/T19b-stage2`:
   ```powershell
   git log --oneline -n 4
   ```
2. Open `docs/EVAL-RESULTS.md` and verify layout, tables, and the three rendered figures in `docs/figures/`.
3. Run the automated audit script directly from PowerShell:
   ```powershell
   .venv\Scripts\python eval/audit_docs_numbers.py
   ```
   Confirm all 171 checks pass.

---

### 5. Known limitations / deviations / ADRs

- **No Spec or Rule Deviations:** All frozen files (`eval/questions.json`, `eval/text_layer_audit.csv`, `conformance/**`, `AGENTS.md`, ADRs `0001`–`0010`) were left completely untouched. C# production code (`src/**`) and test code (`tests/**`) were untouched.
- **Exploratory Scope of Document-Cluster Bounds:** With $G=3$ natural clusters in the heldout collection, asymptotic normal convergence of cluster-robust standard errors does not hold. Question-level Wilson intervals and paired non-parametric tests (Wilcoxon and McNemar) serve as the primary inferential standard.
- **ADR 0011 Reference:** Formalized in `docs/adr/0011-stage2-evaluation-protocol.md`.

---

### 6. Recommended next task

- Review and merge branch `feature/T19b-stage2` into `main`.
- Proceed to **Task T20 (Final Polish, Documentation & Project Wrap-up)**.
