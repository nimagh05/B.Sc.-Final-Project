#!/usr/bin/env python3
"""audit_docs_numbers.py — Reproducible verification script for docs/EVAL-RESULTS.md.

Programmatically computes ground-truth numbers from JSON/CSV evaluation artifacts and
asserts exact presence, row-scoped table consistency, and anti-fabrication / strict-hygiene
rules in docs/EVAL-RESULTS.md.

Usage:
    python eval/audit_docs_numbers.py [--doc docs/EVAL-RESULTS.md]

Exit code:
    0: 100% checks passed cleanly.
    1: One or more checks failed.
"""

import argparse
import json
import re
import sys
from pathlib import Path
from typing import List, Tuple

REPO_ROOT = Path(__file__).resolve().parent.parent

FORBIDDEN_STRINGS = [
    "farsiparsed",
    "Gemini 2.5",
    "+200%",
    "three-quarters",
    "universal gate",
    "0/78",
    "40.0% (4/10)",
    "4 / 10",
    "socket resets",
    "HTTP 503",
    "streaming generation",
    "< 35 ms",
    "146 questions",
    "N=146",
    "RRF Parameter Stability Sweep",
    "48 months",
    "U+FFFD",
    "IEEE",
    "Procedural / Legal",
    "close to chance",
    "near-domain",
    "eliminating dangerous",
    "Paired Wilcoxon",
    "greedy decoding",
    "introducing no observed degradation",
]


