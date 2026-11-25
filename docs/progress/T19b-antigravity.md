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
  - **Automated Verification Harness (`eval/audit_docs_numbers.py`):** Added a standalone, CI-safe verification script that audits `docs/EVAL-RESULTS.md` against live JSON/CSV artifacts.
- **Intermediate Offline Report Regeneration on Clean Tree:**
  - Ran `--stage2-report` on a completely clean git working tree matching Commit A (`506aa58`).
  - Merged official report manifest in `eval/results/t19b_stage2/results.json`:
    - `data_git_sha`: `e7f4924554bc733979a72ddc6c7ff998a47d64ca`
    - `report_git_sha`: `506aa58708dde16154aa358bf6ce2455a834bbed`
    - `git_dirty`: `false`
    - `run_status`: `official`
    - `failed_provider_count`: 0
  - Exported freshly rendered headless figures to `docs/figures/plot[1-3]*.png`.
- **Grounded Results Documentation (Commit B — `6ce8d9f`):**
  - **Comprehensive Grounded Rewrite of `docs/EVAL-RESULTS.md`:** Grounded reported statistics strictly in verified artifacts from Stage 1 (`eval/results/t19a_stage1/results.json`) and Stage 2 (`eval/results/t19b_stage2/results.json`, `summary.csv`, `report.md`).
  - **Anti-Fabrication & Strict Hygiene:** Completely purged ungrounded numbers, broken `file:///` URLs, and forbidden phrases. All image references use clean relative markdown paths (`figures/plot*.png`).