class DocAuditor:
    def __init__(self, doc_path: Path):
        self.doc_path = doc_path
        self.doc_text = doc_path.read_text(encoding="utf-8")
        self.passes: List[str] = []
        self.failures: List[Tuple[str, str]] = []

        # Lazy-loaded by run_all_audits() so CI unit tests run without git-ignored eval/results/
        self.s1_results = None
        self.s2_results = None
        self.s2_summary_csv = ""
        self.questions = []

    def _load_artifacts(self):
        self.s1_results = json.loads((REPO_ROOT / "eval/results/t19a_stage1/results.json").read_text(encoding="utf-8"))
        self.s2_results = json.loads((REPO_ROOT / "eval/results/t19b_stage2/results.json").read_text(encoding="utf-8"))
        self.s2_summary_csv = (REPO_ROOT / "eval/results/t19b_stage2/summary.csv").read_text(encoding="utf-8")
        self.questions = json.loads((REPO_ROOT / "eval/questions.json").read_text(encoding="utf-8"))

    def check(self, name: str, condition: bool, err_msg: str):
        if condition:
            self.passes.append(name)
            print(f"  [PASS] {name}")
        else:
            self.failures.append((name, err_msg))
            print(f"  [FAIL] {name}: {err_msg}")

    def assert_contains(self, name: str, expected_snippet: str):
        self.check(
            name,
            expected_snippet in self.doc_text,
            f"Snippet not found in document: {expected_snippet!r}",
        )

    def assert_regex(self, name: str, pattern: str):
        self.check(
            name,
            bool(re.search(pattern, self.doc_text)),
            f"Pattern not found in document: {pattern!r}",
        )

    def assert_row(self, name: str, row_key: str, *expected_cells: str):
        """Locates markdown table line(s) matching row_key and asserts all expected_cells appear on that row."""
        matching_rows = [
            line for line in self.doc_text.splitlines()
            if row_key in line and line.strip().startswith("|")
        ]
        if not matching_rows:
            self.check(name, False, f"Table row with key {row_key!r} not found in document")
            return

        for row in matching_rows:
            if all(cell in row for cell in expected_cells):
                self.check(name, True, "")
                return

        best_row = max(matching_rows, key=lambda r: sum(1 for c in expected_cells if c in r))
        missing = [c for c in expected_cells if c not in best_row]
        self.check(name, False, f"Row with key {row_key!r} missing cells {missing!r}. Found row: {best_row.strip()!r}")

    def run_all_audits(self) -> bool:
        self._load_artifacts()
        print("\n========================================================")
        print(f"  Auditing {self.doc_path.name} against ground-truth data")
        print("========================================================")

        self._audit_hygiene_and_forbidden_strings()
        self._audit_dataset_structure()
        self._audit_stage1_metrics()
        self._audit_stage2_provenance_and_validation()
        self._audit_primary_endpoints()
        self._audit_vector_roc_auc()
        self._audit_hybrid_vs_vector_and_counterfactual()
        self._audit_null_experiment()
        self._audit_secondary_endpoint_heldout_cmap()
        self._audit_frr_decomposition()
        self._audit_question_types_and_citations()
        self._audit_regime_b_case_study()
        self._audit_latencies()
        self._audit_production_reliability()

        print("\n--------------------------------------------------------")
        print(f"  Audit Summary: {len(self.passes)} PASSED, {len(self.failures)} FAILED")
        print("--------------------------------------------------------")
        return len(self.failures) == 0

    def _audit_hygiene_and_forbidden_strings(self):
        # 1. Zero file:/// links
        self.check(
            "hygiene_zero_file_urls",
            "file:///" not in self.doc_text,
            "Found broken 'file:///' link in document (must use relative paths)!",
        )

        # 2. Relative figures check
        for fig_name in [
            "figures/plot1_stage2_quality_comparison.png",
            "figures/plot2_threshold_tradeoff_curves.png",
            "figures/plot3_ocr_generation_impact.png",
        ]:
            self.assert_contains(f"relative_figure_{fig_name}", fig_name)

        # 3. Forbidden strings check
        for s in FORBIDDEN_STRINGS:
            pattern = re.compile(re.escape(s), re.IGNORECASE)
            self.check(
                f"forbidden_string_{s.replace(' ', '_')}",
                not pattern.search(self.doc_text),
                f"Found forbidden/fabricated string: {s!r}",
            )

        # 4. Zero API keys or secrets
        api_key_match = re.search(r"AIza[0-9A-Za-z_-]{35}", self.doc_text)
        self.check(
            "hygiene_zero_api_keys",
            not api_key_match,
            f"Found potential API key in document: {api_key_match.group(0) if api_key_match else ''}",
        )

    def _audit_dataset_structure(self):
        unique_q_count = len(self.questions)
        self.check("dataset_unique_questions_138", unique_q_count == 138, f"Expected 138 questions, found {unique_q_count}")
        self.assert_contains("doc_mentions_138_questions", "138 unique questions across 14 Regime A PDFs")
        self.assert_contains("doc_mentions_74_tuned", "74 questions across 11 documents")
        self.assert_contains("doc_mentions_64_heldout", "64 questions across **3 PDFs**")
        self.assert_contains("doc_mentions_heldout_3_pdfs", "forsat-motaleati.pdf`, `shiveh-karamoozi.pdf`, and `tarh-dakheli.pdf")
        self.assert_contains("doc_mentions_anti_leakage", "ANTI-LEAKAGE VIOLATION")
        self.assert_contains("doc_mentions_bootstrap_resamples", "B = 10,000")

    def _audit_stage1_metrics(self):
        s1_manifest = self.s1_results["manifest"]
        # Section 1.1 provenance
        self.assert_row("s1_data_git_sha_row", "Data Generation Git SHA", s1_manifest["git_sha"])
        self.assert_row("s1_working_tree_row", "Git Working Tree Status", "pre-schema / unrecorded", "dirty: True")
        self.assert_row("s1_run_status_row", "Run Status", "pre-schema / unrecorded")

        # Table 3.1 Primary Endpoints 1 & 2 Outcomes (Row-Scoped)
        self.assert_row("s1_tbl31_overall", "**Overall** ($N=112$)", "0.8500", "0.7837", "0.7822", "-0.0663", "-0.0014")
        self.assert_row("s1_tbl31_pe1_heldout", "Primary Endpoint 1 (`heldout_all`, $N=52$)", "0.8638", "0.7919", "0.7903", "-0.0719", "-0.0016")
        self.assert_row("s1_tbl31_pe2_cmap", "Primary Endpoint 2 (`heldout-CMap`, $N=27$)", "0.8951", "0.7883", "0.7852", "-0.1068", "-0.0031")
        self.assert_row("s1_tbl31_heldout_test", "`heldout_test` ($N=39$)", "0.8739", "0.7986", "0.7952", "-0.0754", "-0.0034")
        self.assert_row("s1_tbl31_tuned_test", "`tuned_test` ($N=42$)", "0.8340", "0.7409", "0.7504", "-0.0931", "+0.0095")
        self.assert_row("s1_tbl31_tuned_dev", "`tuned_dev` ($N=18$)", "0.8472", "0.8598", "0.8333", "+0.0126", "-0.0265")

        # CMap heterogeneity
        self.assert_contains("s1_forsat_delta_pos_01153", "+0.1153")
        self.assert_contains("s1_tarh_delta_neg_00978", "-0.0978")

        # Pure OCR timing
        self.assert_contains("s1_pure_ocr_overall_4131", "4.131")
        self.assert_contains("s1_pure_ocr_regime_a_3608", "3.608")
        self.assert_contains("s1_pure_ocr_regime_b_6026", "6.026")

        # Stage 1 Regime B retrieval
        self.assert_contains("s1_regb_scan_off_0000", "0.000")
        self.assert_contains("s1_regb_scan_def_0875", "0.875")
        self.assert_contains("s1_regb_digital_ref_09167", "0.9167")
        self.assert_contains("s1_regb_delta_neg_00417", "-0.0417")

    def _audit_stage2_provenance_and_validation(self):
        s2_manifest = self.s2_results["manifest"]
        # Section 1.1 provenance
        self.assert_row("s2_data_git_sha_row", "Data Generation Git SHA", s2_manifest["data_git_sha"])
        self.assert_row("s2_working_tree_row", "Git Working Tree Status", "Clean on data generation", "git_dirty: false")
        self.assert_row("s2_run_status_row", "Run Status", "official")
        self.assert_row("reproducibility_archive_row", "Reproducibility Archive", "t19b-stage2-data", "e8d7e4fc92c6e7e2f3965be9ad1f0e3e325ab030f9d31bd2db667df6209f2b95")
        self.assert_row("temperature_row", "Generation Temperature", "0.0", "non-deterministic across sessions as shown by `q015`")
        self.assert_row("top_k_row", "Retrieved Context Top-K", "Top-K = 6")
        self.assert_row("prompt_sha_row", "System Prompt SHA-256", "3d7c8fe28584d70e188198b9cc72f740f813b70fd6b910f82ff363e01fe8265f")
        self.assert_row("cache_hit_rate_row", "Cache Hit Rate (Stage 2)", "1.2%")
        self.assert_row("failed_providers_row", "Failed Provider Count", "0", "16 transient first-attempt failures recovered on retry")

        # Section 2 ADR reference updates
        self.assert_row("adr_ref_pe1", "Primary Endpoint 1", "ADR 0010 §3 & §5")
        self.assert_row("adr_ref_pe2", "Primary Endpoint 2", "ADR 0010 §3 & §5")
        self.assert_row("adr_ref_regb", "Regime B Scanned Twin Benchmark", "ADR 0010 §2.2 & ADR 0011 §2.2")
        self.assert_row("adr_ref_ocr_lat", "Pure OCR Extractor Latency", "ADR 0009 §5")
        self.assert_row("adr_ref_cit", "Citation Precision & Page-Hit Audit", "ADR 0011 §7")
        self.assert_row("adr_ref_sec86", "54-Judgment Manual Correctness Audit", "Section 8.6 of this report")

        # Live validation cross-check
        self.assert_row("live_verification_agreement_row", "Live Verification Agreement", "36/36 (100%) pre-LLM gate agreement at $\\tau = 0.55$")
        self.assert_regex("s2_val_18_18_vector", r"18\s*/\s*18")
        self.assert_regex("s2_val_17_18_post_llm", r"17\s*/\s*18")
        self.assert_contains("s2_val_q015_variation", "q015")

    def _audit_primary_endpoints(self):
        # Section 5.1: Held-out Generalization Partition (N=64: 52 Answerable, 12 NID)
        self.assert_row("pe21_heldout_a1_ungated", "Arm 1 (Vector)", "ungated", "13.5%", "[6.7%, 25.3%]", "0.0%", "13.5%", "0.0%", "[0.0%, 24.2%]")
        self.assert_row("pe21_heldout_a1_prod055", "Arm 1 (Vector)", "prod_055", "15.4%", "[8.0%, 27.5%]", "1.9%", "13.5%", "0.0%", "[0.0%, 24.2%]")
        self.assert_row("pe21_heldout_a1_calib", "Arm 1 (Vector)", "calibrated_tau", "76.9%", "[63.9%, 86.3%]", "73.1%", "3.8%", "0.0%", "[0.0%, 24.2%]")
        self.assert_row("pe21_heldout_a2_ungated", "Arm 2 (Hybrid)", "ungated", "9.6%", "[4.2%, 20.6%]", "0.0%", "9.6%", "0.0%", "[0.0%, 24.2%]")
        self.assert_row("pe21_heldout_a2_prod055", "Arm 2 (Hybrid)", "prod_055", "11.5%", "[5.4%, 23.0%]", "1.9%", "9.6%", "0.0%", "[0.0%, 24.2%]")
        self.assert_row("pe21_heldout_a2_calib", "Arm 2 (Hybrid)", "calibrated_tau", "40.4%", "[28.2%, 53.9%]", "34.6%", "5.8%", "0.0%", "[0.0%, 24.2%]")
        self.assert_row("pe21_heldout_a3_ungated", "Arm 3 (Full System)", "ungated", "13.5%", "[6.7%, 25.3%]", "0.0%", "13.5%", "0.0%", "[0.0%, 24.2%]")
        self.assert_row("pe21_heldout_a3_prod055", "Arm 3 (Full System)", "prod_055", "15.4%", "[8.0%, 27.5%]", "3.8%", "11.5%", "0.0%", "[0.0%, 24.2%]")
        self.assert_row("pe21_heldout_a3_calib", "Arm 3 (Full System)", "calibrated_tau", "40.4%", "[28.2%, 53.9%]", "34.6%", "5.8%", "0.0%", "[0.0%, 24.2%]")

        # Section 5.1: Tuned Dev Partition (In-Sample Calibration, N=25: 18 Ans, 7 NID)
        self.assert_row("pe_dev_a1_ungated", "Arm 1 (Vector)", "ungated", "5.6%", "[1.0%, 25.8%]", "0.0%", "5.6%", "0.0%", "[0.0%, 35.4%]")
        self.assert_row("pe_dev_a1_prod055", "Arm 1 (Vector)", "prod_055", "5.6%", "[1.0%, 25.8%]", "0.0%", "5.6%", "0.0%", "[0.0%, 35.4%]")
        self.assert_row("pe_dev_a1_calib", "Arm 1 (Vector)", "calibrated_tau", "61.1%", "[38.6%, 79.7%]", "61.1%", "0.0%", "0.0%", "[0.0%, 35.4%]")
        self.assert_row("pe_dev_a2_ungated", "Arm 2 (Hybrid)", "ungated", "11.1%", "[3.1%, 32.8%]", "0.0%", "11.1%", "14.3%", "[2.6%, 51.3%]")
        self.assert_row("pe_dev_a2_prod055", "Arm 2 (Hybrid)", "prod_055", "11.1%", "[3.1%, 32.8%]", "0.0%", "11.1%", "14.3%", "[2.6%, 51.3%]")
        self.assert_row("pe_dev_a2_calib", "Arm 2 (Hybrid)", "calibrated_tau", "33.3%", "[16.3%, 56.3%]", "22.2%", "11.1%", "14.3%", "[2.6%, 51.3%]")
        self.assert_row("pe_dev_a3_ungated", "Arm 3 (Full System)", "ungated", "11.1%", "[3.1%, 32.8%]", "0.0%", "11.1%", "0.0%", "[0.0%, 35.4%]")
        self.assert_row("pe_dev_a3_prod055", "Arm 3 (Full System)", "prod_055", "11.1%", "[3.1%, 32.8%]", "0.0%", "11.1%", "0.0%", "[0.0%, 35.4%]")
        self.assert_row("pe_dev_a3_calib", "Arm 3 (Full System)", "calibrated_tau", "33.3%", "[16.3%, 56.3%]", "22.2%", "11.1%", "0.0%", "[0.0%, 35.4%]")

        # Section 5.1: Tuned Test Partition (N=49: 42 Ans, 7 NID)
        self.assert_row("pe_test_a1_ungated", "Arm 1 (Vector)", "ungated", "9.5%", "[3.8%, 22.1%]", "0.0%", "9.5%", "0.0%", "[0.0%, 35.4%]")
        self.assert_row("pe_test_a1_prod055", "Arm 1 (Vector)", "prod_055", "9.5%", "[3.8%, 22.1%]", "0.0%", "9.5%", "0.0%", "[0.0%, 35.4%]")
        self.assert_row("pe_test_a1_calib", "Arm 1 (Vector)", "calibrated_tau", "61.9%", "[46.8%, 75.0%]", "61.9%", "0.0%", "0.0%", "[0.0%, 35.4%]")
        self.assert_row("pe_test_a2_ungated", "Arm 2 (Hybrid)", "ungated", "9.5%", "[3.8%, 22.1%]", "0.0%", "9.5%", "0.0%", "[0.0%, 35.4%]")
        self.assert_row("pe_test_a2_prod055", "Arm 2 (Hybrid)", "prod_055", "9.5%", "[3.8%, 22.1%]", "0.0%", "9.5%", "0.0%", "[0.0%, 35.4%]")
        self.assert_row("pe_test_a2_calib", "Arm 2 (Hybrid)", "calibrated_tau", "33.3%", "[21.0%, 48.5%]", "28.6%", "4.8%", "0.0%", "[0.0%, 35.4%]")
        self.assert_row("pe_test_a3_ungated", "Arm 3 (Full System)", "ungated", "11.9%", "[5.2%, 25.0%]", "0.0%", "11.9%", "0.0%", "[0.0%, 35.4%]")
        self.assert_row("pe_test_a3_prod055", "Arm 3 (Full System)", "prod_055", "11.9%", "[5.2%, 25.0%]", "0.0%", "11.9%", "0.0%", "[0.0%, 35.4%]")
        self.assert_row("pe_test_a3_calib", "Arm 3 (Full System)", "calibrated_tau", "35.7%", "[23.0%, 50.8%]", "28.6%", "7.1%", "0.0%", "[0.0%, 35.4%]")

        # Section 5.1: Overall NID Table (N=26)
        self.assert_row("pe_nid_a1_ungated", "Arm 1 (Vector-Only)", "ungated", "26", "0", "0.0%", "[0.0%, 12.9%]", "0 LowSim / 26 ModRef")
        self.assert_row("pe_nid_a1_prod055", "Arm 1 (Vector-Only)", "prod_055", "26", "0", "0.0%", "[0.0%, 12.9%]", "4 LowSim / 22 ModRef")
        self.assert_row("pe_nid_a1_calib", "Arm 1 (Vector-Only)", "calibrated_tau", "26", "0", "0.0%", "[0.0%, 12.9%]", "25 LowSim / 1 ModRef")
        self.assert_row("pe_nid_a2_ungated", "Arm 2 (Hybrid, OCR-Off)", "ungated", "26", "1", "3.8%", "[0.7%, 18.9%]", "0 LowSim / 25 ModRef (1 FA on `q043`)")
        self.assert_row("pe_nid_a2_prod055", "Arm 2 (Hybrid, OCR-Off)", "prod_055", "26", "1", "3.8%", "[0.7%, 18.9%]", "3 LowSim / 22 ModRef (1 FA on `q043`)")
        self.assert_row("pe_nid_a2_calib", "Arm 2 (Hybrid, OCR-Off)", "calibrated_tau", "26", "1", "3.8%", "[0.7%, 18.9%]", "13 LowSim / 12 ModRef (1 FA on `q043`)")
        self.assert_row("pe_nid_a3_ungated", "Arm 3 (Full System)", "ungated", "26", "0", "0.0%", "[0.0%, 12.9%]", "0 LowSim / 26 ModRef")
        self.assert_row("pe_nid_a3_prod055", "Arm 3 (Full System)", "prod_055", "26", "0", "0.0%", "[0.0%, 12.9%]", "2 LowSim / 24 ModRef")
        self.assert_row("pe_nid_a3_calib", "Arm 3 (Full System)", "calibrated_tau", "26", "0", "0.0%", "[0.0%, 12.9%]", "12 LowSim / 14 ModRef")

    def _audit_vector_roc_auc(self):
        # Table 5.5: Vector-Component ROC-AUC Analysis
        self.assert_row("roc_auc_overall_row", "Overall", "0.7424", "[0.649, 0.836]", "[0.640, 0.837]", "0.7208", "[0.623, 0.818]", "[0.612, 0.821]")
        self.assert_row("roc_auc_heldout_row", "Held-out", "0.6250", "[0.459, 0.791]", "[0.449, 0.787]", "0.5641", "[0.388, 0.740]", "[0.375, 0.745]")

    def _audit_hybrid_vs_vector_and_counterfactual(self):
        # Section 6.1 McNemar p-values
        self.assert_contains("mcnemar_overall_ans_p1000", "exact McNemar **$p = 1.000$**")
        self.assert_contains("mcnemar_heldout_ans_p06875", "exact McNemar **$p = 0.6875$**")

        # Section 6.2 FTS-bypass counterfactual
        self.assert_contains("fts_counterfactual_with_346", "Held-out LowSimilarity FRR = **34.6%** (18/52)")
        self.assert_contains("fts_counterfactual_without_981", "Held-out LowSimilarity FRR = **98.1%** (51/52)")

    def _audit_null_experiment(self):
        # Section 7: Baseline Noise Floor
        self.assert_contains("null_exp_0_76_disagreements", "Answer Status Disagreements:** **0 / 76**")
        self.assert_contains("null_exp_ans_69_76", "both configurations answered 69/76")
        self.assert_contains("null_exp_kw_61_76", "both configurations achieved 61/76 keyword hits")
        self.assert_contains("null_exp_defect_false_0_83", "Answer status disagreements: **0 / 83**")
        self.assert_contains("null_exp_defect_false_kw_2_83", "Keyword hit disagreements: **2 / 83**")
        self.assert_contains("null_exp_diff_q090_q094", "`q090` and `q094`")

    def _audit_secondary_endpoint_heldout_cmap(self):
        # Table 8.1 Secondary Endpoint Metrics (Row-Scoped)
        self.assert_row("sec_tbl81_numbers", "Multi-digit Numbers ($N=7$)", "0 / 7 (0.0%)", "3 / 7 (42.9%)", "+42.9 pp", "p = 0.250")
        self.assert_row("sec_tbl81_unit_strict", "Unit Phrases Strict ($N=10$)", "1 / 10 (10.0%)", "2 / 10 (20.0%)", "+10.0 pp", "p = 1.000")
        self.assert_row("sec_tbl81_unit_prox", "Unit Phrases Proximity", "1 / 10 (10.0%)", "3 / 10 (30.0%)", "+20.0 pp", "p = 0.625")

        # Table 8.6 54-Judgment Manual Correctness Audit on heldout-CMap (Row-Scoped)
        self.assert_row("sec_tbl86_correct", "Correct", "16 / 27 (59.3%)", "15 / 27 (55.6%)", "-3.7 pp")
        self.assert_row("sec_tbl86_partial", "Partial (no error)", "1 / 27 (3.7%)", "3 / 27 (11.1%)", "+7.4 pp")
        self.assert_row("sec_tbl86_wrong", "Wrong (factual/numeric error)", "7 / 27 (25.9%)", "4 / 27 (14.8%)", "-11.1 pp", "p = 0.508")
        self.assert_row("sec_tbl86_refused", "Refused", "3 / 27 (11.1%)", "5 / 27 (18.5%)", "+7.4 pp")
        self.assert_row("sec_tbl86_cmap_subset", "CMap Digit-Corruption Subset", "7 / 27 (25.9%)", "1 / 27 (3.7%)", "-22.2 pp")

        # Table 8.5 Contextual Comparison Across Cohorts (Row-Scoped)
        self.assert_row("sec_tbl85_heldout_cmap", "`heldout-CMap` ($N=27, G=2$)", "0/7 (0.0%) vs. 3/7 (42.9%)", "1/10 (10.0%) vs. 2/10 (20.0%)", "1/10 (10.0%) vs. 3/10 (30.0%)", "16/27 (59.3%) vs. 15/27 (55.6%)")
        self.assert_row("sec_tbl85_primary_cmap", "`primary_cmap` ($N=36, G=6$)", "0/10 (0.0%) vs. 4/10 (40.0%)", "1/14 (7.1%) vs. 3/14 (21.4%)", "1/14 (7.1%) vs. 4/14 (28.6%)", "22/36 (61.1%) vs. 20/36 (55.6%)")
        self.assert_row("sec_tbl85_overall", "`overall` ($N=112$)", "25/41 (61.0%) vs. 30/41 (73.2%)", "17/36 (47.2%) vs. 17/36 (47.2%)", "19/36 (52.8%) vs. 22/36 (61.1%)", "83/112 (74.1%) vs. 81/112 (72.3%)")

    def _audit_frr_decomposition(self):
        # Section 9.1: Answerable FRR Decomposition at prod_055
        self.assert_contains("decomp_a1_tot_13", "Arm 1 ($Tot=13$):** `LowSimilarity = 1` (`q078`), `RetMiss = 3` (`q015, q034, q056`), `Gold-in-top-6 (defect=True) = 6` (`q039, q082, q083, q087, q100, q101`), `Gold-in-top-6 (defect=False) = 3` (`q058, q073, q098`)")
        self.assert_contains("decomp_a2_tot_12", "Arm 2 ($Tot=12$):** `LowSimilarity = 1` (`q078`), `RetMiss = 6` (`q028, q034, q039, q056, q074, q125`), `Gold-in-top-6 (defect=True) = 3` (`q087, q100, q101`), `Gold-in-top-6 (defect=False) = 2` (`q024, q058`)")
        self.assert_contains("decomp_a3_tot_15", "Arm 3 ($Tot=15$):** `LowSimilarity = 2` (`q078, q088`), `RetMiss = 5` (`q034, q039, q056, q074, q125`), `Gold-in-top-6 (defect=True) = 6` (`q014, q022, q087, q100, q101, q102`), `Gold-in-top-6 (defect=False) = 2` (`q024, q058`)")

    def _audit_question_types_and_citations(self):
        # Programmatic cell-level and row-scoped verification of ALL 6 rows × 3 arms of Table 9.3 computed directly from results.json at prod_055
        questions_by_id = {q["id"]: q for q in self.questions}
        table_counts = {}
        for arm_key in ["arm1_vector_ocr_off", "arm2_hybrid_ocr_off", "arm3_hybrid_ocr_defective"]:
            rows = self.s2_results["arms"][arm_key]
            counts_by_type = {}
            for r in rows:
                qid = r["id"]
                q = questions_by_id[qid]
                if q.get("type") == "not-in-docs":
                    continue
                qtype = q.get("type", "unknown")
                if qtype not in counts_by_type:
                    counts_by_type[qtype] = {"ans": 0, "kw": 0, "total": 0}
                counts_by_type[qtype]["total"] += 1

                score = r.get("bestVectorScore", 0.0)
                has_fts = r.get("hasFtsHit", False)
                is_hybrid = "hybrid" in arm_key
                low_sim = (score < 0.55) if not is_hybrid else (score < 0.55 and not has_fts)
                answered = (not low_sim) and (not r.get("notFound", False))
                kw_hit = (not low_sim) and r.get("kw_hit", False)
                if answered:
                    counts_by_type[qtype]["ans"] += 1
                if kw_hit:
                    counts_by_type[qtype]["kw"] += 1
            table_counts[arm_key] = counts_by_type

        # Verify all 6 rows of Table 9.3 with assert_row
        for t in ["numeric", "legal-reference", "paraphrase", "multi-chunk", "factual", "english"]:
            c1 = table_counts["arm1_vector_ocr_off"][t]
            c2 = table_counts["arm2_hybrid_ocr_off"][t]
            c3 = table_counts["arm3_hybrid_ocr_defective"][t]
            tot = c1["total"]
            self.assert_row(
                f"tbl93_{t}_row",
                f"`{t}`",
                str(tot),
                f"{c1['ans']}/{tot}",
                f"{c1['kw']}/{tot}",
                f"{c2['ans']}/{tot}",
                f"{c2['kw']}/{tot}",
                f"{c3['ans']}/{tot}",
                f"{c3['kw']}/{tot}",
            )

        # Table 9.4 Citation Metrics at prod_055 (Row-Scoped)
        self.assert_row("cit_tbl94_val", "Citation Validity Rate", "83.8% (83/99)", "83.0% (83/100)", "85.6% (83/97)")
        self.assert_row("cit_tbl94_page", "Citation Page-Hit Rate", "71.7% (71/99)", "73.0% (73/100)", "74.2% (72/97)")
        self.assert_row("cit_tbl94_prec", "Citation Precision (Mean)", "67.3%", "69.1%", "71.9%")
        self.assert_row("cit_tbl94_rec", "Citation Recall (Mean)", "67.7%", "67.5%", "68.6%")

    def _audit_regime_b_case_study(self):
        # Section 9.5 Regime B Case Study
        self.assert_contains("regb_scan_off_0_8", "**0 / 8 (0.0%) answered**")
        self.assert_contains("regb_scan_def_6_8", "**6 / 8 (75.0%) answered**")
        self.assert_contains("regb_digital_ref_8_8", "**8 / 8 (100.0%) answered**")

    def _audit_latencies(self):
        # Table 9.6 Live System Latency Profile (Row-Scoped)
        self.assert_row("lat_tbl96_a1", "Arm 1 (Vector-Only)", "193.5 ms / 360.9 ms", "1738.5 ms / 10495.8 ms", "2111.6 ms / 11713.7 ms")
        self.assert_row("lat_tbl96_a2", "Arm 2 (Hybrid)", "209.0 ms / 341.4 ms", "2917.0 ms / 20959.8 ms", "3258.1 ms / 21305.2 ms")
        self.assert_row("lat_tbl96_a3", "Arm 3 (Full System)", "204.0 ms / 274.4 ms", "2058.0 ms / 16643.5 ms", "2426.7 ms / 16895.9 ms")
        self.assert_row("lat_tbl96_regb", "Regime B Defective", "203.0 ms / 327.9 ms", "1459.5 ms / 3828.9 ms", "1746.4 ms / 4199.8 ms")

    def _audit_production_reliability(self):
        # Section 9.7 Reliability and Recovery
        self.assert_contains("rel_degraded_16_417", "**16 / 417 initial requests**")
        self.assert_contains("rel_recovered_16_16", "**16 / 16 recovery**")
        self.assert_contains("rel_unclassified_15", "the remaining 15 first-attempt degraded responses are unclassified (not broken down by HTTP 429/5xx/timeout in saved logs)")


def main():
    parser = argparse.ArgumentParser(description="Audit docs/EVAL-RESULTS.md numbers and hygiene.")
    parser.add_argument(
        "--doc",
        type=Path,
        default=REPO_ROOT / "docs" / "EVAL-RESULTS.md",
        help="Path to evaluation results document to audit.",
    )
    args = parser.parse_args()

    auditor = DocAuditor(args.doc)
    success = auditor.run_all_audits()
    if not success:
        sys.exit(1)
    sys.exit(0)


if __name__ == "__main__":
    main()