- **Surgical Review Polish (Current Commit):**
  - **Table 9.3 Overhaul:** Replaced fabricated values with exact verified per-category counts from `eval/results/t19b_stage2/results.json` at `prod_055` (column sums matching exactly $99, 100, 97$ for Answer Rate and $83, 82, 80$ for Keyword Hits across Arms 1, 2, 3).
  - **Correction of Stage 1 Test References:** Removed all references to non-existent "Paired Wilcoxon" tests in Stage 1, correctly labeling them as `Paired Bootstrap 95% CI` (1,000 resamples), while keeping exact McNemar for Stage 2 binary paired comparisons.
  - **Accurate `q043` Context:** Corrected description of `q043` (asks for a base lecturer's `مربی پایه` hourly teaching rate in Toman, matched against doctoral TA practical-course compensation clause in `dastyar-amoozeshi.pdf`).
  - **Overall NID Refusal Attribution:** Completed Section 5.1 Overall NID table with exact refusal attribution (`LowSimilarity` vs. `ModelRefusal`) across `ungated`, `prod_055`, and `calibrated_tau`, documenting invariant FAR.
  - **Persian Spelled-Out-Number Blind Spot:** Added explicit discussion in Section 8.2 regarding spelled-out numerals bypassing regex denominators, and labeled Table 8.5 `(ungated operating point)`.
  - **Audit Script Hardening (`eval/audit_docs_numbers.py`):** Added programmatic cell-level verification of all 6 rows × 3 arms of Table 9.3 computed directly from `results.json`, and added `"Paired Wilcoxon"` to `FORBIDDEN_STRINGS` (total 208 checks passing).

---

### 2. How I verified

1. **Automated Documentation Number & Anti-Fabrication Audit:**
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
     [PASS] tbl93_numeric_a1_ans_22_22
     ...
     [PASS] tbl93_english_a3_kw_5_12
     ...
     [PASS] rel_recovered_16_16

   --------------------------------------------------------
     Audit Summary: 208 PASSED, 0 FAILED
   --------------------------------------------------------
   ```
   Exit code: `0`. 100% of assertions passed cleanly.

2. **Pyflakes Verification:**
   ```powershell
   .venv\Scripts\python -m pyflakes eval/
   ```
   *Output:* Exited with code `0` and **zero output**.

3. **Fast Evaluation Unit Test Suite:**
   ```powershell
   .venv\Scripts\pytest eval/tests/ -v
   ```
   *Output:* `============================= 32 passed in 39.13s =============================`

4. **Full .NET Test Suite:**
   ```powershell
   dotnet test --filter "Category!=RequiresOllama"
   ```
   *Output:* `Passed! - Failed: 0, Passed: 412, Skipped: 0, Total: 412`

5. **Git Tree Cleanliness:**
   Confirmed that working tree modifications are strictly limited to `docs/EVAL-RESULTS.md`, `eval/audit_docs_numbers.py`, and `docs/progress/T19b-antigravity.md`.

---

### 3. Tests added/changed

- **Across T19b Branch:**
  - `eval/audit_docs_numbers.py` (NEW standalone doc-number & anti-fabrication verification script; hardened with 36 cell-level Table 9.3 checks and Wilcoxon guard)
  - `eval/tests/test_run_eval.py::test_audit_docs_numbers_catches_file_url_and_forbidden_strings` (NEW)
  - `eval/tests/test_run_eval.py::test_stage2_validate_offline_sim_zero_questions` (NEW)
  - `eval/tests/test_run_eval.py::test_stage2_run_stage2_report_end_to_end` (NEW)

---

### 4. What the human must check manually

1. Review the git log on `feature/T19b-stage2`:
   ```powershell
   git log --oneline -n 5
   ```
2. Open `docs/EVAL-RESULTS.md` and review the revised Table 9.3, Section 5.1 NID attribution table, and conclusions.
3. Run the automated audit script directly from PowerShell:
   ```powershell
   .venv\Scripts\python eval/audit_docs_numbers.py
   ```
   Confirm all 208 checks pass.

---

### 5. Known limitations / deviations / ADRs

- **No Spec or Rule Deviations:** All frozen files (`eval/questions.json`, `eval/text_layer_audit.csv`, `conformance/**`, `AGENTS.md`, ADRs `0001`–`0010`) were left completely untouched. C# production code (`src/**`) and test code (`tests/**`) were untouched.
- **Exploratory Scope of Document-Cluster Bounds:** With $G=3$ natural clusters in the heldout collection, asymptotic normal convergence of cluster-robust standard errors does not hold. Question-level Wilson intervals and paired non-parametric tests (paired bootstrap CIs and exact McNemar) serve as the primary inferential standard.
- **ADR 0011 Reference:** Formalized in `docs/adr/0011-stage2-evaluation-protocol.md`.

---

### 6. Recommended next task

- Review and merge branch `feature/T19b-stage2` into `main`.
- Proceed to **Task T17 (Web UI: React + Vite + TypeScript, RTL)** followed by **Task T20 (Final Packaging & Documentation)**.

---

## Post-Merge Audit Polish (`fix/T19b-audit-polish`, 2026-11-24 -- 2026-11-25)

1. **Provenance, ADR Alignment & POSIX Archive (`docs/EVAL-RESULTS.md`):**
   - Tightened Section 2 registry labels (`Pre-Registered (post-Stage 1, pre-Stage 2)` for Secondary Endpoint at ADR 0010 §7.1 & ADR 0011 §5.2; `Planned measurement` for Pure OCR Latency at ADR 0009 §5; `Pre-specified metric definitions` for Citation Audit at ADR 0011 §7; ADR 0010 §3 & §5 for Primary Endpoints 1 & 2; ADR 0010 §2.2 & ADR 0011 §2.2 for Regime B).
   - Corrected Stage 1 working-tree/status provenance (`pre-schema / unrecorded; report rendered with dirty: True`) and updated Section 1.1 & `eval/README.md` for the POSIX-path reproducibility archive `t19-eval-data.zip` (18 files, 975,651 bytes, SHA-256 `34b64841e785d72842f3860ea64d81eea54f26a4d1d4cc097f048f1d680b2310`; Stage 1 `t19a_stage1/` generated at `b3c9c12`, Stage 2 tag `t19b-stage2-data` from `e7f4924` plus report rendered at `506aa58`).
   - Added $B=10,000$ bootstrap specification (§1.2), hybrid equal-weight MRR vs. top-6 generation clarification (§6.1 & Conclusion 3), unclassified degraded log disclosure (§9.7), and Future Work bullets (§11.1) including ADR 0012 RRF weights, HTTP resilience, and targeted digit/ligature/page-level OCR.

2. **Tracked 54-Judgment CSV, Per-Doc Table & Thesis Caveats (`eval/manual_audit_heldout_cmap.csv`, `docs/EVAL-RESULTS.md`):**
   - Persisted 54 author-reviewed `heldout-CMap` correctness judgments (`id,arm,label,note`, LF UTF-8) with `q090,arm3` updated to `"University Presidential Board"` (`هیأت رئیسه`).
   - Added Section 8.6 rater provenance (`single-rater, unblinded, author-reviewed judgments (LLM-assisted)`), borderline rulings (`q082`, `q086`, `q081`, `q098`), exact 7-error CMap outcome (`4 fully corrected, 2 partial/refused, 1 numeric error remaining`), the $G=2$ Per-Document Breakdown table (`forsat-motaleati.pdf` vs. `tarh-dakheli.pdf`), explicit **Hypotheses (not verified by ablation)** labeling, Document-Wide OCR Propagation Hypothesis (ADR 0009 §2), binary `wrong` vs. non-`wrong` McNemar invariance note ($b=6, c=3, p=0.5078$ / `0.508`), and Section 11.2 Methodological Limitations unblinded rater bias caveat.

3. **Data-Computed Doc Auditor & Exact Wilson CIs (`eval/audit_docs_numbers.py`, `eval/README.md`, `eval/tests/test_run_eval.py`):**
   - Upgraded `DocAuditor` with section-scoped `assert_row`, CI-safe `audit_manual_cmap_from_csv()`, and programmatic computation from `results.json` + CSV across Tables 5.1, 8.1, 8.5, 8.6, 9.3, 9.4 (plus Stage 1, ROC-AUC, McNemar, counterfactual, null experiment, FRR decomposition, Regime B, and latency tables).
   - Implemented independent full-precision `wilson_interval_exact` (`statistics.NormalDist().inv_cdf(0.975)`) to prevent double-rounding from frozen `stage2.wilson_score_interval`'s intentional 4-decimal rounding (`6/18 -> 56.3%`, `14/42 -> 48.4%`, `0/12 -> 24.2%`).
   - Added CI-safe unit tests `test_audit_docs_numbers_assert_row_catches_swapped_table_rows`, `test_audit_manual_cmap_csv_reproduces_table_8_6`, and `test_audit_wilson_interval_exact_boundary_cases` in `eval/tests/test_run_eval.py`.
   - Verification targets after final edits: `eval/audit_docs_numbers.py` (`183` checks), `pyflakes eval/` (`0` warnings), and `pytest eval/tests/ -v` (`35` tests).
